using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using MisaManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MisaManager.Automation;

// Properties fetched together with the search result. Every read of a live
// AutomationElement property is a separate cross-process call into MISA.
internal sealed record ElementSnapshot(AutomationElement Element, Rectangle Bounds, ControlType ControlType,
    int ProcessId, bool Enabled, bool Offscreen, string Name, string AutomationId, nint NativeWindowHandle,
    bool HasKeyboardFocus)
{
    public bool HasArea => Bounds.Width > 0 && Bounds.Height > 0;
}

public sealed partial class NativeInputService
{
    private CacheRequest? snapshotRequest;
    private readonly BoundWindowTracker windowTracker = new();
    private readonly Dictionary<string, AutomationElement> gridCache = new(StringComparer.Ordinal);
    private AutomationElement? gridCacheRoot;
    private AutomationElement? topLevelRoot;
    private nint topLevelHandle;

    private CacheRequest SnapshotRequest
    {
        get
        {
            if (snapshotRequest is not null) return snapshotRequest;
            var properties = automation.PropertyLibrary.Element;
            // Full mode keeps live references so callers can still use patterns.
            var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
            request.Add(properties.BoundingRectangle);
            request.Add(properties.ControlType);
            request.Add(properties.ProcessId);
            request.Add(properties.IsEnabled);
            request.Add(properties.IsOffscreen);
            request.Add(properties.Name);
            request.Add(properties.AutomationId);
            request.Add(properties.NativeWindowHandle);
            request.Add(properties.HasKeyboardFocus);
            return snapshotRequest = request;
        }
    }

    // Only cached properties may be read while the request is active; filter
    // on the returned snapshots and use live element reads afterwards.
    private ElementSnapshot[] Snapshots(AutomationElement scope, ConditionBase condition)
    {
        using (SnapshotRequest.Activate())
            return scope.FindAllDescendants(condition).Select(ToSnapshot).ToArray();
    }

    private ElementSnapshot? TrySnapshot(AutomationElement element)
    {
        try
        {
            using (SnapshotRequest.Activate())
            {
                var updated = element.FindFirst(TreeScope.Element, TrueCondition.Default);
                return updated is null ? null : ToSnapshot(updated);
            }
        }
        catch (COMException) { return null; }
    }

    private static ElementSnapshot ToSnapshot(AutomationElement element)
        => new(element, element.Properties.BoundingRectangle.ValueOrDefault,
            element.Properties.ControlType.ValueOrDefault, element.Properties.ProcessId.ValueOrDefault,
            element.Properties.IsEnabled.ValueOrDefault, element.Properties.IsOffscreen.ValueOrDefault,
            element.Properties.Name.ValueOrDefault ?? "", element.Properties.AutomationId.ValueOrDefault ?? "",
            element.Properties.NativeWindowHandle.ValueOrDefault, element.Properties.HasKeyboardFocus.ValueOrDefault);

    // Evaluated by UIA during the search instead of per element by the caller.
    private ConditionBase Live(ConditionBase condition, int? processId, bool enabled = true, bool onscreen = true)
    {
        var properties = automation.PropertyLibrary.Element;
        var result = processId is { } pid ? condition.And(automation.ConditionFactory.ByProcessId(pid)) : condition;
        if (enabled) result = result.And(new PropertyCondition(properties.IsEnabled, true));
        if (onscreen) result = result.And(new PropertyCondition(properties.IsOffscreen, false));
        return result;
    }

    // Enabled, onscreen Window elements under the MISA host.
    private ElementSnapshot[] HostWindows(int? processId)
        => Snapshots(automation.FromHandle(identity!.WindowHandle),
            Live(automation.ConditionFactory.ByControlType(ControlType.Window), processId));

    private ConditionBase AnyControlType(params ControlType[] types)
        => types.Length == 1 ? automation.ConditionFactory.ByControlType(types[0])
            : new OrCondition(types.Select(type => (ConditionBase)automation.ConditionFactory.ByControlType(type)).ToArray());

    private AutomationElement? WidestEditor(AutomationElement scope)
        => Snapshots(scope, Live(automation.ConditionFactory.ByControlType(ControlType.Edit), identity!.ProcessId))
            .Where(item => item.HasArea)
            .OrderByDescending(item => item.Bounds.Width)
            .FirstOrDefault()?.Element;

    // Dropdown list items drawn next to the grid cell that opened them.
    private AutomationElement[] RegisteredOptions(AutomationElement host, Rectangle cellBounds)
        => Snapshots(host, Live(automation.ConditionFactory.ByControlType(ControlType.ListItem), identity!.ProcessId))
            .Where(item => item.HasArea
                && item.Bounds.Left < cellBounds.Right && item.Bounds.Right > cellBounds.Left
                && Math.Abs(item.Bounds.Top - cellBounds.Bottom) < 420)
            .Select(item => item.Element)
            .Where(item => item.Patterns.SelectionItem.IsSupported)
            .ToArray();

    private WindowIdentity RefreshWindow(WindowIdentity expected) => windowTracker.Refresh(expected);

    // Win32 modal dialogs and message boxes are visible, enabled top-level
    // windows owned by the MISA host. When Win32 reports none besides the
    // allowed scope windows, the full UIA host scan cannot find one either.
    private List<nint> OwnedDialogCandidates(IReadOnlySet<nint> allowed)
    {
        var main = identity!.WindowHandle;
        var processId = (uint)identity.ProcessId;
        var result = new List<nint>();
        EnumWindows((handle, _) =>
        {
            if (handle != main && !allowed.Contains(handle)
                && GetWindowThreadProcessId(handle, out var owner) != 0 && owner == processId
                && PInvoke.IsWindowVisible((HWND)handle) && PInvoke.IsWindowEnabled((HWND)handle)
                && OwnedBy(handle, main))
                result.Add(handle);
            return true;
        }, nint.Zero);
        return result;
    }

    private bool MayHaveModalDialog()
    {
        var rootWindow = RootTopLevelWindow();
        if (rootWindow == nint.Zero) return true;
        // The bound scope's own window and its owners are its UIA ancestors.
        var allowed = new HashSet<nint>();
        for (var handle = rootWindow; handle != nint.Zero && allowed.Add(handle);
            handle = (nint)PInvoke.GetWindow((HWND)handle, GET_WINDOW_CMD.GW_OWNER)) { }
        foreach (var handle in OwnedDialogCandidates(allowed))
        {
            try
            {
                var window = automation.FromHandle(handle);
                if (window.Patterns.Window.IsSupported && window.Patterns.Window.Pattern.IsModal.Value) return true;
            }
            catch (COMException) { return true; }
        }
        return false;
    }

    private bool MayHaveBlockingDialog()
    {
        foreach (var handle in OwnedDialogCandidates(new HashSet<nint>()))
        {
            try
            {
                var window = automation.FromHandle(handle);
                if (string.Equals(window.Properties.AutomationId.ValueOrDefault, "frmSAVoucherDetail", StringComparison.OrdinalIgnoreCase)
                    || IsCatalogDialogName(window.Properties.Name.ValueOrDefault))
                    continue;
                if (window.Patterns.Window.IsSupported && window.Patterns.Window.Pattern.IsModal.Value
                    || string.Equals(window.Properties.ClassName.ValueOrDefault, "#32770", StringComparison.Ordinal))
                    return true;
            }
            catch (COMException) { return true; }
        }
        return false;
    }

    private nint RootTopLevelWindow()
    {
        if (root is null) return nint.Zero;
        if (ReferenceEquals(topLevelRoot, root)) return topLevelHandle;
        var handle = nint.Zero;
        var depth = 0;
        for (var current = root; current is not null && depth++ < 32; current = current.Parent)
        {
            var native = current.Properties.NativeWindowHandle.ValueOrDefault;
            if (native == nint.Zero) continue;
            handle = (nint)PInvoke.GetAncestor((HWND)native, GET_ANCESTOR_FLAGS.GA_ROOT);
            break;
        }
        topLevelRoot = root;
        topLevelHandle = handle;
        return handle;
    }

    // Grid containers outlive cell editors, so keep them per bound scope and
    // re-check liveness instead of searching the whole editor tree each call.
    private AutomationElement ResolveGrid(string gridId)
    {
        if (!ReferenceEquals(gridCacheRoot, root))
        {
            gridCache.Clear();
            gridCacheRoot = root;
        }
        var selector = gridId.Length > 0 ? new NativeSelector(gridId) : new NativeSelector(ControlType: "DataGrid");
        if (gridCache.TryGetValue(gridId, out var cached))
        {
            var snapshot = TrySnapshot(cached);
            if (snapshot is not null && snapshot.ProcessId == identity!.ProcessId && Matches(snapshot, selector)) return cached;
            gridCache.Remove(gridId);
        }
        var grid = Resolve(selector);
        gridCache[gridId] = grid;
        return grid;
    }

    private AutomationElement Resolve(NativeSelector selector) => ResolveMany([selector])[0];

    // One UIA search for a whole field group, with per-selector uniqueness.
    private AutomationElement[] ResolveMany(IReadOnlyList<NativeSelector> selectors)
    {
        if (root is null || identity is null) throw new AutomationException("NOT_CONNECTED", "Chưa chọn cửa sổ MISA.");
        if (selectors.Count == 0) return [];
        var conditions = selectors.Select(SelectorCondition).ToArray();
        var condition = conditions.Length == 1 ? conditions[0] : new OrCondition(conditions);
        var found = Snapshots(root, Live(condition, identity.ProcessId));
        return selectors.Select(selector =>
        {
            var elements = found.Where(item => Matches(item, selector)).ToArray();
            if (elements.Length == 0) throw new AutomationException("TARGET_NOT_FOUND", $"Chưa có native target: {selector.AutomationId}/{selector.Name}.");
            if (elements.Length != 1) throw new AutomationException("TARGET_AMBIGUOUS", $"Cần đúng một native target: {selector.AutomationId}/{selector.Name}.");
            return elements[0].Element;
        }).ToArray();
    }

    private ConditionBase SelectorCondition(NativeSelector selector)
    {
        var factory = automation.ConditionFactory;
        ConditionBase condition = !string.IsNullOrWhiteSpace(selector.AutomationId) ? factory.ByAutomationId(selector.AutomationId)
            : !string.IsNullOrWhiteSpace(selector.Name) ? factory.ByName(selector.Name)
            : factory.ByControlType(Enum.Parse<ControlType>(selector.ControlType));
        if ((!string.IsNullOrWhiteSpace(selector.AutomationId) || !string.IsNullOrWhiteSpace(selector.Name))
            && Enum.TryParse<ControlType>(selector.ControlType, out var type) && type.ToString() == selector.ControlType)
            condition = condition.And(factory.ByControlType(type));
        return condition;
    }

    private static bool Matches(ElementSnapshot item, NativeSelector selector)
        => (!string.IsNullOrWhiteSpace(selector.AutomationId) ? item.AutomationId == selector.AutomationId
                : string.IsNullOrWhiteSpace(selector.Name) || item.Name == selector.Name)
            && (selector.Name.Length == 0 || item.Name == selector.Name)
            && (selector.ControlType.Length == 0 || item.ControlType.ToString() == selector.ControlType)
            && item.Enabled && !item.Offscreen && item.HasArea;
}
