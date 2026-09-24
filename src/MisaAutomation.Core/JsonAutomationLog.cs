using System.Text.Json;

namespace MisaAutomation.Core;

public sealed class JsonAutomationLog : IAutomationLog, IDisposable
{
    private readonly StreamWriter writer;
    private readonly object gate = new();
    public JsonAutomationLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
    }
    public void Write(string eventName, object data)
    {
        try
        {
            lock (gate) writer.WriteLine(JsonSerializer.Serialize(new
                { timestamp = DateTimeOffset.UtcNow, eventName, data }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // A diagnostic write failure must not turn an already-sent action
            // into a transport error that appears safe to retry.
            try { Console.Error.WriteLine("MISA log unavailable: " + error.Message); }
            catch (IOException) { }
        }
    }
    public void Dispose() { lock (gate) writer.Dispose(); }
}
