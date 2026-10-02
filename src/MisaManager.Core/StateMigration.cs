using System.Collections.Immutable;
using System.Text.Json;

namespace MisaManager.Core;

public interface IStateMigration
{
    string DocumentType { get; }
    int FromVersion { get; }
    int ToVersion { get; }
    object Migrate(object source);
}

public sealed class MigrationRegistry(IEnumerable<IStateMigration> migrations)
{
    private readonly IReadOnlyList<IStateMigration> migrations = migrations.ToArray();

    public object Migrate(string documentType, int fromVersion, int toVersion, object value)
    {
        if (fromVersion > toVersion) throw new InvalidDataException("Không hỗ trợ hạ schema state bằng cách sửa version.");
        var current = fromVersion;
        var result = value;
        while (current < toVersion)
        {
            var next = migrations.SingleOrDefault(m => m.DocumentType == documentType && m.FromVersion == current);
            if (next is null) throw new InvalidDataException($"Thiếu migration {documentType} {current}->{current + 1}.");
            if (next.ToVersion != current + 1 || next.ToVersion > toVersion)
                throw new InvalidDataException("Migration phải tăng đúng một schema version.");
            result = next.Migrate(result);
            current = next.ToVersion;
        }
        return result;
    }

    public MigrationExecution MigrateWithJournal(string documentType, string relativePath, int fromVersion,
        int toVersion, JsonElement value, string sourceSha256)
    {
        if (string.IsNullOrWhiteSpace(documentType) || string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Migration document identity is required.");
        if (sourceSha256.Length != 64 || sourceSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Migration source checksum is required.", nameof(sourceSha256));
        var migrated = Migrate(documentType, fromVersion, toVersion, value);
        var output = migrated is JsonElement element
            ? element.Clone()
            : JsonSerializer.SerializeToElement(migrated, ContractJson.Options);
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(output, ContractJson.Compact);
        return new MigrationExecution(documentType, relativePath.Replace('\\', '/'), fromVersion, toVersion,
            sourceSha256.ToLowerInvariant(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(outputBytes)).ToLowerInvariant(), output,
            DateTimeOffset.UtcNow);
    }
}

public sealed record MigrationExecution(string DocumentType, string RelativePath, int FromVersion, int ToVersion,
    string SourceSha256, string OutputSha256, JsonElement Data, DateTimeOffset CompletedAt);

public sealed record MigrationJournal(string DocumentType, int FromVersion, int ToVersion, string SourceGenerationId,
    string TargetGenerationId, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    IReadOnlyList<MigrationExecution> Documents, string? Error = null, string? PlanId = null,
    IReadOnlyDictionary<string, string>? FileHashes = null, string? BackupPath = null);

public sealed record MigrationPlan(string PlanId, string SourceGenerationId, string AppVersion, int FromVersion,
    int ToVersion, IReadOnlyList<MigrationExecution> Documents, IReadOnlyDictionary<string, string> SourceFiles,
    bool DryRun = true);

/// <summary>Coordinates multi-file state migration inside a prepared generation.</summary>
public sealed class StateMigrationCoordinator
{
    private readonly MigrationRegistry registry;
    private readonly GenerationStore generations;
    private readonly SemaphoreSlim gate = new(1, 1);

    public StateMigrationCoordinator(MigrationRegistry registry, GenerationStore generations)
    {
        this.registry = registry;
        this.generations = generations;
    }
    public async Task<(GenerationInfo Generation, MigrationJournal Journal)> PrepareAsync(
        string appVersion, int fromVersion, int toVersion, IReadOnlyList<MigrationExecution> documents,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var plan = await DryRunAsync(appVersion, fromVersion, toVersion, documents, cancellationToken);
            var candidates = Directory.EnumerateDirectories(generations.RootPath).Select(path => Path.Combine(path, "migration-journal.json"))
                .Where(File.Exists).Select(path => JsonSerializer.Deserialize<MigrationJournal>(File.ReadAllText(path), ContractJson.Options))
                .Where(journal => journal?.PlanId == plan.PlanId).ToArray();
            if (candidates.Length > 1) throw new InvalidDataException("More than one generation owns this migration plan.");
            var previous = candidates.SingleOrDefault();
            var generation = previous is null ? generations.Create(appVersion, plan.SourceGenerationId)
                : JsonSerializer.Deserialize<GenerationInfo>(File.ReadAllText(StateFiles.UnderRoot(generations.RootPath,
                    previous.TargetGenerationId + "/generation.json")), ContractJson.Options)!;
            if (previous?.Status == "prepared") { ValidatePrepared(generation.Path); return (generation, previous); }
            var source = StateFiles.GenerationPath(generations.RootPath, plan.SourceGenerationId);
            var backup = StateFiles.UnderRoot(Path.Combine(Path.GetDirectoryName(generations.RootPath)!, "backups"), "migration-" + generation.GenerationId);
            var started = previous?.StartedAt ?? DateTimeOffset.UtcNow;
            var pending = new MigrationJournal("MisaManager.State", fromVersion, toVersion, plan.SourceGenerationId,
                generation.GenerationId, "pending", started, null, documents, PlanId: plan.PlanId, BackupPath: backup);
            WriteJournal(generation.Path, pending);
            try
            {
                foreach (var entry in plan.SourceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var original = StateFiles.UnderRoot(source, entry.Key);
                    if (await FileDigest.Sha256Async(original, cancellationToken) != entry.Value)
                        throw new InvalidDataException("Source changed during migration: " + entry.Key);
                    foreach (var destinationRoot in new[] { backup, generation.Path })
                    {
                        var destination = StateFiles.UnderRoot(destinationRoot, entry.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        if (destinationRoot == backup && File.Exists(destination))
                        {
                            if (await FileDigest.Sha256Async(destination, cancellationToken) != entry.Value)
                                throw new InvalidDataException("Migration backup checksum mismatch.");
                        }
                        else File.Copy(original, destination, overwrite: true);
                        if (await FileDigest.Sha256Async(destination, cancellationToken) != entry.Value)
                            throw new InvalidDataException("Migration copy checksum mismatch.");
                    }
                }
                StateFiles.WriteAtomic(Path.Combine(backup, "backup-manifest.json"), plan.SourceFiles);
                foreach (var document in documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = StateFiles.UnderRoot(generation.Path, document.RelativePath);
                    using var old = JsonDocument.Parse(await File.ReadAllTextAsync(StateFiles.UnderRoot(source, document.RelativePath), cancellationToken));
                    var created = old.RootElement.GetProperty("createdAt").GetDateTimeOffset();
                    var envelope = new StateEnvelope<JsonElement>(document.DocumentType, document.ToVersion, appVersion,
                        created, DateTimeOffset.UtcNow, document.Data, ProductIdentity.SourceRevision, null,
                        new RuntimeStateIdentity(appVersion, ProductIdentity.SourceRevision, ""));
                    await WriteAtomicAsync(target, envelope, cancellationToken);
                }
                var outputHashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var key in plan.SourceFiles.Keys)
                    outputHashes[key] = await FileDigest.Sha256Async(StateFiles.UnderRoot(generation.Path, key), cancellationToken);
                var completed = pending with { Status = "prepared", CompletedAt = DateTimeOffset.UtcNow, FileHashes = outputHashes };
                WriteJournal(generation.Path, completed);
                ValidatePrepared(generation.Path);
                return (generation, completed);
            }
            catch (Exception error)
            {
                WriteJournal(generation.Path, pending with { Status = "failed", CompletedAt = DateTimeOffset.UtcNow, Error = error.ToString() });
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<MigrationPlan> DryRunAsync(string appVersion, int fromVersion, int toVersion,
        IReadOnlyList<MigrationExecution> documents, CancellationToken token = default)
    {
        if (documents.Count == 0 || !Version.TryParse(appVersion, out _) || fromVersion < 1 || toVersion < fromVersion
            || toVersion != ProductIdentity.StateSchemaVersion) throw new InvalidDataException("Invalid migration version/documents.");
        var active = generations.ReadActive() ?? throw new InvalidDataException("No active generation to migrate.");
        if (generations.HasUnresolvedRun(active.GenerationId)) throw new InvalidOperationException("Resolve the active workflow before migrating.");
        var source = StateFiles.GenerationPath(generations.RootPath, active.GenerationId);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in StateFiles.Files(source))
        {
            var key = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (key is "generation.json" or "migration-journal.json") continue;
            if (key.EndsWith(".tmp", StringComparison.Ordinal)) throw new InvalidDataException("Unresolved state temporary file: " + key);
            hashes[key] = await FileDigest.Sha256Async(file, token);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            ValidateRelativePath(doc.RelativePath);
            if (!seen.Add(doc.RelativePath) || !hashes.TryGetValue(doc.RelativePath, out var sourceHash) || sourceHash != doc.SourceSha256
                || doc.FromVersion != fromVersion || doc.ToVersion != toVersion)
                throw new InvalidDataException("Migration source/version/hash mismatch: " + doc.RelativePath);
            using var original = JsonDocument.Parse(await File.ReadAllTextAsync(StateFiles.UnderRoot(source, doc.RelativePath), token));
            if (original.RootElement.GetProperty("documentType").GetString() != doc.DocumentType
                || original.RootElement.GetProperty("schemaVersion").GetInt32() != doc.FromVersion
                || DataHash(doc.Data) != doc.OutputSha256) throw new InvalidDataException("Invalid migration document: " + doc.RelativePath);
        }
        var planBytes = JsonSerializer.SerializeToUtf8Bytes(new { active.GenerationId, appVersion, fromVersion, toVersion,
            hashes, outputs = documents.OrderBy(d => d.RelativePath, StringComparer.Ordinal).Select(d => new { d.RelativePath, d.OutputSha256 }) }, ContractJson.Compact);
        return new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(planBytes)).ToLowerInvariant(),
            active.GenerationId, appVersion, fromVersion, toVersion, documents, hashes);
    }

    public Task<(GenerationInfo Generation, MigrationJournal Journal)> PrepareFromRawAsync(
        string appVersion, string documentType, int fromVersion, int toVersion,
        IReadOnlyList<(string RelativePath, JsonElement Data, string SourceSha256)> documents,
        CancellationToken cancellationToken = default)
    {
        var executions = documents.Select(document => registry.MigrateWithJournal(documentType, document.RelativePath,
            fromVersion, toVersion, document.Data, document.SourceSha256)).ToArray();
        return PrepareAsync(appVersion, fromVersion, toVersion, executions, cancellationToken);
    }

    public void Activate(GenerationInfo generation, string appVersion, MigrationJournal journal)
    {
        if (journal.Status != "prepared" || journal.TargetGenerationId != generation.GenerationId)
            throw new InvalidDataException("Migration journal is not prepared for this generation.");
        if (journal.ToVersion != ProductIdentity.StateSchemaVersion || journal.Status != "prepared")
            throw new InvalidDataException("Migration target schema is not supported by this binary.");
        ValidatePrepared(generation.Path);
        var recorded = JsonSerializer.Deserialize<MigrationJournal>(File.ReadAllText(Path.Combine(generation.Path, "migration-journal.json")), ContractJson.Options)!;
        if (recorded.PlanId != journal.PlanId || generations.ReadActive()?.GenerationId != recorded.SourceGenerationId)
            throw new InvalidDataException("Migration source pointer or plan changed before activation.");
        var plan = DryRunAsync(appVersion, journal.FromVersion, journal.ToVersion, recorded.Documents).GetAwaiter().GetResult();
        if (plan.PlanId != recorded.PlanId) throw new InvalidDataException("Source changed after preparation.");
        generations.Activate(generation, appVersion);
    }

    internal static void ValidatePrepared(string generationPath)
    {
        var journalPath = Path.Combine(generationPath, "migration-journal.json");
        if (!File.Exists(journalPath)) throw new InvalidDataException("Prepared generation has no migration journal.");
        var journal = JsonSerializer.Deserialize<MigrationJournal>(File.ReadAllText(journalPath), ContractJson.Options)
            ?? throw new InvalidDataException("Migration journal is empty.");
        if (journal.Status != "prepared" || journal.TargetGenerationId != Path.GetFileName(generationPath))
            throw new InvalidDataException("Migration journal is not prepared for this generation.");
        if (journal.FileHashes is null) throw new InvalidDataException("Migration output manifest is missing.");
        var files = StateFiles.Files(generationPath).Where(p => Path.GetFileName(p) is not ("generation.json" or "migration-journal.json")).ToArray();
        if (files.Length != journal.FileHashes.Count) throw new InvalidDataException("Migration file set changed.");
        foreach (var file in files)
        {
            var key = Path.GetRelativePath(generationPath, file).Replace('\\', '/');
            if (!journal.FileHashes.TryGetValue(key, out var expected) || FileDigest.Sha256Async(file).GetAwaiter().GetResult() != expected)
                throw new InvalidDataException("Migration file checksum mismatch: " + key);
        }
        foreach (var document in journal.Documents)
        {
            ValidateRelativePath(document.RelativePath);
            var path = StateFiles.UnderRoot(generationPath, document.RelativePath);
            if (!File.Exists(path)) throw new InvalidDataException("Migration document is missing: " + document.RelativePath);
            var envelope = JsonSerializer.Deserialize<StateEnvelope<JsonElement>>(File.ReadAllText(path), ContractJson.Options)
                ?? throw new InvalidDataException("Migration document is empty: " + document.RelativePath);
            if (envelope.DocumentType != document.DocumentType || envelope.SchemaVersion != document.ToVersion)
                throw new InvalidDataException("Migration document envelope mismatch: " + document.RelativePath);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Data, ContractJson.Compact);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, document.OutputSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Migration document checksum mismatch: " + document.RelativePath);
        }
    }

    private static string DataHash(JsonElement data)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data, ContractJson.Compact))).ToLowerInvariant();

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new ArgumentException("Migration path must be relative.");
        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part is "" or "." or ".." || part.Contains(':'))
            || normalized is "generation.json" or "migration-journal.json") throw new ArgumentException("Migration path escapes generation.");
    }

    private static void WriteJournal(string generationPath, MigrationJournal journal)
        => WriteAtomicAsync(Path.Combine(generationPath, "migration-journal.json"), journal, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, ContractJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
