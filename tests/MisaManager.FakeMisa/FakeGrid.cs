using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MisaManager.FakeMisa;

public enum CellKind { ReadOnly, Edit, Combo }

public sealed record GridColumn(string Key, string Caption, double Width, CellKind Kind = CellKind.ReadOnly,
    IReadOnlyList<string>? Options = null);

/// <summary>A row value source: <c>null</c> row index means the "Bấm vào đây để thêm mới" insertion row.</summary>
public interface IGridSource
{
    int RowCount { get; }
    string Value(int row, string key);
}

public sealed class CellCommitEventArgs(int? row, string key, string text, Key key2) : EventArgs
{
    public int? Row { get; } = row;
    public string Key { get; } = key;
    public string Text { get; } = text;
    public Key Trigger { get; } = key2;
}

/// <summary>
/// Mimics the UIA tree of R20's Infragistics UltraGrid, which the automation reads through its custom path:
/// the grid exposes no Grid/Table/Scroll pattern; headers are HeaderItem; rows are DataItem with
/// AutomationId "0".."N-1"; the insertion row is "-1"; cells are Edit/ComboBox aligned under their
/// header; "Số dòng = N" is a Text inside the grid.
/// </summary>
public sealed class FakeGrid : Border
{
    private const double RowHeight = 21;
    private readonly StackPanel headerPanel = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel rowPanel = new() { Orientation = Orientation.Vertical };
    private readonly ShadowRegion shadow = new();
    private readonly StackPanel ghostHost = new() { Orientation = Orientation.Vertical };
    private GhostRow? ghost;
    private readonly TextBlock footer = new() { Margin = new Thickness(4, 2, 0, 2), FontSize = 11 };
    private readonly List<GridRow> rows = [];
    private IReadOnlyList<GridColumn> columns = [];
    private IGridSource? source;
    private double horizontalShift;

    /// <param name="footerOutside">R20 (VPS): "Số dòng = N" is drawn over the grid's bottom edge but is not a UIA descendant
    /// of the grid; the host places <see cref="FooterBar"/> itself.</param>
    public FakeGrid(string automationId, double height, bool footerOutside = false)
    {
        FooterOutside = footerOutside;
        AutomationProperties.SetAutomationId(this, automationId);
        Height = height;
        BorderBrush = Brushes.SteelBlue;
        BorderThickness = new Thickness(1);
        Background = Brushes.White;
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerPanel, Dock.Top);
        FooterBar = new Border { Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF5)), Child = footer };
        DockPanel.SetDock(FooterBar, Dock.Bottom);
        dock.Children.Add(headerPanel);
        DockPanel.SetDock(shadow, Dock.Top);
        dock.Children.Add(shadow);
        DockPanel.SetDock(ghostHost, Dock.Top);
        dock.Children.Add(ghostHost);
        if (!footerOutside) dock.Children.Add(FooterBar);
        dock.Children.Add(rowPanel);
        Child = dock;
    }

    public bool ShowInsertionRow { get; set; }
    /// <summary>
    /// R20 unit cells (report 0.2.172, HĐ 151/164): the "ĐVT" combo exposes an invokable "[Editor] dropdown button"; while
    /// its list is closed it still exposes the list's items, with bounds over the next grid row; the first opening of a
    /// list is drawn only after <see cref="UnitListOpenDelayMilliseconds"/>.
    /// </summary>
    public bool R20UnitList { get; set; }
    public int UnitListOpenDelayMilliseconds { get; set; }
    /// <summary>
    /// R20 (VPS 0.2.163): a "DataRowCount" Tree region holding disabled copies of the data rows whose cells have no
    /// area, so nothing lies under the headers. Rebuilt with the rows while set.
    /// </summary>
    public bool ShadowBand { get; set; }
    /// <summary>
    /// R20 on the VPS (0.2.164): the DataRowCount region's rows lie exactly over the visible rows but expose no cells,
    /// and the visible rows carry ids that are not the ordinal ("r0", "r1", …).
    /// </summary>
    public bool ShadowOverlay { get; set; }
    /// <summary>
    /// R20 on the VPS (0.2.166): the visible rows leave the UIA tree and only enabled, cell-less ordinal rows remain over
    /// them (<see cref="HideRowsFromUia"/>).
    /// </summary>
    public bool RowsHidden { get; private set; }
    public bool FooterOutside { get; }
    public Border FooterBar { get; }
    /// <summary>Until then cell readback fails with UIA_E_ELEMENTNOTAVAILABLE, as R20's grid did while redrawing.</summary>
    public DateTime CellsUnavailableUntil { get; set; }
    internal bool CellsUnavailable => DateTime.UtcNow < CellsUnavailableUntil;
    /// <summary>Reading a "Tiền thuế GTGT" cell recreates every row and cell, as R20's grid did on the VPS (0.2.160/0.2.161).</summary>
    public bool RecreateCellsOnRead { get; set; }

    // Old rows and cells stay stale for UIA clients still holding them; the grid's children are the new rows.
    internal void RecreateRows()
    {
        foreach (var row in rows) row.MarkStale();
        Rebuild();
        UpdateLayout();
        UIElementAutomationPeer.FromElement(this)?.ResetChildrenCache();
    }

    public void HideRowsFromUia(bool hidden)
    {
        if (RowsHidden == hidden) return;
        RowsHidden = hidden;
        BuildShadow();
        UpdateLayout();
        UIElementAutomationPeer.FromElement(this)?.ResetChildrenCache();
    }

    public event EventHandler? TaxAmountRead;
    internal void RaiseTaxAmountRead() => TaxAmountRead?.Invoke(this, EventArgs.Empty);

    public int FooterCountOffset { get; set; }
    public event EventHandler<CellCommitEventArgs>? CellCommitted;
    /// <summary>A real mouse click landed on a cell (row index or null for the insertion row, column key).</summary>
    public event EventHandler<(int? Row, string Key)>? CellClicked;
    internal void RaiseCellClicked(int? row, string key) => CellClicked?.Invoke(this, (row, key));

    /// <summary>
    /// A second enabled provider ("-1") of a row lying exactly over the visible row with a copy of its values, until the
    /// row is committed again.
    /// </summary>
    public void ShowGhostRow(int index)
    {
        if (rows.FirstOrDefault(row => row.Index == index) is not { } visible) return;
        UpdateLayout();
        ghost = new GhostRow(visible);
        ghostHost.Children.Clear();
        ghostHost.Children.Add(ghost);
        UpdateLayout();
        UIElementAutomationPeer.FromElement(this)?.ResetChildrenCache();
    }

    public bool GhostShown => ghost is not null;

    public void ClearGhostRow()
    {
        if (ghost is null) return;
        ghost = null;
        ghostHost.Children.Clear();
        UIElementAutomationPeer.FromElement(this)?.ResetChildrenCache();
    }
    /// <summary>F9 on the Mã hàng cell ("Thêm nhanh").</summary>
    public event EventHandler<int?>? QuickAddRequested;
    public event EventHandler<int?>? QuickSearchRequested;

    internal IEnumerable<UIElement> HeaderCells => headerPanel.Children.Cast<UIElement>();
    internal IEnumerable<GridRow> Rows => rows;
    internal TextBlock Footer => footer;

    public void Bind(IReadOnlyList<GridColumn> layout, IGridSource data)
    {
        columns = layout;
        source = data;
        headerPanel.Children.Clear();
        foreach (var column in columns)
            headerPanel.Children.Add(new HeaderCell(column.Caption, column.Width));
        Rebuild();
    }

    /// <summary>
    /// Infragistics-like sideways scroll: the first column stays frozen on top while the second column slides under it.
    /// As in R20 the frozen cell is inset 2 px from its header, so the wide cell beneath covers the header more.
    /// </summary>
    public void ScrollHorizontally(double shift)
    {
        horizontalShift = shift;
        ApplyHorizontalScroll(headerPanel.Children.Cast<FrameworkElement>().ToArray(), header: true);
        foreach (var row in rows) ApplyHorizontalScroll([.. row.Cells], header: false);
    }

    private void ApplyHorizontalScroll(IReadOnlyList<FrameworkElement> line, bool header)
    {
        if (horizontalShift <= 0 || line.Count < 2) return;
        Panel.SetZIndex(line[0], 1);
        if (!header)
        {
            line[0].Width = columns[0].Width - 2;
            line[0].Margin = new Thickness(0, 0, 2, 0);
        }
        line[1].Margin = new Thickness(-horizontalShift, 0, 0, 0);
    }

    /// <summary>Re-renders values; recreates rows when the count changed (as Infragistics recreates its providers).</summary>
    public void Refresh(bool force = false)
    {
        if (source is null) return;
        var expected = source.RowCount + (ShowInsertionRow ? 1 : 0);
        if (expected != rows.Count) { Rebuild(); return; }
        foreach (var row in rows) row.Load(source, force);
        footer.Text = $"Số dòng = {source.RowCount + FooterCountOffset}";
    }

    private void Rebuild()
    {
        ClearGhostRow();
        rowPanel.Children.Clear();
        rows.Clear();
        if (source is null) return;
        for (var index = 0; index < source.RowCount; index++) AddRow(index);
        if (ShowInsertionRow) AddRow(null);
        foreach (var row in rows) row.Load(source, force: true);
        footer.Text = $"Số dòng = {source.RowCount + FooterCountOffset}";
        BuildShadow();
    }

    private void BuildShadow()
    {
        var overlay = ShadowOverlay || RowsHidden;
        shadow.Build(ShadowBand || overlay ? source?.RowCount ?? 0 : 0, columns,
            overlay ? index => rows.FirstOrDefault(row => row.Index == index) : null, enabled: RowsHidden);
    }

    private void AddRow(int? index)
    {
        var row = new GridRow(this, index, columns, RowHeight);
        if (ShadowOverlay && index is { } ordinal)
            AutomationProperties.SetAutomationId(row, "r" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        rows.Add(row);
        rowPanel.Children.Add(row);
        ApplyHorizontalScroll([.. row.Cells], header: false);
    }

    /// <summary>Moves keyboard focus to a cell after MISA-like recalculation (next cell, or the new insertion row).</summary>
    public void FocusCell(int? row, string key)
        => Dispatcher.BeginInvoke(() =>
        {
            var target = rows.FirstOrDefault(item => item.Index == row)?.Cell(key);
            if (target is not null) Keyboard.Focus(target);
        }, System.Windows.Threading.DispatcherPriority.Input);

    public string? NextEditableKey(string key)
    {
        var start = columns.ToList().FindIndex(column => column.Key == key);
        return columns.Skip(start + 1).FirstOrDefault(column => column.Kind != CellKind.ReadOnly)?.Key;
    }

    internal void RaiseCommit(int? row, string key, string text, Key trigger) => CellCommitted?.Invoke(this, new(row, key, text, trigger));
    internal void RaiseQuickAdd(int? row) => QuickAddRequested?.Invoke(this, row);
    internal void RaiseQuickSearch(int? row) => QuickSearchRequested?.Invoke(this, row);

    protected override AutomationPeer OnCreateAutomationPeer() => new GridPeer(this);

    private sealed class GridPeer(FakeGrid owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => "UltraGrid";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override List<AutomationPeer> GetChildrenCore()
        {
            var children = new List<AutomationPeer>();
            foreach (var header in owner.HeaderCells)
                if (UIElementAutomationPeer.CreatePeerForElement(header) is { } peer) children.Add(peer);
            if (owner.shadow.Count > 0 && UIElementAutomationPeer.CreatePeerForElement(owner.shadow) is { } shadowPeer)
                children.Add(shadowPeer);
            // Before the real rows, so a reader taking the first match would bind the copy.
            if (owner.ghost is { } ghostRow && UIElementAutomationPeer.CreatePeerForElement(ghostRow) is { } ghostPeer)
                children.Add(ghostPeer);
            if (!owner.RowsHidden)
                foreach (var row in owner.Rows)
                    if (UIElementAutomationPeer.CreatePeerForElement(row) is { } peer) children.Add(peer);
            if (!owner.FooterOutside && UIElementAutomationPeer.CreatePeerForElement(owner.Footer) is { } footerPeer) children.Add(footerPeer);
            return children;
        }
        public override object? GetPattern(PatternInterface patternInterface) => null;
    }
}

internal sealed class ShadowRegion : StackPanel
{
    public int Count { get; private set; }

    /// <param name="overlay">When set, row i reports the bounds of that visible row and has no cells.</param>
    public void Build(int count, IReadOnlyList<GridColumn> columns, Func<int, UIElement?>? overlay = null, bool enabled = false)
    {
        Children.Clear();
        Count = count;
        AutomationProperties.SetItemStatus(this, $"DataRowCount^IG_TOKEN_SEPERATOR^{count}^IG_TOKEN_DELIMETER^");
        for (var index = 0; index < count; index++)
        {
            var ordinal = index;
            var row = new ShadowRow { Orientation = Orientation.Horizontal, Height = 2, Width = 300, IsEnabled = enabled,
                HorizontalAlignment = HorizontalAlignment.Left, Over = overlay is null ? null : () => overlay(ordinal) };
            AutomationProperties.SetAutomationId(row, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (overlay is null)
                foreach (var column in columns) row.Children.Add(new TextBox { Width = 0, Height = 2, Text = "" });
            Children.Add(row);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RegionPeer(this);

    private sealed class RegionPeer(ShadowRegion owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;
        protected override List<AutomationPeer> GetChildrenCore()
            => [.. owner.Children.Cast<UIElement>().Select(UIElementAutomationPeer.CreatePeerForElement).OfType<AutomationPeer>()];
    }

    private sealed class ShadowRow : StackPanel
    {
        public Func<UIElement?>? Over { get; init; }
        protected override AutomationPeer OnCreateAutomationPeer() => new ShadowRowPeer(this);
    }

    private sealed class ShadowRowPeer(ShadowRow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;
        protected override System.Windows.Rect GetBoundingRectangleCore()
            => owner.Over?.Invoke() is { } visible && UIElementAutomationPeer.CreatePeerForElement(visible) is { } peer
                ? peer.GetBoundingRectangle() : base.GetBoundingRectangleCore();
        protected override List<AutomationPeer> GetChildrenCore()
            => [.. owner.Children.Cast<UIElement>().Select(UIElementAutomationPeer.CreatePeerForElement).OfType<AutomationPeer>()];
    }
}

internal sealed class HeaderCell : Border
{
    public HeaderCell(string caption, double width)
    {
        Caption = caption;
        Width = width;
        Height = 22;
        BorderBrush = Brushes.LightSteelBlue;
        BorderThickness = new Thickness(0, 0, 1, 1);
        Background = new SolidColorBrush(Color.FromRgb(0xDD, 0xE6, 0xF5));
        Child = new TextBlock { Text = caption, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    }

    public string Caption { get; }

    protected override AutomationPeer OnCreateAutomationPeer() => new HeaderPeer(this);

    private sealed class HeaderPeer(HeaderCell owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.HeaderItem;
        protected override string GetNameCore() => owner.Caption;
        protected override string GetClassNameCore() => "ColumnHeader";
        protected override List<AutomationPeer> GetChildrenCore() => [];
    }
}

/// <summary>Enabled "-1" copy of a visible row: same bounds, read-only cells with the same text.</summary>
internal sealed class GhostRow : StackPanel
{
    private readonly GridRow visible;

    public GhostRow(GridRow visible)
    {
        this.visible = visible;
        Orientation = Orientation.Horizontal;
        Height = 2;
        Width = 300;
        HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(this, "-1");
        foreach (var cell in visible.Cells)
            Children.Add(new GhostCell(cell)
            {
                Width = 0, Height = 2, IsReadOnly = true,
                Text = cell switch { TextBox text => text.Text, ComboBox combo => combo.Text, _ => "" }
            });
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GhostRowPeer(this);

    private static System.Windows.Rect BoundsOf(UIElement element)
        => UIElementAutomationPeer.CreatePeerForElement(element)?.GetBoundingRectangle() ?? System.Windows.Rect.Empty;

    private sealed class GhostRowPeer(GhostRow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;
        protected override string GetClassNameCore() => "UltraGridRow";
        protected override System.Windows.Rect GetBoundingRectangleCore() => BoundsOf(owner.visible);
        protected override bool IsOffscreenCore() => false;
        protected override List<AutomationPeer> GetChildrenCore()
            => [.. owner.Children.Cast<UIElement>().Select(UIElementAutomationPeer.CreatePeerForElement).OfType<AutomationPeer>()];
    }

    private sealed class GhostCell(Control visible) : TextBox
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new GhostCellPeer(this, visible);
    }

    private sealed class GhostCellPeer(TextBox owner, Control visible) : TextBoxAutomationPeer(owner)
    {
        protected override System.Windows.Rect GetBoundingRectangleCore() => BoundsOf(visible);
        protected override bool IsOffscreenCore() => false;
    }
}

internal sealed class R20UnitCombo : ComboBox
{
    private readonly FakeGrid grid;
    private bool openedOnce;

    public R20UnitCombo(FakeGrid grid)
    {
        this.grid = grid;
        DropDownOpened += (_, _) =>
        {
            if (openedOnce || grid.UnitListOpenDelayMilliseconds <= 0) return;
            openedOnce = true;
            IsDropDownOpen = false;
            FakeLog.Write("unit list opens late");
            var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(grid.UnitListOpenDelayMilliseconds) };
            timer.Tick += (_, _) => { timer.Stop(); IsDropDownOpen = true; };
            timer.Start();
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(R20UnitCombo owner) : ComboBoxAutomationPeer(owner)
    {
        private DropDownButtonPeer? button;
        private List<StaleOptionPeer>? stale;

        protected override List<AutomationPeer> GetChildrenCore()
        {
            var children = base.GetChildrenCore() ?? [];
            if (!owner.IsDropDownOpen)
            {
                stale ??= [.. owner.Items.Cast<object>().Select((item, index) => new StaleOptionPeer(owner, item.ToString() ?? "", index))];
                children.InsertRange(0, stale);
            }
            if (button is null && FindToggle(owner) is { } toggle) button = new DropDownButtonPeer(toggle, owner);
            if (button is not null) children.Add(button);
            return children;
        }

        private static ToggleButton? FindToggle(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is ToggleButton toggle) return toggle;
                if (FindToggle(child) is { } nested) return nested;
            }
            return null;
        }
    }

    private sealed class DropDownButtonPeer(ToggleButton toggle, ComboBox combo) : FrameworkElementAutomationPeer(toggle), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetAutomationIdCore() => "[Editor] dropdown button";
        protected override List<AutomationPeer> GetChildrenCore() => [];
        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Invoke ? this : null;
        public void Invoke() => combo.IsDropDownOpen = !combo.IsDropDownOpen;
    }

    // A closed list's item, laid over the next grid row in a 2-pixel band per item.
    private sealed class StaleOptionPeer(R20UnitCombo combo, string name, int index)
        : FrameworkElementAutomationPeer(combo), ISelectionItemProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetNameCore() => name;
        protected override string GetAutomationIdCore() => "";
        protected override string GetClassNameCore() => "ValueListItem";
        protected override bool IsOffscreenCore() => false;
        protected override bool IsKeyboardFocusableCore() => false;
        protected override List<AutomationPeer> GetChildrenCore() => [];
        protected override System.Windows.Rect GetBoundingRectangleCore()
        {
            var cell = base.GetBoundingRectangleCore();
            return cell.IsEmpty ? cell : new System.Windows.Rect(cell.X, cell.Bottom + 2 + index * 2, cell.Width, 2);
        }
        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.SelectionItem ? this : null;
        bool ISelectionItemProvider.IsSelected => false;
        IRawElementProviderSimple? ISelectionItemProvider.SelectionContainer => null;
        void ISelectionItemProvider.AddToSelection() { }
        void ISelectionItemProvider.RemoveFromSelection() { }
        void ISelectionItemProvider.Select() { }
    }
}

internal sealed class GridRow : StackPanel
{
    private const string NewRowPrompt = "Bấm vào đây để thêm mới";
    private readonly FakeGrid grid;
    private readonly IReadOnlyList<GridColumn> columns;
    private readonly Dictionary<string, Control> cells = new(StringComparer.Ordinal);

    public GridRow(FakeGrid grid, int? index, IReadOnlyList<GridColumn> columns, double height)
    {
        this.grid = grid;
        this.columns = columns;
        Index = index;
        Orientation = Orientation.Horizontal;
        Height = height;
        AutomationProperties.SetAutomationId(this, index?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-1");
        foreach (var column in columns)
        {
            Control cell = column.Kind == CellKind.Combo ? CreateCombo(column) : CreateText(column);
            cell.Width = column.Width;
            cell.Height = height;
            cell.Tag = column.Key;
            cells[column.Key] = cell;
            Children.Add(cell);
        }
        if (index is null)
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFD, 0xE7));
            // Visual hint only: the insertion row's cells stay empty for UIA, like R20.
            ((TextBox)cells[columns[0].Key]).Background = new VisualBrush(new TextBlock { Text = NewRowPrompt, Foreground = Brushes.DimGray, FontSize = 11 })
            { Stretch = Stretch.None, AlignmentX = AlignmentX.Left };
        }
    }

    public int? Index { get; }
    internal bool Stale { get; private set; }

    internal void MarkStale()
    {
        Stale = true;
        foreach (var cell in cells.Values.OfType<GridTextBox>()) cell.Stale = true;
    }
    public Control? Cell(string key) => cells.GetValueOrDefault(key);
    internal IEnumerable<Control> Cells => columns.Select(column => cells[column.Key]);

    public void Load(IGridSource source, bool force)
    {
        foreach (var column in columns)
        {
            var value = Index is { } row ? source.Value(row, column.Key) : "";
            switch (cells[column.Key])
            {
                case TextBox text when force || !text.IsKeyboardFocusWithin || text.IsReadOnly: text.Text = value; break;
                case ComboBox combo when force || !combo.IsKeyboardFocusWithin:
                    combo.Text = value;
                    combo.SelectedItem = combo.Items.Cast<object>().FirstOrDefault(item => Equals(item, value));
                    break;
            }
        }
    }

    private TextBox CreateText(GridColumn column)
    {
        var text = new GridTextBox(grid, this)
        {
            IsReadOnly = column.Kind == CellKind.ReadOnly,
            BorderThickness = new Thickness(0, 0, 1, 1),
            BorderBrush = Brushes.Gainsboro,
            Padding = new Thickness(2, 1, 2, 0),
            FontSize = 11,
            TextAlignment = column.Key is "quantity" or "unit_price" or "amount" or "tax_amount" or "subtotal" or "tax_total" or "grand_total"
                ? TextAlignment.Right : TextAlignment.Left
        };
        text.PreviewKeyDown += (_, e) => OnKey(column.Key, text.Text, e);
        text.PreviewMouseLeftButtonDown += (_, _) => grid.RaiseCellClicked(Index, column.Key);
        return text;
    }

    private ComboBox CreateCombo(GridColumn column)
    {
        var combo = grid.R20UnitList && column.Key == "unit" ? new R20UnitCombo(grid) : new ComboBox();
        combo.IsEditable = true;
        combo.FontSize = 11;
        combo.ItemsSource = column.Options ?? [];
        combo.PreviewMouseLeftButtonDown += (_, _) => grid.RaiseCellClicked(Index, column.Key);
        combo.PreviewKeyDown += (_, e) => OnKey(column.Key, combo.Text, e);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.IsKeyboardFocusWithin && combo.SelectedItem is string chosen)
                grid.RaiseCommit(Index, column.Key, chosen, Key.None);
        };
        return combo;
    }

    private void OnKey(string key, string text, KeyEventArgs e)
    {
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressed == Key.F3 && key == columns[0].Key)
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(() => grid.RaiseQuickSearch(Index));
            return;
        }
        if (pressed == Key.F9 && key == columns[0].Key)
        {
            e.Handled = true;
            var row = Index;
            Dispatcher.BeginInvoke(() => grid.RaiseQuickAdd(row));
            return;
        }
        if (pressed is not (Key.Enter or Key.Tab)) return;
        e.Handled = true;
        grid.RaiseCommit(Index, key, text, pressed);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RowPeer(this);

    private sealed class GridTextBox(FakeGrid grid, GridRow row) : TextBox
    {
        public bool Stale { get; set; }
        protected override AutomationPeer OnCreateAutomationPeer() => new CellPeer(this, grid, row);
    }

    // Re-implements IValueProvider so a read can fail the way a vanished Infragistics cell does.
    private sealed class CellPeer(GridTextBox cell, FakeGrid grid, GridRow row) : TextBoxAutomationPeer(cell), System.Windows.Automation.Provider.IValueProvider
    {
        private GridTextBox Cell => (GridTextBox)Owner;
        private void ThrowIfStale() { if (Cell.Stale) throw new ElementNotAvailableException(); }
        protected override System.Windows.Rect GetBoundingRectangleCore() { ThrowIfStale(); return base.GetBoundingRectangleCore(); }
        bool System.Windows.Automation.Provider.IValueProvider.IsReadOnly { get { ThrowIfStale(); return Cell.IsReadOnly; } }
        string System.Windows.Automation.Provider.IValueProvider.Value
        {
            get
            {
                ThrowIfStale();
                if (grid.CellsUnavailable) throw new ElementNotAvailableException();
                var value = Cell.Text;
                if (row.Index is not null && Equals(Cell.Tag, "tax_amount")) grid.RaiseTaxAmountRead();
                if (grid.RecreateCellsOnRead && row.Index is not null && Equals(Cell.Tag, "tax_amount")) grid.RecreateRows();
                return value;
            }
        }
        void System.Windows.Automation.Provider.IValueProvider.SetValue(string value)
        {
            ThrowIfStale();
            if (!IsEnabled() || Cell.IsReadOnly) throw new ElementNotEnabledException();
            Cell.Text = value;
        }
    }

    private sealed class RowPeer(GridRow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;
        protected override string GetClassNameCore() => "UltraGridRow";
        protected override string GetNameCore() => owner.Index is null ? "Template Add Row" : "Row " + (owner.Index + 1);
        protected override System.Windows.Rect GetBoundingRectangleCore()
            => owner.Stale ? throw new ElementNotAvailableException() : base.GetBoundingRectangleCore();
        protected override List<AutomationPeer> GetChildrenCore()
            => owner.Stale ? throw new ElementNotAvailableException()
                : [.. owner.Cells.Select(UIElementAutomationPeer.CreatePeerForElement).Where(peer => peer is not null).Cast<AutomationPeer>()];
    }
}
