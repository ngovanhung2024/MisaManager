using MisaManager.Core;

namespace MisaManager.Automation;

public sealed record NativeObservation(NativeFrame Frame, IReadOnlyList<ElementIdentity> Elements,
    int VisitedCount = 0, bool Truncated = false, string? Query = null, string? ScopeAutomationId = null);
public sealed record NativeTabExport(NativeObservation Observation, string ScopeAutomationId,
    int MaxElements, int MaxVisited, bool Complete, string CompletenessReason);
public sealed record NativeDiagnosticsSnapshot(IReadOnlyList<WindowIdentity> Candidates,
    IReadOnlyList<NativeObservation> Observations,
    NativeDispatcherMetrics Queue, NativeReadbackMetrics Readback, string ReadbackBackend, DateTimeOffset ObservedAt,
    IReadOnlyList<string>? Skipped = null);
public sealed record NativeReadbackMetrics(long ObserveCalls, long ReadbackCalls, long CacheHits, long CacheInvalidations);

public sealed class NativeObservationService : IAsyncDisposable
{
    private readonly NativeDispatcher dispatcher;
    private readonly bool ownsDispatcher;
    private UiaReadbackProvider? readbackInstance;
    private UiaReadbackProvider readback => readbackInstance ??= dispatcher.Own(() => new UiaReadbackProvider());
    private readonly MisaWindowDiscovery discovery = new();

    public NativeObservationService(NativeDispatcher? owner = null)
    {
        dispatcher = owner ?? new();
        ownsDispatcher = owner is null;
    }

    public Task<IReadOnlyList<WindowIdentity>> FindCandidatesAsync(CancellationToken token = default)
        => dispatcher.RunAsync(() => discovery.FindCandidates(), token);

    public async Task<NativeDiagnosticsSnapshot> DiagnosticsAsync(CancellationToken token = default)
    {
        var candidates = await dispatcher.RunAsync(() => discovery.FindCandidates(), token);
        var observations = new List<NativeObservation>();
        var skipped = new List<string>();
        foreach (var candidate in candidates.Take(8))
        {
            token.ThrowIfCancellationRequested();
            try { observations.Add(await ObserveAsync(candidate.WindowHandle, null, token)); }
            catch (AutomationException error) { skipped.Add($"pid={candidate.ProcessId};hwnd={candidate.WindowHandle};code={error.Code};message={error.Message}"); System.Diagnostics.Trace.TraceWarning("Diagnostics observation skipped: {0}", error.Message); }
        }
        var metrics = await dispatcher.RunAsync(() => readback.Metrics, token);
        return new(candidates, observations, dispatcher.Metrics, metrics, "UIA3/Win32", DateTimeOffset.UtcNow, skipped);
    }

    public Task<NativeObservation> ObserveAsync(nint handle, string? query, CancellationToken token = default,
        string? scopeAutomationId = null)
        => dispatcher.RunAsync(() =>
        {
            var window = discovery.Bind(handle);
            readback.Bind(window);
            var frame = readback.CaptureFrame();
            var elements = readback.Observe(frame, query, scopeAutomationId);
            return new NativeObservation(frame, elements, readback.LastVisitedCount, readback.LastObservationTruncated, query, scopeAutomationId);
        }, token);

    public Task<NativeTabExport> ExportTabAsync(nint handle, string scopeAutomationId, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(scopeAutomationId) || scopeAutomationId.Length > 256)
                throw new AutomationException("SCOPE_REQUIRED", "Cần AutomationId hợp lệ của tab MISA để export.");
            const int maxElements = 10000;
            const int maxVisited = 20000;
            var window = discovery.Bind(handle);
            readback.Bind(window);
            var frame = readback.CaptureFrame();
            var elements = readback.Observe(frame, null, scopeAutomationId, maxElements, maxVisited);
            if (elements.Count == 0)
                throw new AutomationException("SCOPE_NOT_FOUND", "Không thấy tab MISA theo AutomationId hiện tại.");
            var observation = new NativeObservation(frame, elements, readback.LastVisitedCount,
                readback.LastObservationTruncated, null, scopeAutomationId);
            return new NativeTabExport(observation, scopeAutomationId, maxElements, maxVisited,
                !observation.Truncated,
                observation.Truncated ? "UIA tree exceeded the export limit; inspect a narrower scope."
                    : "All UIA elements exposed by this scope were visited at observation time; virtualized or closed content may be absent.");
        }, token);

    public ValueTask DisposeAsync()
    {
        if (ownsDispatcher) dispatcher.Dispose();
        return ValueTask.CompletedTask;
    }
}
