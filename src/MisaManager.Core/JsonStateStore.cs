using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MisaManager.Core;

public sealed record StateEnvelope<T>(
    string DocumentType,
    int SchemaVersion,
    string AppVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    T Data,
    string? SourceRevision = null,
    string? ProfileRevision = null,
    RuntimeStateIdentity? RuntimeIdentity = null);

public interface IStateStore
{
    string RootPath { get; }
    Task<T?> ReadAsync<T>(string documentType, string relativePath, CancellationToken cancellationToken = default);
    Task WriteAsync<T>(string documentType, string relativePath, T data, CancellationToken cancellationToken = default);
}

public sealed class JsonStateStore : IStateStore
{
    private static readonly JsonSerializerOptions Json = ContractJson.Options;
    private readonly SemaphoreSlim gate = new(1, 1);
    public string RootPath { get; }

    public JsonStateStore(string? rootPath = null)
    {
        RootPath = Path.GetFullPath(rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.TechnicalName));
    }

    public async Task<T?> ReadAsync<T>(string documentType, string relativePath, CancellationToken cancellationToken = default)
    {
        var path = SafePath(relativePath);
        if (!File.Exists(path)) return default;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var envelope = await JsonSerializer.DeserializeAsync<StateEnvelope<T>>(stream, Json, cancellationToken);
        if (envelope is null || envelope.DocumentType != documentType || envelope.SchemaVersion != ProductIdentity.StateSchemaVersion
            || string.IsNullOrWhiteSpace(envelope.AppVersion) || envelope.CreatedAt == default
            || envelope.UpdatedAt < envelope.CreatedAt || envelope.Data is null)
            throw new InvalidDataException($"State document '{relativePath}' is not a supported MisaManager schema.");
        return envelope.Data;
    }

    public async Task WriteAsync<T>(string documentType, string relativePath, T data, CancellationToken cancellationToken = default)
    {
        var path = SafePath(relativePath);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var created = File.Exists(path) ? await ReadCreatedAtAsync(path, documentType, cancellationToken) : now;
            var profile = data is WorkflowContext workflow ? workflow.ProfileRevision : null;
            var envelope = new StateEnvelope<T>(documentType, ProductIdentity.StateSchemaVersion,
                ProductIdentity.RuntimeVersion, created, now, data, ProductIdentity.SourceRevision, profile,
                new RuntimeStateIdentity(ProductIdentity.RuntimeVersion, ProductIdentity.SourceRevision, profile ?? ""));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, envelope, Json, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { gate.Release(); }
    }

    private async Task<DateTimeOffset> ReadCreatedAtAsync(string path, string documentType, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var root = document.RootElement;
        if (!root.TryGetProperty("documentType", out var type) || type.GetString() != documentType
            || !root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != ProductIdentity.StateSchemaVersion
            || !root.TryGetProperty("createdAt", out var property) || !property.TryGetDateTimeOffset(out var created))
            throw new InvalidDataException($"State document '{path}' is invalid; refusing to overwrite it.");
        return created;
    }

    private string SafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("State path must be relative.", nameof(relativePath));
        var full = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        var root = RootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("State path escapes the state root.", nameof(relativePath));
        StateFiles.RejectReparse(full);
        return full;
    }
}

public static class FileDigest
{
    public static async Task<string> Sha256Async(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
