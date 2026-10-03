using System.Text;
using System.Text.Json;

namespace MisaManager.Core;

/// <summary>Bounded local JSONL audit for workflow checkpoints; it never stores secrets or input payloads.</summary>
public sealed class RunEventStore
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private readonly string root;
    private readonly object gate = new();
    private readonly string stateRoot;
    private long sequence;

    public RunEventStore(string? rootPath = null)
    {
        root = Path.GetFullPath(rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.TechnicalName, "logs"));
        stateRoot = Directory.GetParent(root)!.FullName;
        StateFiles.RejectReparse(root);
        sequence = ReadLastSequence();
    }

    public string RootPath => root;

    public bool IsProtectedFile(string path)
    {
        if (Path.GetFileName(path).StartsWith("native-tab-", StringComparison.Ordinal)
            && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Path.GetFileName(path).StartsWith("events", StringComparison.Ordinal)
            || !path.EndsWith(".jsonl", StringComparison.Ordinal)) return true;
        try
        {
            StateFiles.RejectReparse(path);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(path))
            {
                var record = JsonSerializer.Deserialize<RunEvent>(line, ContractJson.Options);
                if (record is null || string.IsNullOrEmpty(record.RunId)) return true;
                ids.Add(record.RunId);
            }
            return ids.Count == 0 || ids.Any(IsUnresolvedRun);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Trace.TraceWarning("Protected unreadable audit: {0}", error.Message); return true; }
    }

    public DiagnosticsRetentionReport Prune()
    {
        lock (gate) return DiagnosticsRetention.Prune(root, IsProtectedFile);
    }

    public void Append(WorkflowContext context)
    {
        lock (gate)
        {
        var snapshot = context.Snapshot;
        if (snapshot.RunId.Length != 32 || !snapshot.RunId.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid audit run ID.");
        sequence = checked(sequence + 1);
        // Reserve the number durably before append; a crash may leave a gap but cannot reuse a sequence.
        StateFiles.WriteAtomic(Path.Combine(root, "sequence.json"), sequence);
        var record = new RunEvent(snapshot.RunId, sequence, DateTimeOffset.UtcNow,
            snapshot.PendingAction ?? snapshot.Status.ToString(), context.Invoice.InvoiceId, context.Invoice.Revision,
            snapshot.AppVersion, snapshot.SchemaVersion, snapshot.Status, snapshot.NativeFrame,
            snapshot.LastInputDisposition.ToString(), snapshot.LastErrorCode, snapshot.LastMessage ?? "Checkpoint persisted.",
            RequestId: snapshot.RequestId, OperationId: snapshot.OperationId, ProfileRevision: context.ProfileRevision,
            SourceEpoch: context.Invoice.Source.Sha256, SourceRevision: snapshot.RuntimeIdentity?.SourceRevision, ErrorChain: snapshot.ErrorChain);
        var line = JsonSerializer.Serialize(record, ContractJson.Compact) + Environment.NewLine;
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "events-" + snapshot.RunId + ".jsonl");
            StateFiles.RejectReparse(path);
            RotateIfNeeded(path);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.WriteThrough);
            // A crash mid-append leaves a torn last line; start on a fresh line so the next record stays parseable.
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n') line = Environment.NewLine + line;
            }
            stream.Seek(0, SeekOrigin.End);
            stream.Write(Encoding.UTF8.GetBytes(line));
            stream.Flush(true);
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxFileBytes) return;
        var archive = path + "." + Guid.NewGuid().ToString("N") + ".jsonl";
        File.Move(path, archive, overwrite: false);
    }

    private long ReadLastSequence()
    {
        try
        {
            if (!Directory.Exists(root)) return 0;
            var sequenceFile = Path.Combine(root, "sequence.json");
            StateFiles.RejectReparse(sequenceFile);
            var reserved = File.Exists(sequenceFile) ? JsonSerializer.Deserialize<long>(File.ReadAllText(sequenceFile)) : 0L;
            if (reserved < 0) throw new InvalidDataException("Invalid audit sequence watermark.");
            var max = reserved;
            foreach (var path in Directory.EnumerateFiles(root, "events*.jsonl"))
            {
                StateFiles.RejectReparse(path);
                foreach (var line in File.ReadLines(path))
                    try { max = Math.Max(max, JsonSerializer.Deserialize<RunEvent>(line, ContractJson.Options)?.Sequence ?? 0); }
                    catch (JsonException error) when (reserved > 0) { System.Diagnostics.Trace.TraceWarning("Protected malformed audit event: {0}", error.Message); }
            }
            return max;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new InvalidDataException("Cannot restore audit sequence; repair audit storage before writing.", error); }
    }

    private bool IsUnresolvedRun(string runId)
    {
        if (runId.Length != 32 || !runId.All(Uri.IsHexDigit)) return true;
        var candidates = new List<string>();
        void AddCheckpointPaths(string rootPath)
        {
            foreach (var relative in new[] { "session.json", Path.Combine("runs", runId, "workflow.json") })
            {
                var path = StateFiles.UnderRoot(rootPath, relative);
                if (File.Exists(path)) candidates.Add(path);
            }
        }
        AddCheckpointPaths(stateRoot);
        var generations = Path.Combine(stateRoot, "generations");
        StateFiles.RejectReparse(generations);
        if (Directory.Exists(generations))
        {
            // Checkpoints have fixed locations. Never traverse repair source
            // snapshots for every audit file during startup or diagnostics.
            foreach (var generation in Directory.EnumerateDirectories(generations))
            {
                StateFiles.RejectReparse(generation);
                AddCheckpointPaths(generation);
            }
        }
        var found = false;
        foreach (var path in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.GetProperty("schemaVersion").GetInt32() != ProductIdentity.StateSchemaVersion)
                    return true;
                var data = document.RootElement.GetProperty("data");
                var snapshot = data.GetProperty("snapshot");
                if (!string.Equals(snapshot.GetProperty("runId").GetString(), runId, StringComparison.Ordinal)) continue;
                found = true;
                var status = snapshot.GetProperty("status").GetString();
                if (snapshot.TryGetProperty("inputPending", out var pending) && pending.ValueKind == JsonValueKind.True
                    || status is not ("saved" or "discarded" or "cancelled" or "failed")) return true;
            }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            { System.Diagnostics.Trace.TraceWarning("Protected audit with unreadable checkpoint: {0}", error.Message); return true; }
        }
        return !found;
    }
}
