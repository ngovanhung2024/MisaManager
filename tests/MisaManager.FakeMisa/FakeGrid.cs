using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MisaManager.FakeMisa;

public enum CellKind { ReadOnly, Edit, Combo }

public sealed record GridColumn(string Key, string Caption, double Width, CellKind Kind = CellKind.ReadOnly,
    IReadOnlyList<object>? Options = null);

internal sealed record CostItemOption(string Code, string Name, string Unit = "", string CostObject = "", string Project = "", int Level = 0)
{
    public string DisplayCode => new string(' ', Level * 2) + Code;
    public override string ToString() => Code;
}

internal static class CostItemCatalog
{
    public static IReadOnlyList<object> Options { get; } =
    [
        new CostItemOption("CPSX", "Chi phí sản xuất", Level: 0),
        new CostItemOption("MTC", "Chi phí sử dụng máy thi công", Level: 1),
        new CostItemOption("MTC.DCSX", "Chi phí dụng cụ sản xuất", Level: 2),
        new CostItemOption("MTC.K", "Chi phí bằng tiền khác", Level: 2),
        new CostItemOption("MTC.KH", "Chi phí khấu hao máy thi công", Level: 2),
        new CostItemOption("MTC.MN", "Chi phí dịch vụ mua ngoài", Level: 2),
        new CostItemOption("MTC.NC", "Chi phí nhân công", Level: 2),
        new CostItemOption("MTC.VL", "Chi phí vật liệu", Level: 2),
        new CostItemOption("NCTT", "Chi phí nhân công trực tiếp", Level: 0),
        new CostItemOption("NVLTT", "Chi phí nguyên liệu, vật liệu trực tiếp", Level: 0),
        new CostItemOption("SXC", "Chi phí sản xuất chung", Level: 0),
        new CostItemOption("CPBH.VC", "Chi phí vận chuyển", Level: 2),
        new CostItemOption("CPBH.XANG", "Chi phí xăng", Level: 2),
        new CostItemOption("SXC.NUOC", "Chi phí nước", Level: 2),
        new CostItemOption("SXC.BD", "Chi phí bảo dưỡng", Level: 2),
        new CostItemOption("SXC.DCSX", "Chi phí dụng cụ sản xuất", Level: 2),
        new CostItemOption("SXC.DIEN", "Chi phí điện", Level: 2)
    ];

    public static DataTemplate Template { get; } = CreateTemplate();

    private static DataTemplate CreateTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        AddText(row, nameof(CostItemOption.DisplayCode), 145);
        AddText(row, nameof(CostItemOption.Name), 220);
        return new DataTemplate { VisualTree = row };
    }

    private static void AddText(FrameworkElementFactory row, string property, double width)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(FrameworkElement.WidthProperty, width);
        text.SetValue(TextBlock.PaddingProperty, new Thickness(3, 1, 3, 1));
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        row.AppendChild(text);
    }
}

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
    private readonly StackPanel filterPanel = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
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
        DockPanel.SetDock(filterPanel, Dock.Top);
        FooterBar = new Border { Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF5)), Child = footer };
        DockPanel.SetDock(FooterBar, Dock.Bottom);
        dock.Children.Add(headerPanel);
        dock.Children.Add(filterPanel);
        DockPanel.SetDock(shadow, Dock.Top);
        dock.Children.Add(shadow);
        DockPanel.SetDock(ghostHost, Dock.Top);
        dock.Children.Add(ghostHost);
        if (!footerOutside) dock.Children.Add(FooterBar);
        dock.Children.Add(rowPanel);
        Child = dock;
    }

    public bool ShowInsertionRow { get; set; }
    public bool ShowFilters { get; set; }
    /// <summary>Leaves visible register cells focusable but exposes them as custom providers, like the failing R20 grid.</summary>
    public bool CellsUseCustomProvider { get; set; }
    public bool RowBoundsLostAfterInvoiceRead { get; set; }
    public bool EmptyRegisterExposesRow { get; set; }
    public bool RegisterCountViaItemStatus { get; set; }
    /// <summary>The register filter restores its previous text when the editor is focused a second time.</summary>
    public bool FilterRevertsOnRefocus { get; set; }
    public string FilterInitialText { get; set; } = "";
    /// <summary>
    /// R20 unit cells (report 0.2.172, HĐ 151/164): the "ĐVT" combo exposes an invokable "[Editor] dropdown button"; while
    /// its list is closed it still exposes the list's items, with bounds over the next grid row; the first opening of a
    /// list is drawn only after <see cref="UnitListOpenDelayMilliseconds"/>.
    /// </summary>
    public bool R20UnitList { get; set; }
    public int UnitListOpenDelayMilliseconds { get; set; }
    /// <summary>Exposes cost-item rows as drawn custom text, without SelectionItem, like some R20 builds.</summary>
    public bool CostItemListDrawnAsCustomText { get; set; }
    public bool CostItemListHiddenFromUia { get; set; }
    /// <summary>"% thuế GTGT" cells answer ValuePattern with the list's data value (KKKNT → "-2"); see <see cref="KeyedTaxRateCombo"/>.</summary>
    public bool TaxRateValueIsKey { get; set; }
    public bool TaxRateListOpensOnClick { get; set; }
    public bool TaxRateListHiddenFromUia { get; set; }
    public bool TaxRateListTextAccessible { get; set; }
    public bool TaxRateRequiresKeyboardText { get; set; }
    public bool TaxRateEditorOutsideCellTree { get; set; }
    public string TaxRateListKeyFault { get; set; } = "";
    public bool TaxRateStatusStaleWhileOpen { get; set; }
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
    public event EventHandler<(string Key, string Text)>? FilterCommitted;
    /// <summary>A real mouse click landed on a cell (row index or null for the insertion row, column key).</summary>
    public event EventHandler<(int? Row, string Key)>? CellClicked;
    public event EventHandler<int>? RowDoubleClicked;
    internal void RaiseCellClicked(int? row, string key) => CellClicked?.Invoke(this, (row, key));
    internal void RaiseRowDoubleClicked(int row) => RowDoubleClicked?.Invoke(this, row);

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
        filterPanel.Children.Clear();
        filterPanel.Visibility = ShowFilters ? Visibility.Visible : Visibility.Collapsed;
        if (ShowFilters)
            foreach (var column in columns)
            {
                var committedText = column.Key == "number" ? FilterInitialText : "";
                Control filter = FilterRevertsOnRefocus
                    ? new ComboBox { IsEditable = true, Width = column.Width, Height = RowHeight, Tag = column.Key, FontSize = 11, Text = committedText }
                    : new TextBox { Width = column.Width, Height = RowHeight, Tag = column.Key, FontSize = 11, Text = committedText };
                filter.PreviewMouseLeftButtonDown += (_, _) =>
                {
                    if (!FilterRevertsOnRefocus || committedText.Length == 0) return;
                    if (filter is ComboBox combo) combo.Text = committedText;
                    else ((TextBox)filter).Text = committedText;
                };
                filter.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key is not (Key.Enter or Key.Tab)) return;
                    var text = filter is ComboBox combo ? combo.Text : ((TextBox)filter).Text;
                    committedText = text;
                    FilterCommitted?.Invoke(this, (column.Key, text));
                    if (e.Key == Key.Enter) e.Handled = true;
                };
                filterPanel.Children.Add(filter);
            }
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
        if (expected != rows.Count || EmptyRegisterExposesRow && source.RowCount == 0) { Rebuild(); return; }
        foreach (var row in rows) row.Load(source, force);
        UpdateRowCount(source.RowCount);
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
        if (EmptyRegisterExposesRow && source.RowCount == 0) AddRow(0);
        BuildShadow();
        UpdateRowCount(source.RowCount);
    }

    private void UpdateRowCount(int count)
    {
        footer.Text = $"Số dòng = {count + FooterCountOffset}";
        if (!RegisterCountViaItemStatus) return;
        shadow.MinHeight = 1;
        AutomationProperties.SetItemStatus(shadow,
            $"DataRowCount^IG_TOKEN_SEPERATOR^{count}^IG_TOKEN_SEPERATOR^2^IG_TOKEN_DELIMETER^");
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
            if (owner.ShowFilters)
                foreach (var filter in owner.filterPanel.Children.OfType<UIElement>())
                    if (UIElementAutomationPeer.CreatePeerForElement(filter) is { } filterPeer) children.Add(filterPeer);
            if ((owner.shadow.Count > 0 || owner.RegisterCountViaItemStatus)
                && UIElementAutomationPeer.CreatePeerForElement(owner.shadow) is { } shadowPeer)
                children.Add(shadowPeer);
            // Before the real rows, so a reader taking the first match would bind the copy.
            if (owner.ghost is { } ghostRow && UIElementAutomationPeer.CreatePeerForElement(ghostRow) is { } ghostPeer)
                children.Add(ghostPeer);
            if (!owner.RowsHidden)
                foreach (var row in owner.Rows)
                    if (UIElementAutomationPeer.CreatePeerForElement(row) is { } peer) children.Add(peer);
            if (!owner.FooterOutside && !owner.RegisterCountViaItemStatus
                && UIElementAutomationPeer.CreatePeerForElement(owner.Footer) is { } footerPeer) children.Add(footerPeer);
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
    private readonly bool textOnlyList;
    private readonly bool hiddenOptions;
    private bool openedOnce;

    public R20UnitCombo(FakeGrid grid, bool textOnlyList = false, bool hiddenOptions = false)
    {
        this.grid = grid;
        this.textOnlyList = textOnlyList;
        this.hiddenOptions = hiddenOptions;
        if (hiddenOptions)
        {
            DropDownOpened += (_, _) => FakeLog.Write("cost item list opened");
            DropDownClosed += (_, _) => FakeLog.Write("cost item list closed");
            PreviewKeyDown += (_, e) =>
            {
                if (IsDropDownOpen && e.Key is (Key.Up or Key.Down))
                {
                    FakeLog.Write("cost item list ignored " + e.Key);
                    e.Handled = true;
                }
            };
        }
        // R20's value list closes as soon as the voucher window loses activation (Alt key, foreground switch).
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is not { } window) return;
            window.Deactivated += (_, _) =>
            {
                if (!IsDropDownOpen) return;
                IsDropDownOpen = false;
                FakeLog.Write("unit list closed: voucher window deactivated");
            };
        };
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

    protected override DependencyObject GetContainerForItemOverride()
        => textOnlyList || hiddenOptions ? new DrawnCostItem(hiddenOptions) : base.GetContainerForItemOverride();

    private sealed class Peer(R20UnitCombo owner) : ComboBoxAutomationPeer(owner)
    {
        private DropDownButtonPeer? button;
        private List<StaleOptionPeer>? stale;

        protected override List<AutomationPeer> GetChildrenCore()
        {
            if (owner.IsDropDownOpen) stale = null;
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

    internal sealed class DropDownButtonPeer(ToggleButton toggle, ComboBox combo) : FrameworkElementAutomationPeer(toggle), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetAutomationIdCore() => "[Editor] dropdown button";
        protected override List<AutomationPeer> GetChildrenCore() => [];
        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Invoke ? this : null;
        public void Invoke() => combo.IsDropDownOpen = !combo.IsDropDownOpen;

        // WPF's toggle spans the whole editable combo under its text box; R20's arrow is the glyph at the right edge.
        protected override System.Windows.Rect GetBoundingRectangleCore()
        {
            var bounds = base.GetBoundingRectangleCore();
            if (bounds.IsEmpty) return bounds;
            var width = Math.Min(16, bounds.Width);
            return new System.Windows.Rect(bounds.Right - width, bounds.Y, width, bounds.Height);
        }
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

    private sealed class DrawnCostItem(bool hiddenOptions) : ComboBoxItem
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new DrawnCostItemPeer(this, hiddenOptions);
    }

    private sealed class DrawnCostItemPeer(DrawnCostItem owner, bool hiddenOptions) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore()
            => hiddenOptions ? AutomationControlType.Custom : AutomationControlType.DataItem;
        protected override string GetNameCore()
            => hiddenOptions ? "" : owner.Content is CostItemOption option ? option.Code : owner.Content?.ToString() ?? "";
        protected override List<AutomationPeer> GetChildrenCore() => [];
        public override object? GetPattern(PatternInterface patternInterface)
            => !hiddenOptions && patternInterface == PatternInterface.Value ? this : null;
        bool IValueProvider.IsReadOnly => true;
        string IValueProvider.Value => owner.Content is CostItemOption option ? option.Code : owner.Content?.ToString() ?? "";
        void IValueProvider.SetValue(string value) => throw new ElementNotEnabledException();
    }
}

internal sealed class GridRow : StackPanel
{
    private bool loseBoundsOnce;
    private const string NewRowPrompt = "Bấm vào đây để thêm mới";
    private readonly FakeGrid grid;
    private readonly IReadOnlyList<GridColumn> columns;
    private readonly Dictionary<string, Control> cells = new(StringComparer.Ordinal);

    public GridRow(FakeGrid grid, int? index, IReadOnlyList<GridColumn> columns, double height)
    {
        this.grid = grid;
        this.columns = columns;
        Index = index;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && Index is { } row) grid.RaiseRowDoubleClicked(row);
        };
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
                    combo.SelectedItem = combo.Items.Cast<object>().FirstOrDefault(item => item is CostItemOption option
                        ? string.Equals(option.Code, value, StringComparison.OrdinalIgnoreCase)
                        : Equals(item, value));
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
        var combo = column.Key == "cost_item" || grid.R20UnitList && column.Key == "unit" ? new R20UnitCombo(grid,
                textOnlyList: column.Key == "cost_item" && grid.CostItemListDrawnAsCustomText,
                hiddenOptions: column.Key == "cost_item" && grid.CostItemListHiddenFromUia)
            : grid.TaxRateValueIsKey && column.Key == "tax_rate" ? new KeyedTaxRateCombo(grid.TaxRateListOpensOnClick,
                grid.TaxRateListHiddenFromUia, grid.TaxRateListKeyFault, grid.TaxRateStatusStaleWhileOpen,
                grid.TaxRateListTextAccessible, grid.TaxRateEditorOutsideCellTree) : new ComboBox();
        combo.IsEditable = true;
        combo.FontSize = 11;
        combo.ItemsSource = column.Key == "cost_item" ? CostItemCatalog.Options : column.Options ?? Array.Empty<object>();
        if (column.Key == "cost_item")
        {
            combo.ItemTemplate = CostItemCatalog.Template;
            combo.MaxDropDownHeight = 190;
        }
        combo.PreviewMouseLeftButtonDown += (_, _) => grid.RaiseCellClicked(Index, column.Key);
        combo.PreviewKeyDown += (_, e) => OnKey(column.Key,
            combo is KeyedTaxRateCombo keyed && grid.TaxRateRequiresKeyboardText && !keyed.ReceivedKeyboardText
                ? "0%" : combo.Text, e);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo is KeyedTaxRateCombo && grid.TaxRateRequiresKeyboardText) return;
            if (combo is KeyedTaxRateCombo { HidesList: true, IsDropDownOpen: true }) return;
            if (!combo.IsKeyboardFocusWithin) return;
            var chosen = combo.SelectedItem is CostItemOption option ? option.Code : combo.SelectedItem as string;
            if (chosen is not null) grid.RaiseCommit(Index, column.Key, chosen, Key.None);
        };
        // R20 takes the rate highlighted in its open list when the list closes.
        if (combo is KeyedTaxRateCombo { HidesList: true })
            combo.DropDownClosed += (_, _) =>
            {
                if (grid.TaxRateRequiresKeyboardText) return;
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

        protected override AutomationControlType GetAutomationControlTypeCore()
            => grid.CellsUseCustomProvider && row.Index is not null
                ? AutomationControlType.Custom
                : base.GetAutomationControlTypeCore();

        protected override string GetNameCore()
        {
            if (!grid.CellsUseCustomProvider || row.Index is null) return base.GetNameCore();
            if (grid.RowBoundsLostAfterInvoiceRead && Equals(Cell.Tag, "number"))
            {
                row.loseBoundsOnce = true;
                FakeLog.Write("register invoice cell read");
            }
            return Cell.Text;
        }
    }

    private sealed class RowPeer(GridRow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;
        protected override string GetClassNameCore() => "UltraGridRow";
        protected override string GetNameCore() => owner.Index is null ? "Template Add Row" : "Row " + (owner.Index + 1);
        protected override System.Windows.Rect GetBoundingRectangleCore()
        {
            if (owner.Stale) throw new ElementNotAvailableException();
            if (!owner.loseBoundsOnce) return base.GetBoundingRectangleCore();
            owner.loseBoundsOnce = false;
            return System.Windows.Rect.Empty;
        }
        protected override List<AutomationPeer> GetChildrenCore()
            => owner.Stale ? throw new ElementNotAvailableException()
                : [.. owner.Cells.Select(UIElementAutomationPeer.CreatePeerForElement).Where(peer => peer is not null).Cast<AutomationPeer>()];
    }
}
