using System.Text;
using System.Text.Json;
using MisaAutomation.Core;
using MisaAutomation.Input;
using MisaAutomation.UIA;

namespace MisaAutomation.App;

internal sealed record Request(string Id, string Method, JsonElement Parameters);

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object OutputGate = new();
    private static void Write(object payload)
    { lock (OutputGate) Console.WriteLine(JsonSerializer.Serialize(payload, Json)); }
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        var locatorFolder = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "locators");
        var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MisaInvoiceTool", "engine-logs");
        using var log = new JsonAutomationLog(Path.Combine(logFolder, $"engine-{Environment.ProcessId}.jsonl"));
        var repository = new LocatorRepository();
        repository.Load(locatorFolder);
        using var input = new WindowsInputProvider();
        await using var client = new MisaAutomationClient(() => new UiaProvider(repository, log), repository, log, input);
        client.DialogsChanged += dialogs => Write(new { eventName = "dialogs_changed", observedAt = DateTimeOffset.UtcNow, dialogs });
        // Stdout is reserved for correlated responses. One request owns the action lane.
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            string? id = null;
            try
            {
                if (line.Length > 2 * 1024 * 1024) throw new AutomationException("REQUEST_TOO_LARGE", "Request exceeds 2MB.");
                var request = JsonSerializer.Deserialize<Request>(line, Json) ?? throw new JsonException("Missing request.");
                id = request.Id;
                var p = request.Parameters;
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || string.IsNullOrWhiteSpace(request.Method)
                    || p.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Request needs an id, method and object parameters.");
                object? result = request.Method switch
                {
                    "hello" => new { protocol = 1, engine = "FlaUI.UIA3", version = "0.4.0", verification = "pending_live",
                        capabilities = new[] { "inline_wait", "grid_rows", "wait_window", "observe_value", "set_observed_value", "set_observed_date", "dump_tree" } },
                    "connect" => await client.ConnectAsync(Number(p, "windowHandle"), (int?)Number(p, "processId")),
                    "bind" => await client.BindWindowAsync(Number(p, "windowHandle") ?? throw new JsonException("Missing windowHandle.")),
                    "windows" => await client.WindowsAsync(),
                    "dumpTree" => await client.DumpTreeAsync((int)(Number(p, "maximumNodes") ?? 5000)),
                    "dumpTrees" => await client.DumpTreesAsync((int)(Number(p, "maximumNodes") ?? 5000)),
                    "waitWindow" => await client.WaitForWindowAsync(p.GetProperty("kind").GetString()!,
                        (int)(Number(p, "timeoutMs") ?? 5000)),
                    "query" => await client.QueryAsync(p.Deserialize<ElementQuery>(Json) ?? new()),
                    "read" => await client.ReadAsync(p.GetProperty("token").GetString()!),
                    "observeValue" => await client.ObserveValueAsync(
                        p.GetProperty("bounds").Deserialize<Bounds>(Json) ?? throw new JsonException("Missing bounds."),
                        p.TryGetProperty("automationId", out var aid) ? aid.GetString() : null,
                        (int?)Number(p, "row")),
                    "setObservedValue" => await client.SetObservedValueAsync(
                        p.GetProperty("target").Deserialize<ObservedValueTarget>(Json)
                            ?? throw new JsonException("Missing target."),
                        p.GetProperty("value").GetString() ?? "",
                        p.GetProperty("workflow").GetString() ?? ""),
                    "grid" => await client.ReadGridAsync(p.GetProperty("token").GetString()!,
                        (int)(Number(p, "startRow") ?? 0), (int)(Number(p, "maximumRows") ?? 50)),
                    "find" => p.TryGetProperty("locator", out var locator)
                        ? await client.FindAsync(p.GetProperty("key").GetString()!, locator.Deserialize<Locator>(Json)!)
                        : await client.FindAsync(p.GetProperty("key").GetString()!),
                    "execute" => await client.ExecuteAsync(p.Deserialize<Operation>(Json) ?? throw new JsonException("Missing operation.")),
                    "diagnostics" => await client.DiagnosticsAsync(),
                    "close" => null,
                    _ => throw new AutomationException("UNKNOWN_METHOD", request.Method)
                };
                Write(new { id, success = true, data = result });
                if (request.Method == "close") return 0;
            }
            catch (Exception error)
            {
                log.Write("request_error", new { id, error = error.ToString() });
                Write(new { id, success = false,
                    errorCode = (error as AutomationException)?.Code ?? "REQUEST_FAILED", message = error.Message,
                    inputMayHaveBeenSent = (error as AutomationException)?.InputMayHaveBeenSent == true });
            }
        }
        return 0;
    }
    private static long? Number(JsonElement value, string key) => value.TryGetProperty(key, out var item)
        && item.ValueKind != JsonValueKind.Null ? item.GetInt64() : null;
}
