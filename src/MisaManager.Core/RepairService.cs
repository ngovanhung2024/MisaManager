using System.Collections.Immutable;
using System.Text.Json;

namespace MisaManager.Core;

public sealed record RepairBaseline(string IncidentId, string SessionId, string SourceRoot, DateTimeOffset CreatedAt,
    ImmutableDictionary<string, string> Files, string InvoiceFingerprint = "");
public sealed record RepairChange(string Path, string Kind, string? Before, string? After);
public sealed record RepairHandoff(string IncidentId, string Status, string ReleaseRevision, DateTimeOffset CompletedAt,
    IReadOnlyList<RepairChange> ChangedFiles, string Message, string RootCause, IReadOnlyList<string> AffectedCallers,
    bool RestartRequired, string ReleaseDirectory = "", string ManifestSha256 = "", string AppVersion = "");
public sealed record RepairRestartAcknowledgement(string IncidentId, string SessionId, string ReleaseRevision,
    string AppVersion, string ManifestSha256, string CandidateExecutableSha256, string WindowIdentity,
    DateTimeOffset AcknowledgedAt, string Status = "candidate_rebound");

public sealed class RepairService(IStateStore state)
{
    public async Task<RepairBaseline> BeginAsync(string incidentId, string sessionId, string sourceRoot, CancellationToken token)
    {
        CheckId(incidentId);
        var incident = await state.ReadAsync<WorkflowContext>("workflow-session", "session.json", token)
            ?? throw new InvalidOperationException("Không có incident/run được app ghi nhận.");
        if (incident.Snapshot.RunId != incidentId
            || incident.Snapshot.Status is not (WorkflowStatus.Blocked or WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain or WorkflowStatus.Failed))
            throw new InvalidOperationException("Run chưa ở checkpoint lỗi.");
        var existing = await state.ReadAsync<RepairBaseline>("repair-baseline", $"repairs/{incidentId}/baseline.json", token);
        if (existing is not null)
        {
            if (existing.SessionId != sessionId)
            {
                // A completed handoff/restart means the original bridge lease is no longer editing.
                // Rebind the baseline to the current bridge so a stale MCP session cannot strand recovery.
                var handoff = await state.ReadAsync<RepairHandoff>("repair-handoff", $"repairs/{incidentId}/handoff.json", token);
                var restart = await state.ReadAsync<RepairRestartAcknowledgement>("repair-restart", $"repairs/{incidentId}/restart.json", token);
                if (handoff?.IncidentId != incidentId || handoff.Status != "source_ready" || !handoff.RestartRequired
                    || restart?.IncidentId != incidentId || restart.Status != "candidate_rebound")
                    throw new InvalidOperationException("Incident đang được phiên repair khác giữ.");
                existing = existing with { SessionId = sessionId };
                await state.WriteAsync("repair-baseline", $"repairs/{incidentId}/baseline.json", existing, token);
            }
            if (existing.InvoiceFingerprint != incident.Invoice.Fingerprint
                || !string.Equals(existing.SourceRoot, Path.GetFullPath(sourceRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Repair baseline khác nguồn hoặc hóa đơn hiện tại.");
            return existing;
        }
        var root = Path.GetFullPath(sourceRoot);
        if (!File.Exists(Path.Combine(root, "MisaManager.sln"))) throw new InvalidDataException("Source root không có solution MisaManager.");
        var files = await InventoryAsync(root, token);
        var baseline = new RepairBaseline(incidentId, sessionId, root, DateTimeOffset.UtcNow, files, incident.Invoice.Fingerprint);
        foreach (var file in files)
        {
            var target = Path.Combine(state.RootPath, "repairs", incidentId, "source", file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(Path.Combine(root, file.Key), target, false);
            if (await FileDigest.Sha256Async(target, token) != file.Value)
                throw new IOException("Source thay đổi trong lúc snapshot: " + file.Key);
        }
        await state.WriteAsync("repair-baseline", $"repairs/{incidentId}/baseline.json", baseline, token);
        return baseline;
    }

    public async Task<RepairHandoff> CompleteAsync(string incidentId, string sessionId, string releaseDirectory,
        string rootCause, IReadOnlyList<string> affectedCallers, string message, CancellationToken token)
    {
        CheckId(incidentId);
        var baseline = await state.ReadAsync<RepairBaseline>("repair-baseline", $"repairs/{incidentId}/baseline.json", token)
            ?? throw new InvalidOperationException("Gọi begin_repair trước khi sửa source.");
        if (baseline.SessionId != sessionId) throw new InvalidOperationException("Session không giữ incident.");
        var incident = await state.ReadAsync<WorkflowContext>("workflow-session", "session.json", token);
        if (incident?.Snapshot.RunId != incidentId || incident.Invoice.Fingerprint != baseline.InvoiceFingerprint
            || incident.Snapshot.Status is not (WorkflowStatus.Blocked or WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain or WorkflowStatus.Failed))
            throw new InvalidOperationException("Workflow không còn ở incident/revision đã bind.");
        if (string.IsNullOrWhiteSpace(rootCause) || string.IsNullOrWhiteSpace(message) || affectedCallers.Count == 0)
            throw new ArgumentException("Cần root cause, affected callers và message để ghi handoff.");
        var current = await InventoryAsync(baseline.SourceRoot, token);
        var changes = baseline.Files.Keys.Union(current.Keys).OrderBy(key => key, StringComparer.Ordinal)
            .Where(key => baseline.Files.GetValueOrDefault(key) != current.GetValueOrDefault(key))
            .Select(key => new RepairChange(key, !baseline.Files.ContainsKey(key) ? "added" : !current.ContainsKey(key) ? "removed" : "modified",
                baseline.Files.GetValueOrDefault(key), current.GetValueOrDefault(key))).ToArray();
        var release = Path.GetFullPath(releaseDirectory);
        var manifestPath = Path.Combine(release, "release-manifest.json");
        var manifestHash = await FileDigest.Sha256Async(manifestPath, token);
        using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, token));
        var document = manifest.RootElement;
        if (document.GetProperty("documentType").GetString() != "MisaManager.ReleaseManifest"
            || document.GetProperty("schemaVersion").GetInt32() != 1
            || document.GetProperty("target").GetString() != "win-x64"
            || document.GetProperty("stateSchemaVersion").GetInt32() != ProductIdentity.StateSchemaVersion
            || document.GetProperty("verification").GetString() != "build_verified")
            throw new InvalidDataException("Cần manifest từ build/package thực.");
        var captured = document.GetProperty("source").GetProperty("files");
        foreach (var file in current)
        {
            if (!captured.TryGetProperty(file.Key.Replace('\\', '/'), out var value) || value.GetString() != file.Value
                || await FileDigest.Sha256Async(Path.Combine(release, "source", file.Key), token) != file.Value)
                throw new InvalidDataException("Release chưa chứa source hiện tại: " + file.Key);
        }
        foreach (var file in document.GetProperty("files").EnumerateObject())
        {
            var path = Path.GetFullPath(Path.Combine(release, "payload", file.Name));
            if (!path.StartsWith(Path.Combine(release, "payload") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || await FileDigest.Sha256Async(path, token) != file.Value.GetString())
                throw new InvalidDataException("Payload hash không khớp manifest.");
        }
        // A deleted implementation must not survive in the candidate source snapshot.
        foreach (var file in captured.EnumerateObject())
            if (baseline.Files.ContainsKey(file.Name) && !current.ContainsKey(file.Name))
                throw new InvalidDataException("Release vẫn chứa source đã xóa: " + file.Name);
        if (!document.GetProperty("files").TryGetProperty("app/MisaManager.App.exe", out _)
            || await FileDigest.Sha256Async(manifestPath, token) != manifestHash)
            throw new InvalidDataException("Release thiếu app hoặc manifest thay đổi khi kiểm tra.");
        var result = new RepairHandoff(incidentId, "source_ready", document.GetProperty("revision").GetString()!, DateTimeOffset.UtcNow,
            changes, message, rootCause, affectedCallers, true, release, manifestHash, document.GetProperty("version").GetString()!);
        await state.WriteAsync("repair-handoff", $"repairs/{incidentId}/handoff.json", result, token);
        return result;
    }

    public async Task<RepairRestartAcknowledgement> AcknowledgeRestartAsync(string incidentId, string sessionId,
        string releaseDirectory, string windowIdentity, CancellationToken token)
    {
        CheckId(incidentId);
        if (string.IsNullOrWhiteSpace(windowIdentity)) throw new ArgumentException("Window identity is required.", nameof(windowIdentity));
        var handoff = await state.ReadAsync<RepairHandoff>("repair-handoff", $"repairs/{incidentId}/handoff.json", token)
            ?? throw new InvalidOperationException("Chưa có repair handoff để xác nhận restart.");
        var baseline = await state.ReadAsync<RepairBaseline>("repair-baseline", $"repairs/{incidentId}/baseline.json", token)
            ?? throw new InvalidOperationException("Repair baseline is missing.");
        if (!string.Equals(baseline.SessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Session does not hold the repair incident.");
        if (!string.Equals(handoff.IncidentId, incidentId, StringComparison.Ordinal) || handoff.Status != "source_ready"
            || !handoff.RestartRequired || !string.Equals(handoff.ReleaseDirectory, Path.GetFullPath(releaseDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Repair handoff không yêu cầu candidate restart.");
        var release = Path.GetFullPath(releaseDirectory);
        var manifestPath = Path.Combine(release, "release-manifest.json");
        var manifestSha = await FileDigest.Sha256Async(manifestPath, token);
        if (!string.Equals(manifestSha, handoff.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release manifest không khớp repair handoff.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, token));
        var version = manifest.RootElement.GetProperty("version").GetString() ?? "";
        var currentVersion = typeof(RepairService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        if (!string.Equals(version, currentVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Candidate app version không khóp release handoff.");
        var executable = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Không xác định được candidate executable."));
        var relativeExecutable = Path.GetRelativePath(Path.Combine(release, "payload"), executable).Replace('\\', '/');
        if (!string.Equals(relativeExecutable, "app/MisaManager.App.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Candidate process không chạy từ app payload của release.");
        var executableSha = await FileDigest.Sha256Async(executable, token);
        if (!manifest.RootElement.GetProperty("files").TryGetProperty(relativeExecutable, out var expected)
            || !string.Equals(expected.GetString(), executableSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Candidate executable hash không khớp release manifest.");
        var acknowledgement = new RepairRestartAcknowledgement(incidentId, sessionId, handoff.ReleaseRevision,
            version, manifestSha, executableSha, windowIdentity, DateTimeOffset.UtcNow);
        await state.WriteAsync("repair-restart", $"repairs/{incidentId}/restart.json", acknowledgement, token);
        return acknowledgement;
    }

    public async Task<RepairRestartAcknowledgement> ReadRestartAcknowledgementAsync(string incidentId,
        string sessionId, CancellationToken token)
    {
        CheckId(incidentId);
        var acknowledgement = await state.ReadAsync<RepairRestartAcknowledgement>("repair-restart",
            $"repairs/{incidentId}/restart.json", token)
            ?? throw new InvalidOperationException("Candidate restart has not been acknowledged.");
        if (!string.Equals(acknowledgement.IncidentId, incidentId, StringComparison.Ordinal)
            || !string.Equals(acknowledgement.SessionId, sessionId, StringComparison.Ordinal)
            || acknowledgement.Status != "candidate_rebound")
            throw new InvalidOperationException("Candidate restart acknowledgement does not match this session.");
        return acknowledgement;
    }

    private static async Task<ImmutableDictionary<string, string>> InventoryAsync(string root, CancellationToken token)
    {
        var files = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var folder in new[] { "src", "profiles", "installer", "scripts" })
        {
            var pending = new Stack<string>();
            pending.Push(Path.Combine(root, folder));
            while (pending.TryPop(out var directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Source reparse point bị chặn.");
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var info = File.GetAttributes(path);
                    if ((info & FileAttributes.ReparsePoint) != 0) throw new IOException("Source reparse point bị chặn.");
                    if ((info & FileAttributes.Directory) != 0)
                    {
                        var name = Path.GetFileName(path);
                        if (name is "bin" or "obj" || folder == "src" && !name.StartsWith("MisaManager.", StringComparison.Ordinal)
                            && Path.GetDirectoryName(path) == Path.Combine(root, "src")) continue;
                        pending.Push(path);
                    }
                    else files[Path.GetRelativePath(root, path).Replace('\\', '/')] = await FileDigest.Sha256Async(path, token);
                }
            }
        }
        foreach (var name in new[] { "MisaManager.sln", "global.json", "Directory.Build.props", "Directory.Packages.props" })
            files[name] = await FileDigest.Sha256Async(Path.Combine(root, name), token);
        return files.ToImmutable();
    }
    private static void CheckId(string value)
    {
        if (value.Length != 32 || value.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Incident ID phải là runId do app tạo.");
    }
}
