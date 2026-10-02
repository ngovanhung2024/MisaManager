using System.Text.Json;

namespace MisaManager.Core;

public sealed record GenerationPointer(string GenerationId, string AppVersion, int SchemaVersion, DateTimeOffset UpdatedAt);
public sealed record GenerationInfo(string GenerationId, string Path, string Status, string? PreviousGenerationId,
    DateTimeOffset CreatedAt, string AppVersion = "", int SchemaVersion = ProductIdentity.StateSchemaVersion,
    string? ArtifactManifestSha256 = null);

public sealed record GenerationRetentionReport(IReadOnlyList<string> RetainedGenerationIds,
    IReadOnlyList<string> RemovedGenerationIds, IReadOnlyList<string> SkippedGenerationIds);

public sealed class GenerationStore
{
    private readonly string root;
    private readonly object gate = new();
    public GenerationStore(string? rootPath = null)
        => root = Path.GetFullPath(rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.TechnicalName, "generations"));

    public string RootPath => root;
    public GenerationPointer? ReadActive()
    {
        var path = Path.Combine(root, "active.json");
        if (!File.Exists(path)) return null;
        StateFiles.RejectReparse(path);
        var pointer = JsonSerializer.Deserialize<GenerationPointer>(File.ReadAllText(path), ContractJson.Options)
            ?? throw new InvalidDataException("Empty active generation pointer.");
        var directory = StateFiles.GenerationPath(root, pointer.GenerationId);
        var info = ReadInfo(directory) ?? throw new InvalidDataException("Active generation metadata is missing.");
        if (pointer.SchemaVersion != ProductIdentity.StateSchemaVersion || info.SchemaVersion != pointer.SchemaVersion
            || info.GenerationId != pointer.GenerationId || info.AppVersion != pointer.AppVersion
            || info.Status is not ("active" or "prepared") || string.IsNullOrWhiteSpace(pointer.AppVersion))
            throw new InvalidDataException("Active generation is incompatible; controlled recovery is required.");
        if (info.Status == "prepared") ValidatePrepared(directory);
        return pointer;
    }

    public GenerationInfo Create(string appVersion, string? previousGenerationId = null)
    {
        lock (gate)
        {
            Directory.CreateDirectory(root);
            var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
            var path = Path.Combine(root, id);
            Directory.CreateDirectory(path);
            var info = new GenerationInfo(id, path, "prepared", previousGenerationId, DateTimeOffset.UtcNow,
                appVersion, ProductIdentity.StateSchemaVersion);
            WriteAtomic(Path.Combine(path, "generation.json"), info);
            return info;
        }
    }

    public GenerationPointer EnsureActive(string appVersion)
    {
        lock (gate)
        {
            var active = ReadActive();
            if (!Version.TryParse(appVersion, out var requestedVersion)) throw new InvalidDataException("Invalid application version.");
            if (active is not null && string.Equals(active.AppVersion, appVersion, StringComparison.Ordinal)) return active;
            if (active is not null)
            {
                if (!Version.TryParse(active.AppVersion, out var previousVersion) || requestedVersion < previousVersion)
                    throw new InvalidDataException("Use a verified binary/state rollback before starting an older application.");
                var sourcePath = StateFiles.GenerationPath(root, active.GenerationId);
                var target = Create(appVersion, active.GenerationId);
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var source in StateFiles.Files(sourcePath))
                {
                    var relative = Path.GetRelativePath(sourcePath, source).Replace('\\', '/');
                    if (relative is "generation.json" or "migration-journal.json") continue;
                    if (relative.EndsWith(".tmp", StringComparison.Ordinal)) throw new InvalidDataException("Pending state write requires recovery: " + relative);
                    var destination = StateFiles.UnderRoot(target.Path, relative);
                    var before = FileDigest.Sha256Async(source).GetAwaiter().GetResult();
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: false);
                    hashes[relative] = FileDigest.Sha256Async(destination).GetAwaiter().GetResult();
                    if (hashes[relative] != before || FileDigest.Sha256Async(source).GetAwaiter().GetResult() != before)
                        throw new InvalidDataException("State changed while preparing the binary upgrade.");
                }
                StateFiles.WriteAtomic(Path.Combine(target.Path, "migration-journal.json"), new MigrationJournal(
                    "MisaManager.GenerationCopy", ProductIdentity.StateSchemaVersion, ProductIdentity.StateSchemaVersion,
                    active.GenerationId, target.GenerationId, "prepared", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    [], PlanId: "copy-" + target.GenerationId, FileHashes: hashes));
                Activate(target, appVersion);
                return ReadActive()!;
            }
            // Never create a new generation when an active pointer is missing but state directories exist.
            if (Directory.Exists(root) && Directory.EnumerateDirectories(root).Any())
                throw new InvalidDataException("Generations exist without an active pointer; recover explicitly instead of resetting state.");
            var generation = Create(appVersion, active?.GenerationId);
            Activate(generation, appVersion);
            return ReadActive()!;
        }
    }

    public void Activate(GenerationInfo generation, string appVersion)
    {
        lock (gate)
        {
            var ownedPath = StateFiles.GenerationPath(root, generation.GenerationId);
            if (!string.Equals(ownedPath, Path.GetFullPath(generation.Path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Generation path does not match its identity.");
            if (!Directory.Exists(ownedPath)) throw new DirectoryNotFoundException(ownedPath);
            var marker = Path.Combine(generation.Path, "generation.json");
            var current = JsonSerializer.Deserialize<GenerationInfo>(File.ReadAllText(marker), ContractJson.Options);
            if (current?.GenerationId != generation.GenerationId || current.Status != "prepared") throw new InvalidDataException("Generation is not prepared.");
            if (current.SchemaVersion != ProductIdentity.StateSchemaVersion || !string.Equals(current.AppVersion, appVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Generation binary/state version pair is incompatible.");
            ValidatePrepared(ownedPath);
            WriteAtomic(Path.Combine(root, "active.json"), new GenerationPointer(generation.GenerationId, current.AppVersion,
                current.SchemaVersion, DateTimeOffset.UtcNow));
            WriteAtomic(marker, current with { Status = "active" });
        }
    }

    public GenerationPointer Rollback(string? expectedAppVersion = null)
    {
        lock (gate)
        {
            var active = ReadActive() ?? throw new InvalidOperationException("No active generation.");
            var previous = Directory.EnumerateDirectories(root)
                .Where(path => !Path.GetFileName(path).Equals(active.GenerationId, StringComparison.OrdinalIgnoreCase))
                .Select(path => new { Path = path, Id = Path.GetFileName(path), Info = ReadInfo(path) })
                .Where(item => item.Info?.Status == "active"
                    && item.Info.SchemaVersion == ProductIdentity.StateSchemaVersion
                    && !string.IsNullOrWhiteSpace(item.Info.AppVersion))
                .OrderByDescending(item => item.Info!.CreatedAt)
                .FirstOrDefault() ?? throw new InvalidOperationException("No compatible previous active generation.");
            var previousInfo = previous.Info ?? throw new InvalidOperationException("Previous generation metadata is missing.");
            if (!string.IsNullOrWhiteSpace(expectedAppVersion)
                && !string.Equals(previousInfo.AppVersion, expectedAppVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("No previous generation contains a compatible binary/state version pair.");
            var pointer = new GenerationPointer(previous.Id, previousInfo.AppVersion, previousInfo.SchemaVersion, DateTimeOffset.UtcNow);
            WriteAtomic(Path.Combine(root, "active.json"), pointer);
            var currentPath = Path.Combine(root, active.GenerationId, "generation.json");
            if (File.Exists(currentPath))
            {
                var current = JsonSerializer.Deserialize<GenerationInfo>(File.ReadAllText(currentPath), ContractJson.Options);
                if (current is not null) WriteAtomic(currentPath, current with { Status = "rolled_back" });
            }
            return pointer;
        }
    }

    public GenerationRetentionReport Retain(int maximumGenerations = 2, TimeSpan? minimumAge = null,
        Func<string, bool>? hasUnresolvedRun = null)
    {
        if (maximumGenerations < 1) throw new ArgumentOutOfRangeException(nameof(maximumGenerations));
        lock (gate)
        {
            if (!Directory.Exists(root)) return new([], [], []);
            var active = ReadActive();
            var generations = Directory.EnumerateDirectories(root)
                .Select(path => new { Path = path, Info = ReadInfo(path) })
                .Where(item => item.Info is not null)
                .Select(item => item.Info!)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray();
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (active is not null) retained.Add(active.GenerationId);
            foreach (var generation in generations.Where(item => item.Status == "active").Take(maximumGenerations)) retained.Add(generation.GenerationId);
            var removed = new List<string>();
            var skipped = new List<string>();
            var cutoff = DateTimeOffset.UtcNow - (minimumAge ?? TimeSpan.Zero);
            foreach (var generation in generations)
            {
                if (retained.Contains(generation.GenerationId) || generation.CreatedAt > cutoff ||
                    hasUnresolvedRun?.Invoke(generation.GenerationId) == true)
                {
                    if (!retained.Contains(generation.GenerationId)) skipped.Add(generation.GenerationId);
                    continue;
                }
                var owned = StateFiles.GenerationPath(root, generation.GenerationId);
                if (!string.Equals(owned, Path.GetFullPath(generation.Path), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Generation metadata path mismatch.");
                _ = StateFiles.Files(owned).ToArray();
                Directory.Delete(owned, recursive: true);
                removed.Add(generation.GenerationId);
            }
            return new(retained.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(), removed, skipped);
        }
    }

    /// <summary>Conservatively protects generations that still contain a recoverable workflow checkpoint.</summary>
    public bool HasUnresolvedRun(string generationId)
    {
        if (string.IsNullOrWhiteSpace(generationId) || generationId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || generationId.Contains(Path.DirectorySeparatorChar) || generationId.Contains(Path.AltDirectorySeparatorChar))
            return true;
        var generationPath = Path.Combine(root, generationId);
        if (!Directory.Exists(generationPath)) return true;
        try { _ = StateFiles.Files(generationPath).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { System.Diagnostics.Trace.TraceWarning("Protected unresolved generation: {0}", error.Message); return true; }
        var candidates = new List<string>();
        foreach (var sessionName in new[] { "session.json", "session-live-test.json" })
        {
            var session = Path.Combine(generationPath, sessionName);
            if (File.Exists(session)) candidates.Add(session);
        }
        var runs = Path.Combine(generationPath, "runs");
        if (Directory.Exists(runs)) candidates.AddRange(Directory.EnumerateFiles(runs, "workflow.json", SearchOption.AllDirectories));
        foreach (var path in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var rootNode = document.RootElement;
                if (!rootNode.TryGetProperty("documentType", out var type) || type.GetString() is not ("workflow-session" or "workflow-context")
                    || !rootNode.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != ProductIdentity.StateSchemaVersion
                    || !rootNode.TryGetProperty("data", out var data) || !data.TryGetProperty("snapshot", out var snapshot))
                    return true;
                if (snapshot.TryGetProperty("inputPending", out var pending) && pending.ValueKind == JsonValueKind.True)
                    return true;
                if (!snapshot.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                    return true;
                if (status.GetString()?.ToLowerInvariant() is not ("saved" or "discarded")) return true;
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Protected unresolved generation: {0}", error.Message); return true; }
        }
        return false;
    }

    private static GenerationInfo? ReadInfo(string path)
    {
        var file = Path.Combine(path, "generation.json");
        StateFiles.RejectReparse(file);
        var info = File.Exists(file) ? JsonSerializer.Deserialize<GenerationInfo>(File.ReadAllText(file), ContractJson.Options) : null;
        if (info is not null && (!string.Equals(Path.GetFullPath(info.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            || info.GenerationId != Path.GetFileName(path))) throw new InvalidDataException("Generation identity/path mismatch.");
        return info;
    }

    private static void ValidatePrepared(string path)
    {
        var journal = Path.Combine(path, "migration-journal.json");
        if (File.Exists(journal)) StateMigrationCoordinator.ValidatePrepared(path);
        else if (StateFiles.Files(path).Any(file => Path.GetFileName(file) != "generation.json"))
            throw new InvalidDataException("A nonempty prepared generation requires a validated migration journal.");
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        StateFiles.WriteAtomic(path, value);
    }
}
