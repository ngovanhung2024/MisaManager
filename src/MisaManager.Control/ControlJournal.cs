using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MisaManager.Core;

namespace MisaManager.Control;

internal sealed record ControlJournalRecord(string IdempotencyKey, string Body, string State,
    DateTimeOffset UpdatedAt, OperationResult? Result);

internal sealed class ControlJournal
{
    private readonly string root;
    private readonly object gate = new();

    public ControlJournal(string? rootPath = null)
    {
        root = Path.GetFullPath(rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.TechnicalName, "control", "idempotency"));
    }

    public ControlJournalRecord? Read(string idempotencyKey)
    {
        var path = PathFor(idempotencyKey);
        lock (gate)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var record = JsonSerializer.Deserialize<ControlJournalRecord>(File.ReadAllText(path), ContractJson.Options)
                    ?? throw new InvalidDataException("Control idempotency journal is empty.");
                if (record.IdempotencyKey != idempotencyKey || string.IsNullOrWhiteSpace(record.Body)
                    || record.State is not ("pending" or "completed") || record.State == "completed" && record.Result is null)
                    throw new InvalidDataException("Control idempotency journal identity or state is invalid.");
                return record;
            }
            catch (JsonException error) { throw new InvalidDataException("Control idempotency journal is corrupt.", error); }
        }
    }

    public ControlJournalRecord? Reserve(string idempotencyKey, string body, DateTimeOffset now)
    {
        lock (gate)
        {
            var previous = Read(idempotencyKey);
            if (previous is not null) return previous;
            Write(new ControlJournalRecord(idempotencyKey, body, "pending", now, null));
            return null;
        }
    }

    public void BindRequest(ControlRequest request, string body)
    {
        var bindingKey = JsonSerializer.Serialize(new[] { request.SessionId, request.RequestId });
        var path = Path.Combine(root, "requests", Path.GetFileName(PathFor(bindingKey)));
        var binding = JsonSerializer.Serialize(new { request.IdempotencyKey, Body = body }, ContractJson.Compact);
        lock (gate)
        {
            if (File.Exists(path))
            {
                if (!string.Equals(File.ReadAllText(path), binding, StringComparison.Ordinal))
                    throw new InvalidDataException("Request ID is already bound to another key or body in this session.");
                return;
            }
            WriteAtomic(path, binding);
        }
    }

    public void WriteCompleted(string idempotencyKey, string body, OperationResult result, DateTimeOffset now)
        => Write(new ControlJournalRecord(idempotencyKey, body, "completed", now, result));

    private void Write(ControlJournalRecord value)
    {
        var path = PathFor(value.IdempotencyKey);
        lock (gate)
        {
            WriteAtomic(path, JsonSerializer.Serialize(value, ContractJson.Compact));
        }
    }

    private static void WriteAtomic(string path, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string PathFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Idempotency key is required.", nameof(key));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(root, digest + ".json");
    }
}
