using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
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
        => ProcessSnapshots(identity!.WindowHandle, identity.ProcessId,
            Live(automation.ConditionFactory.ByControlType(ControlType.Window), processId));

    /// <summary>
    /// Matches under MISA's main window and under the other top-level windows of the same process. R20 can open the
    /// voucher form as a top-level window without owner (report 0.2.187, HĐ 205): UIA then lists it, and the dialogs it
    /// owns, under the desktop rather than under the main window.
    /// </summary>
    private ElementSnapshot[] ProcessSnapshots(nint windowHandle, int processId, ConditionBase condition)
    {
        var found = Snapshots(automation.FromHandle(windowHandle), condition).ToList();
        AutomationElement[] tops;
        try { tops = automation.GetDesktop().FindAllChildren(automation.ConditionFactory.ByProcessId(processId)); }
        catch (COMException) { return [.. found]; }
        foreach (var top in tops)
        {
            try
            {
                if (top.Properties.NativeWindowHandle.ValueOrDefault == windowHandle) continue;
                using (SnapshotRequest.Activate())
                    found.AddRange(top.FindAll(TreeScope.Subtree, condition).Select(ToSnapshot));
            }
            catch (Exception error) when (error is COMException or ElementNotAvailableException) { /* closed meanwhile */ }
        }
        // An owned window is listed once (under its owner); keep one entry per HWND in case UIA shows it twice.
        return found.Where((item, index) => item.NativeWindowHandle == nint.Zero
            || found.FindIndex(other => other.NativeWindowHandle == item.NativeWindowHandle) == index).ToArray();
    }

    private ConditionBase AnyControlType(params ControlType[] types)
        => types.Length == 1 ? automation.ConditionFactory.ByControlType(types[0])
            : new OrCondition(types.Select(type => (ConditionBase)automation.ConditionFactory.ByControlType(type)).ToArray());

    private AutomationElement? WidestEditor(AutomationElement scope)
        => Snapshots(scope, Live(automation.ConditionFactory.ByControlType(ControlType.Edit), identity!.ProcessId))
            .Where(item => item.HasArea)
            .OrderByDescending(item => item.Bounds.Width)
            .FirstOrDefault()?.Element;

    // Dropdown list items drawn next to the grid cell that opened them.
    // R20 unit cells keep their value-list items in the UIA tree while the list is closed (or not drawn yet), with
    // bounds over the next grid row (report 0.2.172, HĐ 151/164). Clicking one clicked that row instead, and MISA
    // put the item on a second line. Only items whose point is drawn outside the grid's own window are options.
    // The list belongs to the voucher form, which R20 may open without owner (report 0.2.187): search all MISA windows.
    private AutomationElement[] RegisteredOptions(Rectangle cellBounds, nint gridWindow)
        => ProcessSnapshots(identity!.WindowHandle, identity.ProcessId,
                Live(automation.ConditionFactory.ByControlType(ControlType.ListItem), identity.ProcessId))
            .Where(item => item.HasArea
                && item.Bounds.Left < cellBounds.Right && item.Bounds.Right > cellBounds.Left
                && Math.Abs(item.Bounds.Top - cellBounds.Bottom) < 420
                && DrawnOutside(item.Bounds, gridWindow))
            .Select(item => item.Element)
            .Where(item => item.Patterns.SelectionItem.IsSupported)
            .ToArray();

    private static bool DrawnOutside(Rectangle bounds, nint gridWindow)
        => OutsideGrid(NativeKeyboard.WindowAt(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), gridWindow);

    // Some value lists expose text labels without ListItem/SelectionItem. Clicking a drawn label needs neither pattern.
    // Scope every candidate to the one popup beside this cell; never infer an option from its row number.
    private AutomationElement[] DrawnOptionLabels(Rectangle cellBounds, nint gridWindow, string expectedName)
    {
        var popup = ListPopupAt(cellBounds, gridWindow);
        if (popup == nint.Zero) return [];
        var labels = Snapshots(automation.FromHandle(popup), Live(
                AnyControlType(ControlType.ListItem, ControlType.Text), identity!.ProcessId))
            .Where(item => item.HasArea && NormalizeOptionName(item.Name) == expectedName
                && !item.Bounds.IntersectsWith(cellBounds)
                && item.Bounds.Left < cellBounds.Right && item.Bounds.Right > cellBounds.Left
                && Math.Abs(item.Bounds.Top - cellBounds.Bottom) < 420
                && (nint)PInvoke.GetAncestor((HWND)NativeKeyboard.WindowAt(
                    item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2),
                    GET_ANCESTOR_FLAGS.GA_ROOT) == popup)
            .ToArray();
        // A text child and its containing row describe one choice. Separate rows with the same label are ambiguous.
        return labels.Where(item => !labels.Any(other => other.Bounds != item.Bounds
                && item.Bounds.Contains(other.Bounds)))
            .GroupBy(item => item.Bounds).Select(group => group.First().Element).ToArray();
    }

    // Report 0.2.203, HĐ 18534: R20's "% thuế GTGT" list was drawn open but UIA gave none of its items. The open list
    // is a top-level popup of MISA drawn against the cell (above it when there is no room below).
    private bool ListDrawnAt(Rectangle cellBounds, nint gridWindow)
        => ListPopupAt(cellBounds, gridWindow) != nint.Zero;

    private nint ListPopupAt(Rectangle cellBounds, nint gridWindow)
    {
        var x = cellBounds.X + cellBounds.Width / 2;
        foreach (var y in new[] { cellBounds.Bottom + 6, cellBounds.Top - 6 })
        {
            var hit = NativeKeyboard.WindowAt(x, y);
            if (hit == nint.Zero) continue;
            PInvoke.GetWindowThreadProcessId((HWND)hit, out var processId);
            if (processId == identity!.ProcessId && InOwnPopup(hit, gridWindow))
                return (nint)PInvoke.GetAncestor((HWND)hit, GET_ANCESTOR_FLAGS.GA_ROOT);
        }
        return nint.Zero;
    }

    // What UIA lists near the cell, for reports when no option could be read.
    private string DescribeListItems(Rectangle cellBounds, nint gridWindow)
    {
        var items = ProcessSnapshots(identity!.WindowHandle, identity.ProcessId,
                Live(automation.ConditionFactory.ByControlType(ControlType.ListItem), identity.ProcessId, enabled: false, onscreen: false))
            .Where(item => Math.Abs(item.Bounds.Top - cellBounds.Bottom) < 420)
            .ToArray();
        if (items.Length == 0) return "UIA không có mục danh sách nào gần ô";
        return $"UIA có {items.Length} mục gần ô: " + string.Join("; ", items.Take(10).Select(item =>
            $"'{item.Name.Trim()}' {item.Bounds.X},{item.Bounds.Y} {item.Bounds.Width}x{item.Bounds.Height}"
            + $"{(item.Offscreen ? " offscreen" : "")}{(item.Enabled ? "" : " disabled")}"
            + $"{(item.HasArea && DrawnOutside(item.Bounds, gridWindow) ? "" : " trong-grid")}"
            + $"{(item.Element.Patterns.SelectionItem.IsSupported ? "" : " no-select")}"));
    }

    private static bool OutsideGrid(nint hit, nint gridWindow)
        => hit != nint.Zero && hit != gridWindow && !PInvoke.IsChild((HWND)gridWindow, (HWND)hit);

    // A combo's open list is its own top-level popup; a point in the combo's own window means the list has closed.
    private static bool InOwnPopup(nint hit, nint comboWindow)
        => hit != nint.Zero && PInvoke.GetAncestor((HWND)hit, GET_ANCESTOR_FLAGS.GA_ROOT)
            != PInvoke.GetAncestor((HWND)comboWindow, GET_ANCESTOR_FLAGS.GA_ROOT);

    // Open list items named like the option, anywhere in MISA's windows (the popup is a separate top-level window).
    private AutomationElement[] OpenOptions(string expectedName, nint comboWindow)
        => ProcessSnapshots(identity!.WindowHandle, identity.ProcessId,
                Live(automation.ConditionFactory.ByControlType(ControlType.ListItem), identity.ProcessId))
            .Where(item => item.HasArea && TextKey.Canonical(item.Name) == expectedName
                && InOwnPopup(NativeKeyboard.WindowAt(item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2), comboWindow))
            .Select(item => item.Element)
            .ToArray();

    private WindowIdentity RefreshWindow(WindowIdentity expected) => windowTracker.Refresh(expected);

    private WorkflowProfile? profile;

    /// <summary>Supplies the screen selectors used by workflow helpers (editor scope, prompts, unit column).</summary>
    public void UseProfile(WorkflowProfile value) => profile = value;

    private WorkflowProfile RequireProfile() => profile
        ?? throw new AutomationException("PROFILE_MISSING", "Chưa nạp profile MISA cho thao tác workflow.");

    // The voucher editor is an owned window but never a blocking dialog.
    private bool IsEditorScope(string? automationId)
        => profile is not null && string.Equals(automationId, profile.EditorId, StringComparison.OrdinalIgnoreCase);

    private string UnitColumnCaption => profile?.Columns.GetValueOrDefault("unit") ?? "ĐVT";
    private string? TaxRateColumnCaption => profile?.Columns.GetValueOrDefault("tax_rate");

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
            catch (ElementNotAvailableException)
            {
                // A child window can disappear between EnumWindows and UIA
                // binding while MISA recreates a grid editor.
                continue;
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
                if (IsEditorScope(window.Properties.AutomationId.ValueOrDefault)
                    || IsCatalogDialogName(window.Properties.Name.ValueOrDefault))
                    continue;
                if (window.Patterns.Window.IsSupported && window.Patterns.Window.Pattern.IsModal.Value
                    || string.Equals(window.Properties.ClassName.ValueOrDefault, "#32770", StringComparison.Ordinal))
                    return true;
            }
            catch (ElementNotAvailableException)
            {
                // The owned handle is stale; the next native operation will
                // resolve the current MISA surface again.
                continue;
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

    private AutomationElement ResolveInvokeTarget(NativeSelector selector)
    {
        if (selector.ByLabel) return Resolve(selector);
        if (root is null || identity is null) throw new AutomationException("NOT_CONNECTED", "Chưa chọn cửa sổ MISA.");
        var candidates = Snapshots(root, Live(SelectorCondition(selector), identity.ProcessId, enabled: false, onscreen: false))
            .Where(item => Matches(item, selector) && item.HasArea && !item.Offscreen)
            .Select(item => item.Element)
            .Where(element => element.Patterns.Invoke.IsSupported)
            .ToArray();
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length > 1)
            throw new AutomationException("TARGET_AMBIGUOUS", $"Cần đúng một native command: {selector.AutomationId}/{selector.Name}.");
        return Resolve(selector);
    }

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
            if (selector.ByLabel)
            {
                var labelled = LabelledMatches(found, selector);
                if (labelled.Length == 0) throw new AutomationException("TARGET_NOT_FOUND", $"Chưa có native target theo nhãn '{selector.Label}'.");
                if (labelled.Length != 1) throw new AutomationException("TARGET_AMBIGUOUS", $"Cần đúng một ô cạnh nhãn '{selector.Label}'.");
                return labelled[0].Element;
            }
            var elements = found.Where(item => Matches(item, selector)).ToArray();
            if (elements.Length == 0) throw new AutomationException("TARGET_NOT_FOUND", $"Chưa có native target: {selector.AutomationId}/{selector.Name}.");
            if (elements.Length != 1) throw new AutomationException("TARGET_AMBIGUOUS", $"Cần đúng một native target: {selector.AutomationId}/{selector.Name}.");
            return elements[0].Element;
        }).ToArray();
    }

    private ConditionBase SelectorCondition(NativeSelector selector)
    {
        var factory = automation.ConditionFactory;
        if (selector.ByLabel)
            return new OrCondition(factory.ByControlType(ControlType.Text), factory.ByControlType(LabelledType(selector)));
        ConditionBase condition = !string.IsNullOrWhiteSpace(selector.AutomationId) ? factory.ByAutomationId(selector.AutomationId)
            : !string.IsNullOrWhiteSpace(selector.Name) ? factory.ByName(selector.Name)
            : factory.ByControlType(Enum.Parse<ControlType>(selector.ControlType));
        if ((!string.IsNullOrWhiteSpace(selector.AutomationId) || !string.IsNullOrWhiteSpace(selector.Name))
            && Enum.TryParse<ControlType>(selector.ControlType, out var type) && type.ToString() == selector.ControlType)
            condition = condition.And(factory.ByControlType(type));
        return condition;
    }

    private static ControlType LabelledType(NativeSelector selector)
        => Enum.TryParse<ControlType>(selector.ControlType, out var type) && selector.ControlType.Length > 0 ? type : ControlType.Edit;

    // Captions compare without the required-field mark, a trailing colon or markup: "Mã (*)" = "Mã".
    private static string LabelKey(string? text) => TextKey.Compact(LabelNoise().Replace(text ?? "", ""));

    [System.Text.RegularExpressions.GeneratedRegex(@"<[^>]*>|\(\s*\*\s*\)|[*:]", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex LabelNoise();

    /// <summary>
    /// Controls of the selector's type on the row of its visible label, nearest to the right of it. A wrapper and its
    /// inner editor share the left edge; the innermost one is the control. More than one result means ambiguous.
    /// </summary>
    private static ElementSnapshot[] LabelledMatches(IReadOnlyList<ElementSnapshot> found, NativeSelector selector)
    {
        var key = LabelKey(selector.Label);
        var labels = found.Where(item => item.ControlType == ControlType.Text && !item.Offscreen && item.HasArea
            && LabelKey(item.Name) == key).ToArray();
        if (labels.Length != 1) return labels;
        var label = labels[0].Bounds;
        var middle = label.Top + label.Height / 2;
        var type = LabelledType(selector);
        var row = found.Where(item => item.ControlType == type && !item.Offscreen && item.HasArea
                && item.Bounds.Top <= middle && item.Bounds.Bottom >= middle && item.Bounds.Left >= label.Left + label.Width / 2)
            .OrderBy(item => item.Bounds.Left).ToArray();
        if (row.Length == 0) return [];
        var nearest = row.Where(item => item.Bounds.Left - row[0].Bounds.Left <= 2).ToArray();
        return nearest.Where(item => !nearest.Any(other => !ReferenceEquals(other, item) && other.Bounds != item.Bounds
            && item.Bounds.Contains(other.Bounds))).ToArray();
    }

    private static bool Matches(ElementSnapshot item, NativeSelector selector, bool requireVisible = true)
        => (!string.IsNullOrWhiteSpace(selector.AutomationId) ? item.AutomationId == selector.AutomationId
                : string.IsNullOrWhiteSpace(selector.Name) || item.Name == selector.Name)
            && (selector.Name.Length == 0 || item.Name == selector.Name)
            && (selector.ControlType.Length == 0 || item.ControlType.ToString() == selector.ControlType)
            && (!requireVisible || item.Enabled && !item.Offscreen && item.HasArea);
}
