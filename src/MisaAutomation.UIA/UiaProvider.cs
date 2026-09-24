using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using FlaUI.UIA3;
using MisaAutomation.Core;
using GridRow = MisaAutomation.Core.GridRow;
using System.Runtime.InteropServices;

namespace MisaAutomation.UIA;

public sealed class UiaProvider : IAutomationProvider
{
    private readonly UIA3Automation automation = new();
    private readonly LocatorRepository locators;
    private readonly IAutomationLog log;
    private readonly bool subscribeEvents;
    private readonly bool checkPermission;
    private readonly ElementCache cache = new();
    private readonly List<EventHandlerBase> subscriptions = new();
    private AutomationElement? root;
    private Mutex? processLease;
    private bool ownsLease;
    private int treeChanged;
    public WindowInfo? Window { get; private set; }
    public ChangeSignal Changes { get; } = new();

    public UiaProvider(LocatorRepository locators, IAutomationLog log, bool subscribeEvents = true,
        bool checkPermission = true)
    {
        this.locators = locators;
        this.log = log;
        this.subscribeEvents = subscribeEvents;
        this.checkPermission = checkPermission;
        automation.TransactionTimeout = TimeSpan.FromSeconds(3);
        automation.ConnectionTimeout = TimeSpan.FromSeconds(3);
    }

    public WindowInfo Connect(long? windowHandle, int? processId)
    {
        if (Window != null)
            throw new AutomationException("ALREADY_CONNECTED", "Disconnect before selecting another MISA session.");
        var windows = WindowContext.List(processId);
        WindowInfo selected;
        if (windowHandle.HasValue) selected = WindowContext.Read(windowHandle.Value, processId);
        else
        {
            if (windows.Count == 0) throw new AutomationException("MISA_NOT_RUNNING", "No MISA window is open.");
            if (windows.Select(w => w.ProcessId).Distinct().Count() != 1)
                throw new AutomationException("AMBIGUOUS_MISA", "Select a MISA process explicitly.");
            var foreground = WindowContext.GetForegroundWindow().ToInt64();
            selected = windows.FirstOrDefault(w => w.Handle == foreground)
                ?? (windows.Count == 1 ? windows[0] : throw new AutomationException("AMBIGUOUS_WINDOW", "Select the current MISA form explicitly."));
        }
        if (checkPermission) WindowContext.CheckPermission(selected.ProcessId);
        processLease = new Mutex(false, "Local\\MisaAutomation-" + selected.ProcessId);
        try { ownsLease = processLease.WaitOne(0); }
        catch (AbandonedMutexException) { ownsLease = true; }
        if (!ownsLease) throw new AutomationException("MISA_BUSY", "Another automation engine owns this MISA process.");
        Window = selected;
        return BindWindow(selected.Handle);
    }

    public WindowInfo BindWindow(long handle)
    {
        var selected = WindowContext.Read(handle, Window?.ProcessId
            ?? throw new AutomationException("NOT_CONNECTED", "Connect first."));
        if (root != null && Window?.Handle == handle) { Window = selected; return selected; }
        Unsubscribe();
        cache.Clear();
        root = automation.FromHandle((nint)handle);
        Window = selected;
        if (subscribeEvents) Subscribe();
        Changes.Pulse();
        return selected;
    }

    private void Subscribe()
    {
        void Changed(bool structural)
        {
            if (structural) Interlocked.Exchange(ref treeChanged, 1);
            Changes.Pulse();
        }
        void Register(string name, Func<EventHandlerBase> register)
        {
            try { subscriptions.Add(register()); }
            catch (Exception error) { log.Write("event_registration_failed", new { name, error = error.ToString() }); }
        }
        Register("structure", () => root!.RegisterStructureChangedEvent(TreeScope.Subtree, (_, _, _) => Changed(true)));
        Register("property", () => root!.RegisterPropertyChangedEvent(TreeScope.Subtree,
            (_, _, _) => Changed(false), automation.PropertyLibrary.Element.IsEnabled,
            automation.PropertyLibrary.Element.Name, automation.PropertyLibrary.Element.IsOffscreen,
            automation.PropertyLibrary.Value.Value, automation.PropertyLibrary.SelectionItem.IsSelected));
        Register("window_opened", () => root!.RegisterAutomationEvent(automation.EventLibrary.Window.WindowOpenedEvent,
            TreeScope.Subtree, (_, _) => Changed(true)));
        Register("window_closed", () => root!.RegisterAutomationEvent(automation.EventLibrary.Window.WindowClosedEvent,
            TreeScope.Subtree, (_, _) => Changed(true)));
        Register("selection", () => root!.RegisterAutomationEvent(automation.EventLibrary.SelectionItem.ElementSelectedEvent,
            TreeScope.Subtree, (_, _) => Changed(false)));
        Register("focus", () => automation.RegisterFocusChangedEvent(_ => Changed(false)));
    }

    private void Unsubscribe()
    {
        foreach (var handler in subscriptions)
        {
            try { handler.Dispose(); }
            catch (Exception error) { log.Write("event_dispose_failed", new { error = error.ToString() }); }
        }
        subscriptions.Clear();
    }

    private AutomationElement Root()
    {
        if (Window == null || root == null) throw new AutomationException("NOT_CONNECTED", "Connect to MISA first.");
        Window = WindowContext.Read(Window.Handle, Window.ProcessId);
        if (Interlocked.Exchange(ref treeChanged, 0) != 0) cache.Clear();
        return root;
    }

    public IReadOnlyList<WindowInfo> Windows() => WindowContext.List(Window?.ProcessId);
    public void Invalidate() { cache.Clear(); Changes.Pulse(); }

    public IReadOnlyList<UiaTreeNode> DumpTree(int maximumNodes = 10000)
    {
        if (maximumNodes is < 1 or > 50000)
            throw new AutomationException("INVALID_TREE_LIMIT", "UIA tree limit must be between 1 and 50000 nodes.");
        var tree = new List<UiaTreeNode>();
        var pending = new Queue<(AutomationElement Element, string? ParentToken,
            long ParentHandle, int Depth)>();
        pending.Enqueue((Root(), null, 0, 0));
        while (pending.Count > 0)
        {
            if (tree.Count >= maximumNodes)
                throw new AutomationException("UIA_TREE_TOO_LARGE", "UIA tree exceeded the configured node limit.");
            var current = pending.Dequeue();
            var token = cache.Store(current.Element, Window!.Handle);
            var handle = current.Element.Properties.NativeWindowHandle.ValueOrDefault.ToInt64();
            tree.Add(new UiaTreeNode(Describe(current.Element, token, current.ParentToken),
                handle, current.ParentHandle, current.Depth));
            var children = current.Element.FindAll(TreeScope.Children, new BoolCondition(true));
            foreach (var child in children)
                pending.Enqueue((child, token, handle, current.Depth + 1));
        }
        return tree;
    }

    public string WindowText(long handle)
    {
        WindowContext.Read(handle, Window?.ProcessId ?? throw new AutomationException("NOT_CONNECTED", "Connect first."));
        var window = automation.FromHandle((nint)handle);
        var text = window.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Text));
        return string.Join("\n", text.Take(100).Select(e => e.Properties.Name.ValueOrDefault).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public Inspection InspectPoint(int x, int y)
    {
        Root();
        var element = automation.FromPoint(new System.Drawing.Point(x, y));
        if (element.Properties.ProcessId.Value != Window!.ProcessId)
            throw new AutomationException("NOT_MISA", "Pointer is outside the connected MISA process.");
        var parents = new List<AutomationElement>();
        var ancestor = element;
        var foundWindow = false;
        for (var depth = 0; depth < 40; depth++)
        {
            var handle = ancestor.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle != 0 && WindowContext.GetAncestor(handle, 2) == handle)
            {
                BindWindow(handle.ToInt64());
                foundWindow = true;
                break;
            }
            var parent = ancestor.Parent;
            if (parent == null || parent.Properties.ProcessId.ValueOrDefault != Window.ProcessId)
                throw new AutomationException("WINDOW_UNREADABLE", "Cannot establish the hovered control's window.");
            parents.Add(parent);
            ancestor = parent;
        }
        if (!foundWindow) throw new AutomationException("WINDOW_UNREADABLE", "Hovered control exceeds the bounded hierarchy depth.");
        return new Inspection(Describe(element, cache.Store(element, Window.Handle)),
            parents.Select(p => Describe(p, cache.Store(p, Window.Handle))).ToArray(), Window);
    }

    public GridPage ReadGrid(string token, int startRow, int maximumRows)
    {
        Root();
        if (startRow < 0 || maximumRows is < 1 or > 100)
            throw new AutomationException("INVALID_GRID_RANGE", "Read 1..100 rows from a nonnegative row index.");
        var grid = cache.Get(token, Window!.Handle);
        if (!grid.Patterns.Grid.IsSupported)
            throw new AutomationException("PATTERN_UNSUPPORTED", "GridPattern is unavailable; inspect semantic child cells before keyboard fallback.");
        var pattern = grid.Patterns.Grid.Pattern;
        var rowCount = pattern.RowCount.Value;
        var columnCount = pattern.ColumnCount.Value;
        if (columnCount is < 1 or > 100 || rowCount < 0)
            throw new AutomationException("INVALID_GRID_SHAPE", "The UIA grid reports an invalid shape.");
        var headers = grid.Patterns.Table.IsSupported
            ? grid.Patterns.Table.Pattern.ColumnHeaders.Value
            : grid.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.HeaderItem))
                .Select(header => new { Element = header,
                    Bounds = Safe(() => header.BoundingRectangle, System.Drawing.Rectangle.Empty) })
                .Where(item => item.Bounds.Width > 0 && item.Bounds.Height > 0)
                .OrderBy(item => item.Bounds.Left)
                .ThenBy(item => item.Bounds.Top)
                .Select(item => item.Element)
                .ToArray();
        var names = headers.Select(h => h.Name).ToArray();
        if (names.Length != columnCount || names.Any(string.IsNullOrWhiteSpace) || names.Distinct().Count() != columnCount)
            throw new AutomationException("GRID_HEADERS_UNREADABLE", "Each grid column needs a unique accessible header.");
        if (!grid.Patterns.Table.IsSupported)
        {
            var boxes = headers.Select(header =>
                Safe(() => header.BoundingRectangle, System.Drawing.Rectangle.Empty)).ToArray();
            if (boxes.Zip(boxes.Skip(1), (left, right) => left.Right > right.Left).Any(overlap => overlap))
                throw new AutomationException("GRID_HEADERS_UNREADABLE",
                    "Fallback UIA column headers overlap or are not ordered by current geometry.");
        }
        var rows = new List<GridRow>();
        for (var row = startRow; row < Math.Min((long)rowCount, (long)startRow + maximumRows); row++)
        {
            var cells = new List<ElementInfo>();
            ElementInfo? rowElement = null;
            for (var column = 0; column < columnCount; column++)
            {
                var cell = pattern.GetItem(row, column);
                if (!cell.Patterns.GridItem.IsSupported || cell.Patterns.GridItem.Pattern.Row.Value != row
                    || cell.Patterns.GridItem.Pattern.Column.Value != column)
                    throw new AutomationException("GRID_IDENTITY_CHANGED", "UIA returned a cell from another row or column.");
                // Preserve the live cell parent chain. GridPattern cells can be
                // virtualized, so the caller must be able to re-read the row
                // identity instead of treating the grid itself as the parent.
                cells.Add(DescribeCurrent(cell, cache.Store(cell, Window.Handle)) with { Name = names[column] });
                var rowParent = FindDataItemAncestor(cell, grid);
                var currentRowElement = rowParent == null
                    ? null
                    : DescribeCurrent(rowParent, cache.Store(rowParent, Window.Handle));
                if (currentRowElement == null)
                    throw new AutomationException("GRID_IDENTITY_CHANGED",
                        "UIA grid cell has no current DataItem row parent.");
                if (column == 0)
                {
                    rowElement = currentRowElement;
                }
                else if ((rowElement?.Token) != (currentRowElement?.Token))
                {
                    throw new AutomationException("GRID_IDENTITY_CHANGED",
                        "UIA cells in one row do not share the same DataItem parent.");
                }
            }
            rows.Add(new GridRow(row, cells, rowElement));
        }
        if (pattern.RowCount.Value != rowCount || pattern.ColumnCount.Value != columnCount)
            throw new AutomationException("GRID_CHANGED", "Grid shape changed during readback.");
        return new GridPage(rowCount, columnCount, names, rows, "UIA3.Grid+Table");
    }

    private AutomationElement? FindDataItemAncestor(AutomationElement element, AutomationElement grid)
    {
        var parent = element.Parent;
        for (var depth = 0; parent != null && depth < 8; depth++, parent = parent.Parent)
        {
            if (parent.ControlType == ControlType.DataItem) return parent;
            if (parent.Equals(grid) || parent.Properties.ProcessId.ValueOrDefault != Window!.ProcessId)
                break;
        }
        return null;
    }

    public ElementInfo Find(string key, Locator locator, CancellationToken cancellationToken)
    {
        var scope = Root();
        cancellationToken.ThrowIfCancellationRequested();
        if (locator.WindowKind != null && Window!.Kind != locator.WindowKind)
            throw new AutomationException("WRONG_WINDOW", "Locator belongs to window kind " + locator.WindowKind);
        var cached = cache.Find(key, Window!.Handle);
        if (cached != null)
        {
            var observed = Read(cached);
            if (!locator.VisibleOnly || ReadOffscreen(cache.Get(cached, Window.Handle)) != true) return observed;
        }
        if (locator.Parent != null)
            scope = cache.Get(Find(locator.Parent, locators[locator.Parent], cancellationToken).Token, Window.Handle);
        if (locator.Anchor != null)
        {
            var anchor = cache.Get(Find(locator.Anchor, locators[locator.Anchor], cancellationToken).Token, Window.Handle);
            scope = anchor.Parent ?? throw new AutomationException("NOT_FOUND", "Anchor has no accessible parent.");
        }
        var cf = automation.ConditionFactory;
        var strategies = new List<(string Name, ConditionBase Condition)>();
        if (!string.IsNullOrEmpty(locator.AutomationId))
        {
            strategies.Add(("AutomationId", cf.ByAutomationId(locator.AutomationId)));
            if (locator.ControlType.HasValue)
                strategies.Add(("AutomationId+ControlType", cf.ByAutomationId(locator.AutomationId).And(cf.ByControlType(ControlKinds.FromNative(locator.ControlType.Value)))));
        }
        if (!string.IsNullOrEmpty(locator.Name) && locator.ControlType.HasValue)
            strategies.Add(("Name+ControlType", cf.ByName(locator.Name).And(cf.ByControlType(ControlKinds.FromNative(locator.ControlType.Value)))));
        if (!string.IsNullOrEmpty(locator.ClassName)) strategies.Add(("ClassName", cf.ByClassName(locator.ClassName)));
        foreach (var strategy in strategies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = scope.FindAllDescendants(strategy.Condition)
                .Where(e => (!locator.VisibleOnly || ReadOffscreen(e) != true)
                    && (!locator.ControlType.HasValue || ControlKinds.ToNative(e.ControlType) == locator.ControlType.Value)).ToArray();
            log.Write("locator_strategy", new { key, strategy = strategy.Name, count = candidates.Length, window = Window.Handle });
            if (candidates.Length > 1)
                throw new AutomationException("AMBIGUOUS_ELEMENT", "Multiple controls match " + key + " using " + strategy.Name);
            if (candidates.Length == 1)
            {
                if (strategy != strategies[0]) log.Write("locator_fallback_suggestion", new { key, strategy = strategy.Name });
                return DescribeCurrent(candidates[0], cache.Store(candidates[0], Window.Handle, key));
            }
        }
        throw new AutomationException("NOT_FOUND", "No unique accessible element for " + key);
    }

    public IReadOnlyList<ElementInfo> Query(ElementQuery query)
    {
        var scope = Root();
        if (query.ParentToken != null) scope = cache.Get(query.ParentToken, Window!.Handle);
        var cf = automation.ConditionFactory;
        ConditionBase condition = new BoolCondition(true);
        if (query.Ids is { Length: > 0 }) condition = condition.And(cf.ByAutomationId(query.Ids[0]));
        if (query.Ids is { Length: > 1 }) condition = new OrCondition(query.Ids.Select(id => cf.ByAutomationId(id)).ToArray());
        if (query.Names is { Length: > 0 }) condition = condition.And(new OrCondition(query.Names.Select(n => cf.ByName(n)).ToArray()));
        if (query.Kinds is { Length: > 0 }) condition = condition.And(new OrCondition(query.Kinds.Select(k => cf.ByControlType(ControlKinds.FromNative(k))).ToArray()));
        var found = scope.FindAll(query.ChildrenOnly ? TreeScope.Children : TreeScope.Descendants, condition);
        if (found.Length > 2048) throw new AutomationException("QUERY_TOO_BROAD", "Narrow the UIA query to a parent or control type.");
        return found.Select(e => DescribeCurrent(e, cache.Store(e, Window!.Handle))).ToArray();
    }

    public ElementInfo Read(string token)
    {
        Root();
        return DescribeCurrent(cache.Get(token, Window!.Handle), token);
    }

    private ElementInfo DescribeCurrent(AutomationElement element, string token)
    {
        string? parentToken = null;
        try
        {
            var parent = element.Parent;
            if (parent != null && parent.Properties.ProcessId.ValueOrDefault == Window!.ProcessId)
                parentToken = cache.Store(parent, Window.Handle);
        }
        catch (Exception error) when (error is FlaUI.Core.Exceptions.PropertyNotSupportedException
            or InvalidOperationException or COMException)
        {
            log.Write("property_read_failed", new { property = "parent", error = error.Message });
        }
        return Describe(element, token, parentToken);
    }

    private ElementInfo Describe(AutomationElement element, string token, string? parent = null)
    {
        var p = element.Properties;
        var rectangle = Safe(() => element.BoundingRectangle, System.Drawing.Rectangle.Empty);
        var automationId = Safe(() => p.AutomationId.ValueOrDefault, "") ?? "";
        var name = Safe(() => p.Name.ValueOrDefault, "") ?? "";
        var className = Safe(() => p.ClassName.ValueOrDefault, "") ?? "";
        var enabled = p.IsEnabled.TryGetValue(out var enabledValue) && enabledValue;
        var offscreen = ReadOffscreen(element) ?? false;
        string? value = null;
        try
        {
            if (element.Patterns.Value.IsSupported) value = element.Patterns.Value.Pattern.Value.Value;
            else if (element.Patterns.Text.IsSupported) value = element.Patterns.Text.Pattern.DocumentRange.GetText(32768);
            else if (element.ControlType is ControlType.Text or ControlType.ListItem) value = name;

            // MISA grid cells can expose a selected-item GUID through ValuePattern
            // while rendering the caption through a text pattern or child node.
            if (value != null && Guid.TryParse(value.Trim(), out _))
                value = ReadDisplayedCaption(element, rectangle) ?? value;
        }
        catch (Exception error) when (error is FlaUI.Core.Exceptions.PropertyNotSupportedException
            or InvalidOperationException or COMException)
        {
            log.Write("property_read_failed", new { property = "value", error = error.Message });
        }
        var patterns = Safe(() => element.GetSupportedPatterns().Select(pattern => pattern.Name).ToArray(), Array.Empty<string>());
        var selected = Safe(() => element.Patterns.SelectionItem.IsSupported
            && element.Patterns.SelectionItem.Pattern.IsSelected.Value, false);
        var toggle = Safe(() => element.Patterns.Toggle.IsSupported
            ? (int)element.Patterns.Toggle.Pattern.ToggleState.Value : 0, 0);
        return new ElementInfo(token, automationId, name,
            ControlKinds.ToNative(element.ControlType), className,
            new Bounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), enabled,
            offscreen, selected, toggle, value, Window!.Handle, patterns, parent);
    }

    private static string? ReadDisplayedCaption(AutomationElement element, System.Drawing.Rectangle box)
    {
        var captions = new List<string>();

        void Add(string? raw)
        {
            var text = raw?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && !Guid.TryParse(text, out _)
                && !captions.Contains(text, StringComparer.Ordinal))
                captions.Add(text);
        }

        if (captions.Count == 0)
        {
            try
            {
                if (element.Patterns.LegacyIAccessible.IsSupported)
                    Add(element.Patterns.LegacyIAccessible.Pattern.Value);
            }
            catch (Exception error) when (error is FlaUI.Core.Exceptions.PropertyNotSupportedException
                or InvalidOperationException or COMException)
            {
            }
        }

        if (captions.Count == 0)
        {
            foreach (var child in element.FindAllDescendants())
            {
                if (child.ControlType is not (ControlType.Text or ControlType.ListItem
                    or ControlType.Edit or ControlType.Custom)) continue;
                var childBox = Safe(() => child.BoundingRectangle, System.Drawing.Rectangle.Empty);
                if (childBox.Width <= 0 || childBox.Height <= 0
                    || childBox.Left < box.Left || childBox.Top < box.Top
                    || childBox.Right > box.Right || childBox.Bottom > box.Bottom)
                    continue;
                try
                {
                    if (child.Patterns.Value.IsSupported)
                        Add(child.Patterns.Value.Pattern.Value.Value);
                    else if (child.Patterns.Text.IsSupported)
                        Add(child.Patterns.Text.Pattern.DocumentRange.GetText(32768));
                }
                catch (Exception error) when (error is FlaUI.Core.Exceptions.PropertyNotSupportedException
                    or InvalidOperationException or COMException)
                {
                }
                Add(Safe(() => child.Properties.Name.ValueOrDefault, ""));
                if (captions.Count > 1) break;
            }
        }

        return captions.Count == 1 ? captions[0] : null;
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception error) when (error is FlaUI.Core.Exceptions.PropertyNotSupportedException
            or InvalidOperationException or COMException)
        { return fallback; }
    }

    private static bool? ReadOffscreen(AutomationElement element)
    {
        try
        {
            return element.Properties.IsOffscreen.TryGetValue(out var value) ? value : null;
        }
        catch (Exception error) when (error is FlaUI.Core.Exceptions.PropertyNotSupportedException
            or InvalidOperationException or COMException)
        {
            // Some MISA controls do not expose IsOffscreen; bounds and window guards remain authoritative.
            return null;
        }
    }

    private bool? ObservationVisibility(AutomationElement element, Bounds bounds, out string source)
    {
        source = "UIA3.IsOffscreen";
        var offscreen = ReadOffscreen(element);
        if (offscreen.HasValue) return !offscreen.Value;
        source = "Win32.IsWindowVisible";
        if (element.Properties.NativeWindowHandle.TryGetValue(out var handle)
            && WindowContext.TryReadControlVisibility((nint)handle, Window!, bounds, out var visible)
            && automation.FromHandle((nint)handle).Equals(element))
            return visible;
        // Unsupported is unknown, never the bool default (visible).
        return null;
    }

    public ValueObservation ObserveValue(Bounds bounds, string? automationId, int? row)
        => ObserveValue(bounds, automationId, row, out _);

    private ValueObservation ObserveValue(Bounds bounds, string? automationId, int? row,
        out AutomationElement? matchedElement)
    {
        matchedElement = null;
        var scope = Root();
        var observedAt = DateTimeOffset.UtcNow;
        ValueObservation Result(string status, string? value, string source, Bounds? box, string reason)
            => new(status, value, source, observedAt, Window!.Handle, Window.ProcessId, box, reason);
        if (bounds.Width <= 0 || bounds.Height <= 0 || row is <= 0)
            throw new AutomationException("INVALID_OBSERVATION", "Invalid native control bounds or row.");
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            throw new AutomationException("INVALID_OBSERVATION", "Native bounds must be finite.");
        var cf = automation.ConditionFactory;
        var condition = string.IsNullOrEmpty(automationId) ? new BoolCondition(true)
            : (ConditionBase)cf.ByAutomationId(automationId);
        bool MatchesRow(AutomationElement element)
        {
            if (!row.HasValue) return true;
            if (element.Patterns.GridItem.IsSupported)
                return element.Patterns.GridItem.Pattern.Row.Value == row.Value - 1;
            var parent = element.Parent;
            for (var depth = 0; parent != null && depth < 8; parent = parent.Parent, depth++)
            {
                if (parent.ControlType == ControlType.DataItem)
                    return int.TryParse(parent.Properties.AutomationId.ValueOrDefault, out var index)
                        && index == row.Value - 1;
                if (parent.Equals(scope)) break;
            }
            // Custom MISA grids expose row editors without GridItem/DataItem
            // identity. The current native bounds still select one visible editor;
            // geometry uniqueness is validated again after the read/write.
            return true;
        }
        var candidates = new List<(AutomationElement Element, Bounds Bounds,
            string VisibilitySource, bool? Visible)>();
        foreach (var element in scope.FindAllDescendants(condition))
        {
            if (element.Properties.ProcessId.ValueOrDefault != Window!.ProcessId) continue;
            if (ReadOffscreen(element) == true) continue;
            var box = element.BoundingRectangle;
            var cx = bounds.X + bounds.Width / 2;
            var cy = bounds.Y + bounds.Height / 2;
            // Native bounds supply the target. UIA may corroborate only this same control.
            if (box.Width <= 0 || box.Height <= 0 || cx < box.Left || cx > box.Right
                || cy < box.Top || cy > box.Bottom || box.Width > bounds.Width + 24
                || box.Height > bounds.Height + 16) continue;
            if (!(element.Patterns.Value.IsSupported || element.Patterns.Text.IsSupported)
                || !MatchesRow(element)) continue;
            var candidateBounds = new Bounds(box.X, box.Y, box.Width, box.Height);
            var visible = ObservationVisibility(element, candidateBounds, out var visibilitySource);
            if (visible != false)
                candidates.Add((element, candidateBounds, visibilitySource, visible));
        }
        if (candidates.Count == 0)
            return Result("unavailable", null, "", null, "No supported scalar control at the native target with matching identity.");

        bool IsAncestor(AutomationElement ancestor, AutomationElement descendant)
        {
            for (var parent = descendant.Parent; parent != null; parent = parent.Parent)
            {
                if (parent.Equals(ancestor)) return true;
                if (parent.Equals(scope)) return false;
            }
            return false;
        }

        // WinForms may expose both an editor and one or more value-capable
        // wrappers at the same native target. Collapse only a single nested chain;
        // separate overlapping controls remain ambiguous.
        var leaves = candidates.Where(candidate => !candidates.Any(other =>
            !other.Element.Equals(candidate.Element)
            && IsAncestor(candidate.Element, other.Element))).ToList();
        if (leaves.Count != 1 || candidates.Any(other =>
                !other.Element.Equals(leaves[0].Element)
                && !IsAncestor(other.Element, leaves[0].Element)))
            return Result("ambiguous", null, "", null,
                "Supported controls at the native target do not form one nested editor chain.");
        var candidate = leaves[0];
        if (candidate.Visible != true)
            return Result("unavailable", null, "", null,
                "IsOffscreen is unsupported and native control visibility could not be established at the target.");
        var chosen = candidate.Element;
        var rectangle = chosen.BoundingRectangle;
        if (new Bounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height) != candidate.Bounds)
            return Result("ambiguous", null, "", null, "Control moved after matching the native target.");
        var source = chosen.Patterns.Value.IsSupported ? "ValuePattern" : "TextPattern";
        var value = chosen.Patterns.Value.IsSupported ? chosen.Patterns.Value.Pattern.Value.Value
            : chosen.Patterns.Text.Pattern.DocumentRange.GetText(32768);
        Root();
        var visibleAfter = ObservationVisibility(chosen, candidate.Bounds, out var visibilityAfter);
        source += "; visibility=" + candidate.VisibilitySource + "/" + visibilityAfter;
        if (visibleAfter != true || chosen.Properties.ProcessId.ValueOrDefault != Window!.ProcessId
            || !MatchesRow(chosen)
            || chosen.BoundingRectangle != rectangle)
            return Result("ambiguous", null, source, null, "Control visibility, identity or bounds changed during observation.");
        if (Guid.TryParse(value, out _))
        {
            // Combo/grid editors can expose an internal selected-item GUID through
            // ValuePattern while publishing the visible caption through a child.
            var displayName = ReadDisplayedCaption(chosen, rectangle) ?? "";
            if (!string.IsNullOrWhiteSpace(displayName)
                && !Guid.TryParse(displayName.Trim(), out _))
                return Result("supported", displayName.Trim(), source + "; displayed",
                    new Bounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), "");
            return Result("unavailable", value, source, null, "Internal GUID is not displayed text.");
        }
        matchedElement = chosen;
        return Result("supported", value, source,
            new Bounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), "");
    }

    public string SetObservedValue(ObservedValueTarget target, string value, string workflow)
    {
        if (target == null || target.Bounds == null || target.WindowBounds == null)
            throw new AutomationException("INVALID_VALUE_TARGET", "Value input requires a current native target.");
        if (string.IsNullOrEmpty(target.Purpose))
            throw new AutomationException("INVALID_VALUE_TARGET", "Value input needs a target purpose.");
        if (string.IsNullOrEmpty(workflow) || !workflow.StartsWith("add.", StringComparison.Ordinal))
            throw new AutomationException("INVALID_VALUE_TARGET", "Value input is outside the Add workflow.");
        if (value.Length > 32768 || value.Any(char.IsControl))
            throw new AutomationException("INVALID_TEXT", "A scalar field cannot contain control characters or exceed 32768 characters.");
        if (target.Row is <= 0)
            throw new AutomationException("INVALID_VALUE_TARGET", "Observed row must be positive.");

        void GuardWindow()
        {
            Root();
            var age = DateTimeOffset.UtcNow - target.ObservedAt;
            if (Window!.Handle != target.WindowId || Window.ProcessId != target.ProcessId
                || !Window.Enabled || WindowContext.GetForegroundWindow().ToInt64() != Window.Handle
                || !WindowContext.MatchesWindowBounds(Window, target.WindowBounds)
                || age < TimeSpan.Zero || age > TimeSpan.FromSeconds(30))
                throw new AutomationException("STALE_VALUE_TARGET", "Value input needs the same foreground window and a fresh native frame.");
        }

        GuardWindow();
        var allowed = Window!.Kind switch
        {
            "new_item" => workflow.StartsWith("add.items.", StringComparison.Ordinal)
                && target.Purpose is ("field:txtInventoryItemCode" or "field:txtInventoryItemName"),
            "find" => workflow.StartsWith("add.items.", StringComparison.Ordinal)
                && target.Purpose.StartsWith("find_filter:", StringComparison.Ordinal),
            "sale" => (workflow == "add.customer" && target.Purpose == "field:cboAccountObjectID")
                || (workflow == "add.invoice" && target.Purpose == "field:txtInvoiceNo")
                || (workflow.StartsWith("add.items.", StringComparison.Ordinal)
                    && (target.Purpose is ("sale_item_cell:soluong" or "sale_item_cell:thanhtien")
                        || (workflow.EndsWith(".tax", StringComparison.Ordinal)
                            && target.Purpose == "sale_item_cell:thuesuatgtgt"))),
            _ => false
        };
        if (!allowed)
            throw new AutomationException("INVALID_VALUE_TARGET", "The observed editor is not writable at this checkpoint.");
        var bounds = target.Bounds;
        var windowBounds = target.WindowBounds;
        if (bounds.X < windowBounds.X || bounds.Y < windowBounds.Y
            || bounds.X + bounds.Width > windowBounds.X + windowBounds.Width
            || bounds.Y + bounds.Height > windowBounds.Y + windowBounds.Height)
            throw new AutomationException("INVALID_VALUE_TARGET", "The native value field is outside its window.");
        var before = ObserveValue(bounds, target.AutomationId, target.Row, out var element);
        if (before.Status != "supported" || element == null || before.Bounds == null)
            throw new AutomationException("VALUE_TARGET_UNAVAILABLE", "Cannot resolve a unique editor at the native target: " + before.Reason);
        if (!element.Patterns.Value.IsSupported)
            throw new AutomationException("PATTERN_UNSUPPORTED", "The observed editor does not support ValuePattern.SetValue.");
        if (element.Patterns.Value.Pattern.IsReadOnly.Value)
            throw new AutomationException("READ_ONLY", "The observed editor is read-only.");
        var enabledKnown = element.Properties.IsEnabled.TryGetValue(out var enabled);
        if (!enabledKnown || !enabled)
            throw new AutomationException("ELEMENT_UNAVAILABLE", "The observed editor is not confirmed enabled.");
        GuardProtected(element, "set");
        GuardWindow();
        var rectangle = element.BoundingRectangle;
        if (element.Properties.ProcessId.ValueOrDefault != target.ProcessId
            || (target.AutomationId != null
                && element.Properties.AutomationId.ValueOrDefault != target.AutomationId)
            || new Bounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height) != before.Bounds
            || ObservationVisibility(element, before.Bounds, out _) != true)
            throw new AutomationException("STALE_VALUE_TARGET", "Value editor identity, visibility or bounds changed before input.");
        try
        {
            element.Focus();
            element.Patterns.Value.Pattern.SetValue(value);
            Changes.Pulse();
            return "UIA3.Value.Observed";
        }
        catch (AutomationException) { throw; }
        catch (Exception error)
        {
            throw new AutomationException("INPUT_UNCERTAIN", "Value input may have reached MISA; read back before continuing.", true, error);
        }
    }

    public string SetObservedDate(ObservedDateTarget target, string value, string workflow)
    {
        var expectedId = workflow switch
        {
            "add.posted_date" => "dtePostedDate",
            "add.voucher_date" => "dteRefDate",
            _ => throw new AutomationException("INVALID_DATE_TARGET", "Date input is outside the current sales checkpoint.")
        };
        if (target.AutomationId != expectedId || target.Bounds == null || target.WindowBounds == null)
            throw new AutomationException("INVALID_DATE_TARGET", "Date input requires the native field for this checkpoint.");
        if (value.Length != 10 || !DateTime.TryParseExact(value, "dd'/'MM'/'yyyy",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new AutomationException("INVALID_DATE", "Date input must be a valid dd/MM/yyyy value.");

        void GuardWindow()
        {
            Root();
            var age = DateTimeOffset.UtcNow - target.ObservedAt;
            if (Window!.Handle != target.WindowId || Window.ProcessId != target.ProcessId || Window.Kind != "sale"
                || !Window.Enabled || WindowContext.GetForegroundWindow().ToInt64() != Window.Handle
                || !WindowContext.MatchesWindowBounds(Window, target.WindowBounds)
                || age < TimeSpan.Zero || age > TimeSpan.FromSeconds(30))
                throw new AutomationException("STALE_DATE_TARGET", "Date input needs the same foreground window and a fresh native frame.");
        }

        GuardWindow();
        var bounds = target.Bounds;
        var windowBounds = target.WindowBounds;
        if (bounds.X < windowBounds.X || bounds.Y < windowBounds.Y
            || bounds.X + bounds.Width > windowBounds.X + windowBounds.Width
            || bounds.Y + bounds.Height > windowBounds.Y + windowBounds.Height)
            throw new AutomationException("INVALID_DATE_TARGET", "The native date field is outside its window.");
        var before = ObserveValue(bounds, target.AutomationId, null, out var element);
        if (before.Status != "supported" || element == null || before.Bounds == null)
            throw new AutomationException("DATE_TARGET_UNAVAILABLE", "Cannot resolve a unique date editor at the native target: " + before.Reason);
        if (!element.Patterns.Value.IsSupported)
            throw new AutomationException("PATTERN_UNSUPPORTED", "The date editor does not support ValuePattern.SetValue.");
        var pattern = element.Patterns.Value.Pattern;
        if (pattern.IsReadOnly.Value)
            throw new AutomationException("READ_ONLY", "The date editor is read-only.");
        var enabledKnown = element.Properties.IsEnabled.TryGetValue(out var enabled);
        if (!enabledKnown && element.Properties.NativeWindowHandle.TryGetValue(out var handle)
            && automation.FromHandle((nint)handle).Equals(element))
            enabledKnown = WindowContext.TryReadControlEnabled((nint)handle, Window!, before.Bounds, out enabled);
        if (!enabledKnown || !enabled)
            throw new AutomationException("ELEMENT_UNAVAILABLE", "The date editor is not confirmed enabled.");
        GuardProtected(element, "set");
        GuardWindow();
        var rectangle = element.BoundingRectangle;
        if (element.Properties.ProcessId.ValueOrDefault != target.ProcessId
            || element.Properties.AutomationId.ValueOrDefault != expectedId
            || new Bounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height) != before.Bounds
            || ObservationVisibility(element, before.Bounds, out _) != true)
            throw new AutomationException("STALE_DATE_TARGET", "Date editor identity, visibility or bounds changed before input.");
        try
        {
            pattern.SetValue(value);
            Changes.Pulse();
            return "UIA3.Value.Date";
        }
        catch (Exception error)
        {
            throw new AutomationException("INPUT_UNCERTAIN", "Date input may have reached MISA; do not replay it before readback.", true, error);
        }
    }

    private AutomationElement Writable(string token, string action, bool allowBackground)
    {
        Root();
        var element = cache.Get(token, Window!.Handle);
        if (!allowBackground && WindowContext.GetForegroundWindow().ToInt64() != Window.Handle)
            throw new AutomationException("FOCUS_LOST", "The bound MISA window must be foreground before an action.");
        // A current offscreen DataItem may still be a valid ScrollItem target;
        // every other action remains visible-only.
        if (!element.IsEnabled || (!allowBackground && ReadOffscreen(element) == true && action != "scroll") || !root!.IsEnabled)
            throw new AutomationException("ELEMENT_UNAVAILABLE", "Control is disabled, hidden or blocked by a dialog.");
        if (element.Properties.ProcessId.Value != Window.ProcessId)
            throw new AutomationException("WRONG_PROCESS", "The control no longer belongs to the MISA process.");
        GuardProtected(element, action);
        return element;
    }

    private void GuardProtected(AutomationElement element, string action)
    {
        AutomationElement? current = element;
        for (var depth = 0; current != null && depth < 40; depth++, current = current.Parent)
        {
            if (current.Properties.ProcessId.ValueOrDefault != Window!.ProcessId) break;
            var id = current.Properties.AutomationId.ValueOrDefault ?? "";
            var name = WindowContext.Fold(current.Properties.Name.ValueOrDefault ?? "");
            if (action is "set" or "key" && (id == "txtJournalMemo" || name.Contains("lydonop") || name.Contains("lydoxuat")))
                throw new AutomationException("PROTECTED_FIELD", "MISA's generated memo must not be overwritten, including through an editor child.");
            if (action is "click" or "invoke" or "select" or "key" && Window.Kind != "new_item"
                && (name.StartsWith("cat", StringComparison.Ordinal) || name.StartsWith("luu", StringComparison.Ordinal)
                    || name.StartsWith("ghiso", StringComparison.Ordinal)
                    || id.Contains("Save", StringComparison.OrdinalIgnoreCase)))
                throw new AutomationException("PROTECTED_ACTION", "Sales voucher save/post is reserved for the user.");
            if (current.Properties.NativeWindowHandle.ValueOrDefault == (nint)Window.Handle) break;
        }
    }

    public void GuardKeyboard(int key, bool control)
    {
        Root();
        if (WindowContext.GetForegroundWindow().ToInt64() != Window!.Handle)
            throw new AutomationException("FOCUS_LOST", "MISA must be foreground before keyboard input.");
        var focused = automation.FocusedElement();
        if (focused == null || focused.Properties.ProcessId.ValueOrDefault != Window.ProcessId)
            throw new AutomationException("FOCUS_LOST", "Keyboard focus is outside MISA.");
        GuardProtected(focused, "key");
        if (Window.Kind != "new_item" && key is 0x72 or 0x78)
            throw new AutomationException("KEY_NOT_ALLOWED", "Unverified function-key shortcuts are disabled on sales vouchers.");
    }

    public string CaptureFailure(Operation operation, Exception error)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MisaInvoiceTool", "engine-logs", "errors", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "error.json"), System.Text.Json.JsonSerializer.Serialize(new
            { timestamp = DateTimeOffset.UtcNow, operation, window = Window, error = error.ToString() }, LocatorRepository.Json));
        try
        {
            var nodes = Root().FindAllDescendants().Take(512).Select(element => new
                { id = element.Properties.AutomationId.ValueOrDefault, name = element.Properties.Name.ValueOrDefault,
                  kind = element.ControlType.ToString(), className = element.Properties.ClassName.ValueOrDefault });
            File.WriteAllText(Path.Combine(folder, "uitree.json"), System.Text.Json.JsonSerializer.Serialize(nodes, LocatorRepository.Json));
        }
        catch (Exception failure) { File.WriteAllText(Path.Combine(folder, "uitree-error.txt"), failure.ToString()); }
        try { WindowEvidence.Capture(Window!, Path.Combine(folder, "screenshot.png")); }
        catch (Exception failure) { File.WriteAllText(Path.Combine(folder, "screenshot-error.txt"), failure.ToString()); }
        return folder;
    }

    public string Execute(string token, string action, string? value, bool allowBackground = false)
    {
        if (action == "set" && value != null && (value.Length > 32768 || value.Any(char.IsControl)))
            throw new AutomationException("INVALID_TEXT", "A scalar field cannot contain control characters or exceed 32768 characters.");
        var element = Writable(token, action, allowBackground);
        Action dispatch;
        string strategy;
        switch (action)
        {
            case "focus":
                dispatch = () =>
                {
                    element.Focus();
                    if (!element.Properties.HasKeyboardFocus.Value)
                        throw new AutomationException("FOCUS_FAILED", "UIA did not confirm keyboard focus.");
                };
                strategy = "UIA3.Focus"; break;
            case "set" when element.Patterns.Value.IsSupported:
                var pattern = element.Patterns.Value.Pattern;
                if (pattern.IsReadOnly.Value) throw new AutomationException("READ_ONLY", "ValuePattern is read-only.");
                dispatch = () => pattern.SetValue(value ?? ""); strategy = "UIA3.Value"; break;
            case "click" or "invoke" when element.Patterns.Invoke.IsSupported:
                dispatch = () => element.Patterns.Invoke.Pattern.Invoke(); strategy = "UIA3.Invoke"; break;
            case "click" or "select" when element.Patterns.SelectionItem.IsSupported:
                dispatch = () => element.Patterns.SelectionItem.Pattern.Select(); strategy = "UIA3.SelectionItem"; break;
            case "click" or "toggle" when element.Patterns.Toggle.IsSupported:
                dispatch = () => element.Patterns.Toggle.Pattern.Toggle(); strategy = "UIA3.Toggle"; break;
            case "expand" when element.Patterns.ExpandCollapse.IsSupported:
                dispatch = () =>
                {
                    var pattern = element.Patterns.ExpandCollapse.Pattern;
                    if (pattern.ExpandCollapseState == ExpandCollapseState.Expanded) return;
                    try { pattern.Expand(); }
                    catch (Exception)
                    {
                        // MISA may invalidate the combo provider while opening it;
                        // the workflow verifies the popup through a fresh native read.
                    }
                };
                strategy = "UIA3.ExpandCollapse"; break;
            case "collapse" when element.Patterns.ExpandCollapse.IsSupported:
                dispatch = () =>
                {
                    var pattern = element.Patterns.ExpandCollapse.Pattern;
                    if (pattern.ExpandCollapseState == ExpandCollapseState.Collapsed) return;
                    try { pattern.Collapse(); }
                    catch (Exception)
                    {
                        // The caller performs a fresh native readback before continuing.
                    }
                };
                strategy = "UIA3.ExpandCollapse"; break;
            case "scroll" when element.Patterns.Scroll.IsSupported:
                var scroll = (value ?? "down:1").Split(':', 2, StringSplitOptions.TrimEntries);
                var direction = scroll[0].Equals("up", StringComparison.OrdinalIgnoreCase)
                    ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
                var count = scroll.Length == 2 && int.TryParse(scroll[1], out var parsed)
                    ? Math.Clamp(parsed, 1, 20) : 1;
                dispatch = () => { for (var index = 0; index < count; index++)
                    element.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, direction); };
                strategy = "UIA3.Scroll"; break;
            case "scroll" when element.Patterns.ScrollItem.IsSupported:
                dispatch = () => element.Patterns.ScrollItem.Pattern.ScrollIntoView(); strategy = "UIA3.ScrollItem"; break;
            default: throw new AutomationException("PATTERN_UNSUPPORTED", "No UIA pattern for " + action);
        }
        try { dispatch(); Changes.Pulse(); return strategy; }
        catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", "UIA action may have reached MISA; read back before continuing.", true, error); }
    }

    public void Focus(string token, bool allowBackground = false)
    {
        var element = Writable(token, "focus", allowBackground);
        if (!allowBackground && WindowContext.GetForegroundWindow().ToInt64() != Window!.Handle)
            throw new AutomationException("FOCUS_LOST", "MISA must be foreground before keyboard input.");
        try
        {
            element.Focus();
        }
        catch (Exception error)
        {
            throw new AutomationException("INPUT_UNCERTAIN", "Focus may have reached MISA; resolve the target again before continuing.", true, error);
        }
    }

    public object Diagnostics() => new { window = Window, cacheHits = cache.Hits, cacheMisses = cache.Misses,
        subscriptions = subscriptions.Count, eventVersion = Changes.Version };
    public void Dispose()
    {
        try { Unsubscribe(); cache.Clear(); automation.Dispose(); }
        finally
        {
            Changes.Dispose();
            try { if (ownsLease) processLease!.ReleaseMutex(); }
            finally { ownsLease = false; processLease?.Dispose(); processLease = null; Window = null; }
        }
    }
}
