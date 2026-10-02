using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed class UiaReadbackProvider : INativeReadback, IDisposable
{
    private readonly UIA3Automation automation = new();
    private AutomationElement? root;
    private WindowIdentity? identity;
    private long epoch;
    private readonly Dictionary<string, (ElementIdentity Identity, AutomationElement Element)> observed = new(StringComparer.Ordinal);
    private long observeCalls;
    private long readbackCalls;
    private long cacheHits;
    private long cacheInvalidations;
    public int LastVisitedCount { get; private set; }
    public bool LastObservationTruncated { get; private set; }

    public NativeReadbackMetrics Metrics => new(observeCalls, readbackCalls, cacheHits, cacheInvalidations);

    public void Bind(WindowIdentity window)
    {
        if (window.WindowHandle == nint.Zero) throw new AutomationException("WINDOW_NOT_FOUND", "HWND không hợp lệ.");
        root = automation.FromHandle(window.WindowHandle);
        identity = window;
        observed.Clear();
        cacheInvalidations++;
        epoch++;
    }

    public NativeFrame CaptureFrame()
    {
        if (identity is null || root is null) throw new AutomationException("NOT_CONNECTED", "Chưa bind cửa sổ MISA.");
        var current = new MisaWindowDiscovery().Bind(identity.WindowHandle, identity.ProcessId);
        if (current.ProcessStartUtc != identity.ProcessStartUtc || current.WindowHandle != identity.WindowHandle)
            throw new AutomationException("WINDOW_IDENTITY_CHANGED", "Process hoặc HWND MISA đã thay đổi.");
        identity = current;
        return new(current, root.Properties.Name.ValueOrDefault ?? "", epoch, DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<ElementIdentity> Observe(NativeFrame frame, string? query = null)
        => Observe(frame, query, null);

    public IReadOnlyList<ElementIdentity> Observe(NativeFrame frame, string? query, string? scopeAutomationId,
        int maxElements = 500, int maxVisited = 5000)
    {
        if (maxElements is < 1 or > 50000 || maxVisited is < 1 or > 100000)
            throw new AutomationException("OBSERVATION_LIMIT_INVALID", "Giới hạn quan sát native nằm ngoài khoảng cho phép.");
        observeCalls++;
        EnsureFrame(frame);
        observed.Clear();
        LastVisitedCount = 0;
        LastObservationTruncated = true;
        var scope = root!;
        if (!string.IsNullOrWhiteSpace(scopeAutomationId))
        {
            var scopes = root!.FindAllDescendants(automation.ConditionFactory.ByAutomationId(scopeAutomationId))
                .Where(item => item.Properties.ProcessId.ValueOrDefault == frame.Window.ProcessId).ToArray();
            if (scopes.Length > 1) throw new AutomationException("SCOPE_AMBIGUOUS", "Không xác định duy nhất vùng native cần đọc.");
            if (scopes.Length == 0)
            {
                LastObservationTruncated = false;
                return Array.Empty<ElementIdentity>();
            }
            scope = scopes[0];
        }
        var results = new List<ElementIdentity>();
        var pending = new Queue<(AutomationElement Element, string ParentToken)>();
        pending.Enqueue((scope, ""));
        var visited = 0;
        var truncatedByLimit = false;
        while (pending.Count > 0 && results.Count < maxElements && visited++ < maxVisited)
        {
            var (element, parentToken) = pending.Dequeue();
            var isMatch = string.IsNullOrWhiteSpace(query)
                || string.Equals(element.Properties.AutomationId.ValueOrDefault, query, StringComparison.Ordinal)
                || string.Equals(element.Properties.Name.ValueOrDefault, query, StringComparison.Ordinal);
            var currentParent = parentToken;
            if (isMatch)
            {
                var identity = ToIdentity(element, parentToken);
                results.Add(identity);
                currentParent = identity.Token;
            }
            var children = element.FindAllChildren();
            var remaining = Math.Max(0, maxVisited - visited - pending.Count);
            if (children.Length > remaining) truncatedByLimit = true;
            foreach (var child in children.Take(remaining))
                if (child.Properties.ProcessId.ValueOrDefault == frame.Window.ProcessId)
                    pending.Enqueue((child, currentParent));
        }
        LastVisitedCount = visited;
        LastObservationTruncated = truncatedByLimit || pending.Count > 0;
        return results;
    }

    public Readback ReadValue(NativeFrame frame, ElementIdentity target)
    {
        readbackCalls++;
        EnsureFrame(frame);
        var element = FindTarget(target);
        if (element is null) return new("unavailable", null, "UIA3", DateTimeOffset.UtcNow, "Target disappeared.", CaptureFrame());
        cacheHits++;
        var value = element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.ValueOrDefault : element.Name;
        return new("supported", value, element.Patterns.Value.IsSupported ? "UIA3.ValuePattern" : "UIA3.Name", DateTimeOffset.UtcNow, null, CaptureFrame());
    }

    private AutomationElement? FindTarget(ElementIdentity target)
    {
        if (!observed.TryGetValue(target.Token, out var entry) || entry.Identity != target) return null;
        var element = entry.Element;
        if (element.Properties.ProcessId.ValueOrDefault != target.ProcessId
            || element.Properties.AutomationId.ValueOrDefault != target.AutomationId
            || element.Properties.Name.ValueOrDefault != target.Name) return null;
        return element;
    }

    private void EnsureFrame(NativeFrame frame)
    {
        if (identity is null || frame.Window.WindowHandle != identity.WindowHandle || frame.Window.ProcessId != identity.ProcessId
            || frame.Window.ProcessStartUtc != identity.ProcessStartUtc || frame.InputEpoch != epoch)
            throw new AutomationException("STALE_NATIVE_FRAME", "Quan sát đã cũ; cần chụp frame native mới.");
    }

    private ElementIdentity ToIdentity(AutomationElement element, string parentToken)
    {
        var bounds = element.BoundingRectangle;
        var runtimeId = element.Properties.RuntimeId.ValueOrDefault is { Length: > 0 } ids
            ? string.Join(".", ids) : "";
        var result = new ElementIdentity(Guid.NewGuid().ToString("N"), element.Properties.AutomationId.ValueOrDefault ?? "", element.Properties.Name.ValueOrDefault ?? "",
            element.ControlType.ToString(), new(bounds.X, bounds.Y, bounds.Width, bounds.Height), element.Properties.NativeWindowHandle.ValueOrDefault,
            element.Properties.ProcessId.ValueOrDefault, identity!.WindowHandle,
            new[] { element.Patterns.Value.IsSupported ? "Value" : "", element.Patterns.Invoke.IsSupported ? "Invoke" : "",
                element.Patterns.SelectionItem.IsSupported ? "SelectionItem" : "", element.Patterns.Grid.IsSupported ? "Grid" : "",
                element.Patterns.GridItem.IsSupported ? "GridItem" : "", element.Patterns.Text.IsSupported ? "Text" : "",
                element.Patterns.ExpandCollapse.IsSupported ? "ExpandCollapse" : "", element.Patterns.Toggle.IsSupported ? "Toggle" : "" }
                .Where(value => value.Length > 0).ToArray(),
            parentToken, element.Properties.IsEnabled.ValueOrDefault, element.Properties.IsOffscreen.ValueOrDefault,
            element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.ValueOrDefault : null,
            element.Properties.ClassName.ValueOrDefault ?? "", element.FrameworkType.ToString(),
            element.Properties.HelpText.ValueOrDefault ?? "", runtimeId,
            element.Properties.IsKeyboardFocusable.ValueOrDefault, element.Properties.HasKeyboardFocus.ValueOrDefault,
            element.Properties.IsContentElement.ValueOrDefault, element.Properties.IsControlElement.ValueOrDefault,
            element.Properties.ItemStatus.ValueOrDefault ?? "");
        observed.Add(result.Token, (result, element));
        return result;
    }

    public void Dispose() => automation.Dispose();
}
