using System.Diagnostics;

namespace MisaAutomation.Core;

public sealed class MisaAutomationClient : IAsyncDisposable
{
    private readonly AutomationDispatcher dispatcher = new();
    private readonly Func<IAutomationProvider> providerFactory;
    private readonly LocatorRepository locators;
    private readonly IAutomationLog log;
    private readonly IInputProvider? input;
    private IAutomationProvider? provider;
    private MisaDialogMonitor? dialogs;
    private readonly CancellationTokenSource monitorStop = new();
    private Task? monitor;
    public event Action<IReadOnlyCollection<DialogState>>? DialogsChanged;
    private IAutomationProvider Provider => provider ?? throw new AutomationException("NOT_CONNECTED", "Connect to MISA first.");

    public MisaAutomationClient(Func<IAutomationProvider> providerFactory, LocatorRepository locators,
        IAutomationLog log, IInputProvider? input = null)
        => (this.providerFactory, this.locators, this.log, this.input) = (providerFactory, locators, log, input);

    public Task<WindowInfo> ConnectAsync(long? windowHandle = null, int? processId = null, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            if (provider != null) throw new AutomationException("ALREADY_CONNECTED", "This client is already connected.");
            var candidate = providerFactory();
            try
            {
                var timer = Stopwatch.StartNew();
                var window = candidate.Connect(windowHandle, processId);
                provider = candidate;
                dialogs = new MisaDialogMonitor(provider, log);
                monitor = MonitorAsync(provider.Changes, monitorStop.Token);
                log.Write("connect", new { window, durationMs = timer.Elapsed.TotalMilliseconds, strategy = "UIA3" });
                return window;
            }
            catch { candidate.Dispose(); throw; }
        }, token);

    public Task<WindowInfo> BindWindowAsync(long handle, CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.BindWindow(handle), token);
    public Task<IReadOnlyList<WindowInfo>> WindowsAsync(CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.Windows(), token);
    public Task<object> DumpTreesAsync(int maximumNodes = 10000, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            var dumps = new List<object>();
            foreach (var listed in Provider.Windows())
            {
                try
                {
                    var window = Provider.BindWindow(listed.Handle);
                    var nodes = Provider.DumpTree(maximumNodes);
                    dumps.Add(new { window, nodes, nodeCount = nodes.Count, error = (string?)null });
                }
                catch (Exception error)
                {
                    dumps.Add(new { window = listed, nodes = Array.Empty<object>(), nodeCount = 0,
                        error = error.ToString() });
                }
            }
            return (object)dumps;
        }, token);
    public Task<IReadOnlyList<UiaTreeNode>> DumpTreeAsync(int maximumNodes = 5000, CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.DumpTree(maximumNodes), token);
    public Task<WindowInfo> WaitForWindowAsync(string kind, int timeoutMs = 5000, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            if (kind is not ("main" or "sale" or "find" or "new_item") || timeoutMs is < 1 or > 60000)
                throw new AutomationException("INVALID_WINDOW_WAIT", "Expected a known window kind and a 1..60000ms timeout.");
            var timer = Stopwatch.StartNew();
            var result = new SmartWait(Provider.Changes).Until(() =>
            {
                dialogs!.ThrowIfBlocking();
                var candidates = Provider.Windows().Where(w => w.Kind == kind && w.Enabled).ToArray();
                if (candidates.Length > 1) throw new AutomationException("AMBIGUOUS_WINDOW", "Multiple enabled MISA windows match " + kind);
                return candidates.SingleOrDefault();
            }, window => window != null, TimeSpan.FromMilliseconds(timeoutMs), token)!;
            log.Write("wait_window", new { kind, waitMs = timer.Elapsed.TotalMilliseconds, window = result.Handle });
            return Provider.BindWindow(result.Handle);
        }, token);
    public Task<IReadOnlyList<ElementInfo>> QueryAsync(ElementQuery query, CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.Query(query), token);
    public Task<ElementInfo> ReadAsync(string elementToken, CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.Read(elementToken), token);
    public Task<ValueObservation> ObserveValueAsync(Bounds bounds, string? automationId, int? row,
        CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.ObserveValue(bounds, automationId, row), token);
    public Task<string> SetObservedValueAsync(ObservedValueTarget target, string value, string workflow,
        CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.SetObservedValue(target, value, workflow), token);
    public Task<Inspection> InspectPointAsync(int x, int y, CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.InspectPoint(x, y), token);
    public Task<GridPage> ReadGridAsync(string elementToken, int startRow = 0, int maximumRows = 50, CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.ReadGrid(elementToken, startRow, maximumRows), token);
    public Task<ElementInfo> FindAsync(string key, CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.Find(key, locators[key], token), token);
    public Task<ElementInfo> FindAsync(string key, Locator locator, CancellationToken token = default)
        => dispatcher.RunAsync(() => Provider.Find(key, locator, token), token);
    public Task<object> DiagnosticsAsync(CancellationToken token = default)
        => dispatcher.RunAsync(() => { dialogs!.Observe(); return (object)new { engine = Provider.Diagnostics(), dialogs = dialogs.Current }; }, token);

    public Task<AutomationResult> GetValueAsync(string locator, CancellationToken token = default)
        => ExecuteAsync(new Operation("get", locator), token);
    public Task<AutomationResult> WaitForAsync(string locator, string condition = "exists", string? expected = null,
        int timeoutMs = 5000, CancellationToken token = default)
        => ExecuteAsync(new Operation("wait:" + condition, locator, Expected: expected, TimeoutMs: timeoutMs), token);

    public Task<AutomationResult> ExecuteAsync(Operation operation, CancellationToken token = default)
        => dispatcher.RunAsync(() => Execute(operation, token), token);

    private AutomationResult Execute(Operation op, CancellationToken token)
    {
        var total = Stopwatch.StartNew();
        var stage = Stopwatch.StartNew();
        double searchMs = 0, actionMs = 0, waitMs = 0;
        var strategy = "UIA3";
        var sent = false;
        AutomationResult result;
        try
        {
            token.ThrowIfCancellationRequested();
            if (op.TimeoutMs is < 1 or > 60000) throw new AutomationException("INVALID_TIMEOUT", "Timeout must be 1..60000ms.");
            Locator? locator = op.Selector ?? (op.Locator == null ? null : locators[op.Locator]);
            var selectorKey = op.Selector == null ? op.Locator : "inline." + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(op.Selector)));
            ElementInfo Resolve() => selectorKey != null ? Provider.Find(selectorKey, locator!, token)
                : op.Token != null ? Provider.Read(op.Token) : throw new AutomationException("MISSING_TARGET", "An element or locator is required.");
            object? data;
            if (op.Action.StartsWith("wait:", StringComparison.Ordinal))
            {
                var condition = op.Action[5..];
                if (condition is not ("exists" or "missing" or "enabled" or "disabled" or "value" or "selected"))
                    throw new AutomationException("INVALID_CONDITION", condition);
                var observed = new SmartWait(Provider.Changes).Until(() =>
                {
                    dialogs!.ThrowIfBlocking();
                    try { return Resolve(); }
                    catch (AutomationException error) when (error.Code == "NOT_FOUND") { return null; }
                }, element => condition switch
                {
                    "exists" => element != null,
                    "missing" => element == null,
                    "enabled" => element is { Enabled: true },
                    "disabled" => element is { Enabled: false },
                    "selected" => element is { Selected: true },
                    "value" => element != null && element.Value == op.Expected,
                    _ => false
                }, TimeSpan.FromMilliseconds(op.TimeoutMs), token);
                data = observed;
                waitMs = stage.Elapsed.TotalMilliseconds;
            }
            else if (op.Action == "set_date")
            {
                dialogs!.ThrowIfBlocking();
                if (op.DateTarget == null || op.Value == null)
                    throw new AutomationException("MISSING_TARGET", "Date input requires a current native target and value.");
                // This path must never fall back to clipboard input for a masked date editor.
                strategy = Provider.SetObservedDate(op.DateTarget, op.Value, op.Workflow);
                sent = true;
                data = null;
                actionMs = stage.Elapsed.TotalMilliseconds;
            }
            else if (op.Action == "key")
            {
                dialogs!.ThrowIfBlocking();
                if (input == null || !op.Key.HasValue) throw new AutomationException("INPUT_UNAVAILABLE", "Keyboard provider or key is missing.");
                Provider.GuardKeyboard(op.Key.Value, op.Control);
                sent = true;
                input.Press(Provider.Window!, op.Key.Value, op.Control);
                strategy = "Keyboard"; data = null;
                actionMs = stage.Elapsed.TotalMilliseconds;
            }
            else
            {
                var element = Resolve();
                searchMs = stage.Elapsed.TotalMilliseconds;
                stage.Restart();
                if (op.Action == "get") data = element;
                else
                {
                    if (locator?.ReadOnly == true) throw new AutomationException("READ_ONLY", "Locator is read-only.");
                    dialogs!.ThrowIfBlocking();
                    if (op.Action == "focus") { Provider.Focus(element.Token, op.AllowBackground); sent = true; strategy = "UIA3.Focus"; }
                    else
                    {
                        try { strategy = Provider.Execute(element.Token, op.Action, op.Value, op.AllowBackground); sent = true; }
                        catch (AutomationException error) when (error.Code == "PATTERN_UNSUPPORTED" && !error.InputMayHaveBeenSent
                            && op.Action == "set" && input != null && !op.AllowBackground)
                        {
                            input.Paste(Provider.Window!, op.Value ?? "", () =>
                            {
                                Provider.Focus(element.Token);
                                Provider.GuardKeyboard(0x56, true);
                            });
                            strategy = "Clipboard"; sent = true;
                        }
                    }
                    data = null;
                }
                actionMs = stage.Elapsed.TotalMilliseconds;
            }
            result = new(true, null, "Operation completed; workflow readback determines accounting verification.",
                total.Elapsed.TotalMilliseconds, strategy, op.Locator, data, searchMs, actionMs, waitMs, sent);
        }
        catch (Exception error)
        {
            var fault = error as AutomationException;
            result = new(false, fault?.Code ?? (error is OperationCanceledException ? "CANCELLED" : "PROVIDER_ERROR"),
                error.Message, total.Elapsed.TotalMilliseconds, strategy, op.Locator, null, searchMs,
                actionMs, waitMs, sent || fault?.InputMayHaveBeenSent == true);
            log.Write("operation_exception", new { op.Action, op.Locator, error = error.ToString() });
            if (error is not OperationCanceledException && provider?.Window != null)
            {
                try { log.Write("error_evidence", new { path = provider.CaptureFailure(op, error) }); }
                catch (Exception evidenceError) { log.Write("error_evidence_failed", new { error = evidenceError.ToString() }); }
            }
        }
        log.Write("operation", new { op.Workflow, op.Action, op.Locator, result });
        return result;
    }

    private async Task MonitorAsync(ChangeSignal changes, CancellationToken token)
    {
        var previous = "";
        while (!token.IsCancellationRequested)
        {
            var version = changes.Version;
            try
            {
                await changes.WaitAsync(version, TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                var snapshot = await dispatcher.RunAsync(() =>
                {
                    dialogs!.Observe();
                    return dialogs.Current.ToArray();
                }, token).ConfigureAwait(false);
                var signature = string.Join("|", snapshot.Select(d => d.Window.Handle + ":" + d.Message));
                if (signature != previous) { previous = signature; DialogsChanged?.Invoke(snapshot); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                log.Write("dialog_monitor_error", new { error = error.ToString() });
                // Provider faults end this monitor; they cannot silently trigger UI input.
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        monitorStop.Cancel();
        if (monitor != null) await monitor.ConfigureAwait(false);
        await dispatcher.RunAsync(() => { provider?.Dispose(); provider = null; return true; }).ConfigureAwait(false);
        dispatcher.Dispose();
        monitorStop.Dispose();
    }
}
