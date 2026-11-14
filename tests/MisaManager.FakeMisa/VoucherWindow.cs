using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MisaManager.FakeMisa;

public sealed class LineModel
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Stock { get; set; } = "";
    public string DebitAccount { get; set; } = "1111";
    public string RevenueAccount { get; set; } = "5111";
    public string CreditAccount { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string TaxRate { get; set; } = "";
    public decimal TaxAmount { get; set; }
}

/// <summary>
/// "Chứng từ bán hàng – Bán hàng hóa, dịch vụ trong nước – Tiền mặt" editor; with <see cref="Scenario.Purchase"/> the
/// "Mua hàng hóa, dịch vụ trong nước nhập kho chưa thanh toán" editor (purchase_voucher screen) built from the same parts.
/// </summary>
public sealed class VoucherWindow : Window, IGridSource
{
    private const string Prompt = "MISA SME 2023";
    private readonly FakeProfile profile;
    private readonly string screen;
    private readonly bool purchase;
    private readonly string company;
    private bool expense;
    private readonly CheckBox stockVoucher;
    private readonly HashSet<TextBox> receiptBoxes = [];
    private readonly Scenario scenario;
    private bool gridWentUnavailable;
    private bool mainActivatedOnce;
    private readonly FakeStore store;
    private readonly MainWindow main;
    private readonly List<LineModel> lines = [];
    private readonly Dictionary<string, string> header = new(StringComparer.Ordinal);
    private readonly List<(TextBox Box, string Key)> bound = [];
    private readonly FakeGrid grid;
    private readonly TabControl detailTabs = new();
    private readonly TabControl headerTabs;
    private bool userSwitchedTabs;
    private bool closeSwallowed;
    private bool promptDelayed;
    private readonly TextBox subtotal, discount, taxTotal, grandTotal;
    private readonly Button save;
    private readonly Button post;
    private readonly List<Button> readOnlyCommands = [];
    private bool saveProofDelayed;
    private IReadOnlyList<GridColumn> itemLayout = [];
    private IReadOnlyList<GridColumn> taxLayout = [];
    private bool dirty;
    private bool saved;
    private bool busyAfterQuantityClickShown;

    private static int vouchersOpened;
    private readonly bool firstVoucher = Interlocked.Increment(ref vouchersOpened) == 1;

    public VoucherWindow(FakeProfile profile, Scenario scenario, FakeStore store, MainWindow main, string company)
    {
        this.profile = profile;
        this.scenario = scenario;
        purchase = scenario.Purchase;
        screen = scenario.VoucherScreen;
        this.store = store;
        this.main = main;
        this.company = company;
        if (!scenario.EditorUnowned) Owner = main;
        Loaded += (_, _) => RememberVoucherType();
        Ui.Id(this, profile.ScopeId(screen));
        Title = VoucherTitle();
        var area = SystemParameters.WorkArea;
        Left = area.Left;
        Top = area.Top;
        Width = Math.Min(1000, area.Width);
        Height = Math.Min(700, area.Height);
        ResizeMode = ResizeMode.CanMinimize;
        ShowInTaskbar = false;
        Background = Ui.Chrome;
        this.IgnoreBareAlt();
        var today = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        header["posted_date"] = header["voucher_date"] = header["issued_date"] = today;
        header["voucher_number"] = store.NextVoucherNumber(purchase ? "NK0" : "PT0");
        header["issue_number"] = "XK0" + header["voucher_number"][3..];
        header["number"] = store.NextInvoiceNumber();
        // Purchases show no proposed series: the tool types the source "Ký hiệu HĐ" and "Mẫu số HĐ".
        header["series"] = purchase ? "" : "1C26MLV";
        header["payment"] = purchase ? "Chưa thanh toán" : "Tiền mặt";
        foreach (var key in new[] { "customer", "customer_name", "address", "customer_tax", "payer", "buyer", "template" }) header[key] = "";
        header["reason"] = (purchase ? "Mua hàng theo hóa đơn " : "Thu tiền bán hàng theo hóa đơn ") + header["number"];

        var root = new DockPanel();
        var toolbar = new WrapPanel { Background = Brushes.White };
        Button Command(string caption, bool enabled, Action? action = null, bool editOnly = false)
        {
            var element = profile.Caption(screen, "save") == caption ? "save"
                : profile.Caption(screen, "close") == caption ? "close" : null;
            var button = Ui.Button(caption, enabled, element is null ? "" : profile.Id(screen, element));
            if (action is not null) button.Click += (_, _) => action();
            if (editOnly) readOnlyCommands.Add(button);
            toolbar.Children.Add(button);
            return button;
        }
        Command("Trước", false); Command("Sau", false);
        Command("Thêm", false, editOnly: true); Command("Sửa", false, editOnly: true); Command("Sửa nhanh", false);
        save = Command(profile.Caption(screen, "save"), true, Save);
        Command("Xóa", false); Command("Hoãn", true); post = Command("Ghi sổ", false); Command("Lập phiếu xuất", false);
        Command("Nạp", false); Command("Tiện ích", true); Command("Mẫu", true); Command("In", false);
        Command("Gửi email, Zalo", false); Command("Giúp", true);
        Command(profile.Caption(screen, "close"), true, CloseVoucher);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 0) };
        titleRow.Children.Add(new TextBlock { Text = purchase ? "Chứng từ mua hàng" : "Chứng từ bán hàng", FontSize = 16, FontWeight = FontWeights.Bold, Width = 210 });
        var voucherType = purchase ? VoucherTypeCombo() : new ComboBox
        {
            Width = 210, SelectedIndex = 0, ItemsSource = new[] { "1. Bán hàng hóa, dịch vụ trong nước" }
        };
        titleRow.Children.Add(voucherType);
        stockVoucher = new CheckBox { Content = purchase ? "Kiêm phiếu nhập kho" : "Kiêm phiếu xuất kho", IsChecked = true, Margin = new Thickness(10, 3, 0, 0) };
        titleRow.Children.Add(stockVoucher);
        titleRow.Children.Add(new CheckBox { Content = "Lập kèm hóa đơn", IsChecked = true, Margin = new Thickness(10, 3, 0, 0) });
        titleRow.Children.Add(new RadioButton { Content = purchase ? "Chưa thanh toán" : "Chưa thu tiền", IsChecked = purchase, Margin = new Thickness(20, 3, 0, 0) });
        titleRow.Children.Add(new RadioButton { Content = purchase ? "Thanh toán ngay" : "Thu tiền ngay", IsChecked = !purchase, Margin = new Thickness(10, 3, 0, 0) });
        DockPanel.SetDock(titleRow, Dock.Top);
        root.Children.Add(titleRow);

        headerTabs = new TabControl { Height = 205, Margin = new Thickness(4, 4, 4, 0) };
        headerTabs.Items.Add(new TabItem { Header = profile.Tab(screen, "receipt"), Content = BuildReceiptTab() });
        if (!purchase) headerTabs.Items.Add(new TabItem { Header = "Phiếu xuất", Content = IssueTab() });
        headerTabs.Items.Add(new TabItem { Header = profile.Tab(screen, "invoice"), Content = purchase ? PurchaseInvoiceTab() : InvoiceTab() });
        headerTabs.SelectedIndex = 0;
        DockPanel.SetDock(headerTabs, Dock.Top);
        root.Children.Add(headerTabs);

        // R20 phiếu nhập exposes its totals under IDs of its own (not the sales form's creTotal…); the tool finds them by
        // their captions, so the fake gives purchase totals IDs the profile never names.
        string TotalId(string key, string purchaseId) => profile.Id(screen, key) is { Length: > 0 } id ? id : purchase ? purchaseId : "";
        subtotal = Ui.Text(TotalId("subtotal", "crePUTotalAmount"), 140, "0", readOnly: true);
        discount = Ui.Text("", 140, "0", readOnly: true);
        taxTotal = Ui.Text(TotalId("tax_total", "crePUTotalVATAmount"), 140, "0", readOnly: true);
        grandTotal = Ui.Text(TotalId("grand_total", "crePUTotalPaymentAmount"), 140, "0", readOnly: true);
        var totals = new WrapPanel { Margin = new Thickness(6, 2, 6, 2) };
        totals.Children.Add(Ui.Button("Phân bổ chiết khấu...", true));
        totals.Children.Add(Ui.Button("Xem công nợ", true));
        totals.Children.Add(Ui.Form(110, ("Tổng tiền hàng", subtotal), ("Tiền chiết khấu", discount)));
        totals.Children.Add(Ui.Form(130, ("Tiền thuế GTGT", taxTotal), ("Tổng tiền thanh toán", grandTotal)));
        var status = new TextBlock { Text = "Ctrl+F - Tìm kiếm, F9 - Thêm nhanh, F3 - Tìm nhanh, F6 - chọn mã quy cách", Margin = new Thickness(6, 0, 6, 2) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        DockPanel.SetDock(totals, Dock.Bottom);
        root.Children.Add(totals);

        foreach (var caption in new[] { profile.Tab(screen, "items"), profile.Tab(screen, "tax"), "3. Giá vốn", "4. Thống kê", "5. Khác" })
            detailTabs.Items.Add(new TabItem { Header = caption });
        detailTabs.SelectedIndex = 0;
        detailTabs.SelectionChanged += (_, e) => { if (e.Source == detailTabs) OnDetailTabChanged(); };
        detailTabs.Margin = new Thickness(4, 4, 4, 0);
        DockPanel.SetDock(detailTabs, Dock.Top);
        root.Children.Add(detailTabs);

        grid = new FakeGrid(profile.GridId(screen, "items"), 230, scenario.GridFooterOutside)
        {
            ShowInsertionRow = true, Margin = new Thickness(4, 0, 4, 0), FooterCountOffset = scenario.GridFooterCountOffset,
            R20UnitList = scenario.R20UnitList, UnitListOpenDelayMilliseconds = scenario.R20UnitList ? 800 : 0,
            TaxRateValueIsKey = scenario.TaxRateValueIsKey, TaxRateListOpensOnClick = scenario.TaxRateListOpensOnClick
        };
        grid.CellCommitted += OnCellCommitted;
        grid.CellClicked += (_, click) =>
        {
            // R20 (report 0.2.172): a click on the add row's cells starts a new line holding the line above's item.
            if (scenario.R20UnitList && click.Row is null && lines.Count > 0 && !saved)
            {
                var above = lines[^1];
                lines.Add(new LineModel
                {
                    Code = above.Code, Name = above.Name, Unit = above.Unit, DebitAccount = above.DebitAccount,
                    RevenueAccount = above.RevenueAccount, Quantity = above.Quantity, UnitPrice = above.UnitPrice,
                    Amount = above.Amount, TaxRate = above.TaxRate, TaxAmount = above.TaxAmount
                });
                FakeLog.Write($"click on the add row ({click.Key}) started line {lines.Count - 1} with {above.Code}");
                dirty = true;
                Dispatcher.BeginInvoke(() => { grid.Refresh(force: true); RefreshTotals(); });
                return;
            }
            if (scenario.BusyAfterQuantityClickMilliseconds <= 0 || busyAfterQuantityClickShown || click.Key != "quantity") return;
            busyAfterQuantityClickShown = true;
            // R20 recalculating on the Số lượng click (report 0.2.171, HĐ 125): its UI thread stops answering UIA while
            // the tool's dialog check right after the click runs (ElementFromHandle → UIA_E_TIMEOUT).
            FakeLog.Write("MISA busy after the quantity click");
            Thread.Sleep(scenario.BusyAfterQuantityClickMilliseconds);
            FakeLog.Write("MISA answers again");
        };
        grid.QuickAddRequested += (_, row) => QuickAddItem(row);
        grid.QuickSearchRequested += (_, _) => new ItemLookupWindow(profile, store) { Owner = main }.ShowDialog();
        Activated += (_, _) => UpdateInactiveGrid();
        Deactivated += (_, _) => UpdateInactiveGrid();
        grid.TaxAmountRead += (_, _) =>
        {
            if (!scenario.MainActivatedOnFirstTaxAmountRead || mainActivatedOnce) return;
            mainActivatedOnce = true;
            FakeLog.Write("user activates the main window after the first Tiền thuế GTGT read");
            Dispatcher.BeginInvoke(() => main.Activate());
        };
        if (scenario.GridFooterOutside)
        {
            // Footer drawn over the grid's bottom edge, a UIA sibling of the grid (not its descendant).
            var host = new Grid();
            host.Children.Add(grid);
            grid.FooterBar.VerticalAlignment = VerticalAlignment.Bottom;
            grid.FooterBar.Margin = new Thickness(5, 0, 5, 1);
            host.Children.Add(grid.FooterBar);
            root.Children.Add(host);
        }
        else root.Children.Add(grid);
        Content = root;
        BuildLayouts();
        BindLayout();
        SyncHeader();
    }

    private string Column(string key) => profile.Column(screen, "items", key);

    private void BuildLayouts()
    {
        itemLayout = purchase && expense
        ?
        [
            new("code", Column("code"), 110, CellKind.Edit), new("name", Column("name"), 200, CellKind.Edit),
            new("debit_account", Column("expense_account"), 80, CellKind.Edit), new("credit_account", Column("credit_account"), 80, CellKind.Edit),
            new("unit", Column("unit"), 60, CellKind.Combo, store.Units), new("quantity", Column("quantity"), 80, CellKind.Edit),
            new("unit_price", Column("unit_price"), 90, CellKind.Edit), new("amount", Column("amount"), 100, CellKind.Edit)
        ]
        : purchase
        ?
        [
            new("code", Column("code"), 110, CellKind.Edit), new("name", Column("name"), 200, CellKind.Edit),
            new("stock", Column("stock"), 60, CellKind.Combo, store.Stocks),
            new("debit_account", Column("debit_account"), 70, CellKind.Edit), new("credit_account", Column("credit_account"), 80, CellKind.Edit),
            new("unit", Column("unit"), 60, CellKind.Combo, store.Units), new("quantity", Column("quantity"), 80, CellKind.Edit),
            new("unit_price", Column("unit_price"), 90, CellKind.Edit), new("amount", Column("amount"), 100, CellKind.Edit)
        ]
        :
        [
            new("code", Column("code"), 110, CellKind.Edit), new("name", Column("name"), 220, CellKind.Edit),
            new("debit_account", Column("debit_account"), 90, CellKind.Edit), new("revenue_account", "TK doanh thu", 80, CellKind.Edit),
            new("unit", Column("unit"), 70, CellKind.Combo, store.Units), new("quantity", Column("quantity"), 80, CellKind.Edit),
            new("unit_price", Column("unit_price"), 90, CellKind.Edit), new("amount", Column("amount"), 100, CellKind.Edit),
            new("discount_rate", "Tỷ lệ CK (%)", 80)
        ];
        taxLayout =
        [
            new("code", Column("code"), 110, CellKind.Edit), new("name", Column("name"), 220),
            new("tax_description", "Diễn giải thuế", 170), new("tax_rate", Column("tax_rate"), 90, CellKind.Combo, store.TaxRates),
            new("tax_amount", Column("tax_amount"), 110, CellKind.Edit), new("tax_account", "TK thuế GTGT", 90)
        ];
    }

    private void BindLayout() => grid.Bind(detailTabs.SelectedIndex == 1 ? taxLayout : itemLayout, this);

    private void UpdateInactiveGrid()
    {
        var hidden = scenario.GridCelllessWhileEditorInactive && !IsActive && detailTabs.SelectedIndex == 1;
        if (hidden == grid.RowsHidden) return;
        FakeLog.Write(hidden ? "voucher window inactive: Thuế grid exposes cell-less ordinal rows only"
            : "voucher window active: Thuế grid exposes its cells again");
        grid.HideRowsFromUia(hidden);
    }

    private void OnDetailTabChanged()
    {
        grid.ShadowBand = scenario.GridShadowBandOnTaxTab && detailTabs.SelectedIndex == 1;
        grid.ShadowOverlay = scenario.GridShadowOverlayOnTaxTab && detailTabs.SelectedIndex == 1;
        BindLayout();
        UpdateInactiveGrid();
        if (detailTabs.SelectedIndex == 1 && scenario.GridCellsRecreatedOnReadAfterTaxTab && !grid.RecreateCellsOnRead)
        {
            grid.RecreateCellsOnRead = true;
            FakeLog.Write("grid recreates its rows after each Tiền thuế GTGT read (after Thuế tab)");
        }
        if (detailTabs.SelectedIndex != 1 || scenario.GridUnavailableAfterTaxTabMilliseconds <= 0 || gridWentUnavailable) return;
        gridWentUnavailable = true;
        grid.CellsUnavailableUntil = DateTime.UtcNow.AddMilliseconds(scenario.GridUnavailableAfterTaxTabMilliseconds);
        FakeLog.Write($"grid cells unavailable for {scenario.GridUnavailableAfterTaxTabMilliseconds} ms after Thuế tab");
    }

    int IGridSource.RowCount => lines.Count;

    string IGridSource.Value(int row, string key)
    {
        var line = lines[row];
        return key switch
        {
            "code" => line.Code,
            "name" => line.Name,
            "stock" => line.Stock,
            "debit_account" => line.DebitAccount,
            "credit_account" => line.CreditAccount,
            "revenue_account" => line.RevenueAccount,
            "unit" => line.Unit,
            "quantity" => Ui.Quantity(line.Quantity),
            // R20 phiếu nhập reports its 2-decimal price padded to 4 decimals ("11571,4300").
            "unit_price" => purchase ? line.UnitPrice.ToString("N4", FakeStore.Vi) : Ui.Price(line.UnitPrice),
            "amount" => Ui.Money(line.Amount),
            "discount_rate" => "0,00",
            "tax_description" => "Thuế GTGT - " + line.Name,
            "tax_rate" => line.TaxRate,
            "tax_amount" => Ui.Money(line.TaxAmount),
            "tax_account" => "33311",
            _ => ""
        };
    }

    // ---------- header ----------

    private TextBox Field(string key, string automationId = "")
    {
        var box = Ui.Text(automationId, value: header.GetValueOrDefault(key, ""));
        bound.Add((box, key));
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitField(key, box.Text);
            e.Handled = true;
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        };
        box.LostKeyboardFocus += (_, _) => CommitField(key, box.Text);
        return box;
    }

    private Grid CustomerRow()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var code = Field("customer", profile.Id(screen, "customer"));
        code.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F9 || purchase) return;
            e.Handled = true;
            QuickAddCustomer(code);
        };
        var name = Field("customer_name", profile.Id(screen, "customer_name"));
        Grid.SetColumn(name, 1);
        row.Children.Add(code);
        row.Children.Add(name);
        return row;
    }

    private static Grid Split(UIElement left, UIElement right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private string FieldId(string element) => profile.Id(screen, element);

    private Grid ReceiptTab() => Split(
        Ui.Group("Thông tin chung", Ui.Form(100, ("Khách hàng", CustomerRow()), ("Người nộp", Field("payer")),
            ("Địa chỉ", Field("address")), ("Lý do nộp", Field("reason")), ("NV bán hàng", Ui.Text()), ("Kèm theo", Ui.Text()))),
        Ui.Group("Chứng từ", Ui.Form(100,
            ("Ngày hạch toán", Field("posted_date", FieldId("posted_date"))),
            ("Ngày chứng từ", Field("voucher_date", FieldId("voucher_date"))),
            ("Số phiếu thu", Field("voucher_number", FieldId("voucher_number"))))));

    // R20 titles the purchase form after its voucher type ("Mua hàng trong nước không qua kho chưa thanh toán - …").
    private string VoucherTitle() => purchase
        ? "Mua hàng trong nước " + (expense ? "không qua kho" : "nhập kho") + " chưa thanh toán - " + company
        : "Bán hàng hóa, dịch vụ trong nước - Tiền mặt - " + company;

    /// <summary>
    /// Purchase "Chứng từ mua hàng" list. As in R20 (reports 0.2.188/0.2.189, HĐ 205) it takes no focus and no typing:
    /// only its arrow opens the list and a click picks the type; UIA reports the item key, not the text shown.
    /// Choosing 2 re-lays the form out in place.
    /// </summary>
    private ComboBox VoucherTypeCombo()
    {
        var combo = new KeyedComboBox
        {
            IsEditable = false, Focusable = false, ItemsSource = VoucherTypes, SelectedIndex = 0, Width = 210, Height = 22,
            Margin = new Thickness(2)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.IsLoaded && !rememberingVoucherType && combo.SelectedItem is string text) CommitField("voucher_type", text);
        };
        voucherTypeCombo = combo;
        return combo;
    }

    private static readonly string[] VoucherTypes =
    [
        "1. Mua hàng trong nước nhập kho", "2. Mua hàng trong nước không qua kho",
        "3. Mua hàng nhập khẩu nhập kho", "4. Mua hàng nhập khẩu không qua kho"
    ];
    private ComboBox? voucherTypeCombo;
    private bool rememberingVoucherType;

    // R20 opens "Thêm" with the voucher type chosen last.
    private void RememberVoucherType()
    {
        if (!purchase || voucherTypeCombo is null || scenario.VoucherTypeRemembered is not { Length: > 0 } remembered) return;
        rememberingVoucherType = true;
        try
        {
            voucherTypeCombo.SelectedItem = remembered;
            SetVoucherType(remembered);
        }
        finally { rememberingVoucherType = false; }
    }

    private Grid BuildReceiptTab()
    {
        bound.RemoveAll(item => receiptBoxes.Contains(item.Box));
        receiptBoxes.Clear();
        var first = bound.Count;
        var tab = !purchase ? ReceiptTab() : expense ? PurchaseExpenseReceiptTab() : PurchaseReceiptTab();
        foreach (var (box, _) in bound.Skip(first)) receiptBoxes.Add(box);
        return tab;
    }

    // "Chứng từ ghi nợ": no delivery person and "Số chứng từ" instead of "Số phiếu nhập" (same txtRefNo).
    private Grid PurchaseExpenseReceiptTab() => Split(
        Ui.Group("Thông tin chung", Ui.Form(100, ("Nhà cung cấp", CustomerRow()), ("Diễn giải", Field("reason")),
            ("NV mua hàng", Ui.Text()))),
        Ui.Group("Chứng từ", Ui.Form(100,
            ("Ngày hạch toán", Field("posted_date", FieldId("posted_date"))),
            ("Ngày chứng từ", Field("voucher_date", FieldId("voucher_date"))),
            ("Số chứng từ", Field("voucher_number", FieldId("voucher_number"))))));

    // R20 renames the form after its new type only once the re-layout is done (report 0.2.197, HĐ 392).
    private async void RetitleLater()
    {
        await Task.Delay(scenario.VoucherTypeRetitleMilliseconds);
        Title = VoucherTitle();
    }

    private void SetVoucherType(string text)
    {
        var value = text.Trim();
        var toExpense = value.StartsWith("2.", StringComparison.Ordinal);
        if (!purchase || saved || toExpense == expense || !toExpense && !value.StartsWith("1.", StringComparison.Ordinal)) return;
        if (lines.Count > 0)
        {
            FakeLog.Write("voucher type not changed: the voucher already has lines");
            return;
        }
        expense = toExpense;
        if (scenario.VoucherTypeRetitleMilliseconds > 0 && !rememberingVoucherType) RetitleLater();
        else Title = VoucherTitle();
        stockVoucher.Visibility = expense ? Visibility.Collapsed : Visibility.Visible;
        header["voucher_number"] = store.NextVoucherNumber(expense ? "MH0" : "NK0");
        var receipt = (TabItem)headerTabs.Items[0];
        receipt.Header = profile.Tab(screen, expense ? "expense_receipt" : "receipt");
        receipt.Content = BuildReceiptTab();
        BuildLayouts();
        BindLayout();
        SyncHeader();
        FakeLog.Write($"voucher type → {(expense ? "không qua kho" : "nhập kho")}, number {header["voucher_number"]}");
    }

    private Grid PurchaseReceiptTab() => Split(
        Ui.Group("Thông tin chung", Ui.Form(100, ("Nhà cung cấp", CustomerRow()), ("Địa chỉ", Field("address")),
            ("Người giao hàng", Field("payer")), ("Diễn giải", Field("reason")), ("NV mua hàng", Ui.Text()))),
        Ui.Group("Chứng từ", Ui.Form(100,
            ("Ngày hạch toán", Field("posted_date", FieldId("posted_date"))),
            ("Ngày chứng từ", Field("voucher_date", FieldId("voucher_date"))),
            ("Số phiếu nhập", Field("voucher_number", FieldId("voucher_number"))))));

    private Grid PurchaseInvoiceTab() => Split(
        Ui.Group("Thông tin chung", Ui.Form(100, ("Nhà cung cấp", CustomerRow()), ("Địa chỉ", Field("address")),
            ("Mã số thuế", Field("customer_tax", scenario.CustomerTaxUnlisted ? "txtUnlistedTaxCode" : FieldId("customer_tax"))),
            ("Diễn giải", Ui.Text(value: "Mua hàng theo hóa đơn")))),
        Ui.Group("Hoá đơn", Ui.Form(100, ("Mẫu số HĐ", TemplateCombo()),
            ("Ký hiệu HĐ", Field("series", FieldId("series"))),
            ("Số hóa đơn", Field("number", FieldId("number"))),
            ("Ngày hóa đơn", Field("issued_date", FieldId("issued_date"))))));

    /// <summary>
    /// R20 phiếu nhập: "Mẫu số HĐ" is combo cboInvTemplateNo whose inner cboInvTemplateNo_EmbeddableTextBox never takes
    /// UIA focus itself; the value is typed into the combo and accepted with Enter.
    /// </summary>
    private ComboBox TemplateCombo()
    {
        var id = FieldId("template");
        var combo = Ui.Combo(id, ["1", "2"], value: header.GetValueOrDefault("template", ""));
        combo.Loaded += (_, _) =>
        {
            if (combo.Template.FindName("PART_EditableTextBox", combo) is not TextBox editor) return;
            editor.Focusable = false;
            System.Windows.Automation.AutomationProperties.SetAutomationId(editor, (id.Length > 0 ? id : "cboInvTemplateNo") + "_EmbeddableTextBox");
        };
        combo.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitField("template", combo.Text);
            e.Handled = true;
            combo.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        };
        combo.LostKeyboardFocus += (_, _) => { if (!combo.IsKeyboardFocusWithin) CommitField("template", combo.Text); };
        return combo;
    }

    private Grid IssueTab() => Split(
        Ui.Group("Thông tin chung", Ui.Form(100, ("Khách hàng", CustomerRow()), ("Người nhận", Ui.Text()),
            ("Địa chỉ", Field("address")), ("Lý do xuất", Ui.Text(value: "Xuất kho bán hàng")), ("NV bán hàng", Ui.Text()))),
        Ui.Group("Chứng từ", Ui.Form(100, ("Ngày hạch toán", Field("posted_date")), ("Ngày chứng từ", Field("voucher_date")),
            ("Số phiếu xuất", Field("issue_number")))));

    private Grid InvoiceTab() => Split(
        Ui.Group("Thông tin chung", Ui.Form(100, ("Khách hàng", CustomerRow()), ("Địa chỉ", Field("address")),
            ("Mã số thuế", Field("customer_tax", scenario.CustomerTaxUnlisted ? "txtUnlistedTaxCode" : FieldId("customer_tax"))),
            ("Diễn giải", Ui.Text(value: "Xuất hóa đơn")),
            ("Người mua hàng", Field("buyer")), ("Hình thức TT", Field("payment", FieldId("payment"))))),
        Ui.Group("Hoá đơn", Ui.Form(100, ("Mẫu số HĐ", TemplateCombo()),
            ("Ký hiệu HĐ", Field("series", FieldId("series"))),
            ("Số hóa đơn", Field("number", FieldId("number"))),
            ("Ngày hóa đơn", Field("issued_date", FieldId("issued_date"))))));

    private void CommitField(string key, string text)
    {
        FakeLog.Write($"field {key} <- '{text}'{(saved ? " (ignored: saved)" : "")}");
        if (saved) return;
        if (key == "voucher_type")
        {
            SetVoucherType(text);
            return;
        }
        var value = text.Trim();
        if (key.EndsWith("_date", StringComparison.Ordinal) && DateTime.TryParseExact(value,
                ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            value = date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        if (header.GetValueOrDefault(key) == value && key != "customer") return;
        header[key] = value;
        dirty = true;
        // R20 sales voucher: choosing "Mẫu số HĐ" clears "Ký hiệu HĐ" and "Số hóa đơn" (screenshot 0.2.149).
        if (key == "template" && !purchase)
        {
            header["series"] = header["number"] = "";
            FakeLog.Write("template chosen: series and number cleared");
        }
        // Purchases find the supplier by the MST typed into "Nhà cung cấp".
        if (key == "customer" && (purchase ? store.FindSupplier(value) : store.FindCustomer(value)) is { } customer)
        {
            header["customer"] = customer.Code;
            header["customer_name"] = customer.Name;
            header["address"] = customer.Address;
            header["customer_tax"] = customer.TaxCode;
            header["payer"] = header["buyer"] = customer.Name;
            FakeLog.Write($"customer {customer.Code} → {customer.Name} / {customer.TaxCode}");
        }
        SyncHeader();
    }

    private void SyncHeader()
    {
        foreach (var (box, key) in bound)
            if (!box.IsKeyboardFocusWithin && box.Text != header.GetValueOrDefault(key, "")) box.Text = header.GetValueOrDefault(key, "");
    }

    // ---------- detail grid ----------

    private void OnCellCommitted(object? sender, CellCommitEventArgs e)
    {
        FakeLog.Write($"cell row={e.Row?.ToString() ?? "-1"} {e.Key} <- '{e.Text}' by {e.Trigger}");
        if (saved) return;
        if (grid.GhostShown && e.Key != "code")
        {
            grid.ClearGhostRow();
            FakeLog.Write("ghost row removed");
        }
        var focusRow = e.Row;
        var text = e.Text.Trim();
        if (e.Key == "code")
        {
            var item = store.FindItem(text);
            if (text.Length == 0 && e.Row is { } cleared)
            {
                lines[cleared] = new LineModel();
                grid.Refresh(force: true);
                RefreshTotals();
                return;
            }
            if (item is null) return;
            if (e.Row is { } existing) Apply(lines[existing], item);
            else
            {
                var line = new LineModel();
                Apply(line, item);
                lines.Add(line);
                focusRow = lines.Count - 1;
            }
            if (focusRow == scenario.LineErrorRow)
                Dispatcher.BeginInvoke(() => MessageBox.Show(this, "Dữ liệu không có trong danh mục.", "MISA SME 2023",
                    MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        else if (e.Row is { } row)
        {
            var line = lines[row];
            switch (e.Key)
            {
                case "name": line.Name = text; break;
                case "stock" when purchase && scenario.PurchaseDefaultsDelayMilliseconds > 0:
                    MessageBox.Show(this, "Dữ liệu không có trong danh mục.", "MISA SME 2023", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                case "stock": line.Stock = text; break;
                case "debit_account": line.DebitAccount = text; break;
                case "credit_account": line.CreditAccount = text; break;
                case "revenue_account": line.RevenueAccount = text; break;
                case "unit": line.Unit = text; break;
                case "quantity" when row == scenario.QuantityIgnoredRow && (firstVoucher || !scenario.QuantityIgnoredInFirstVoucherOnly):
                    FakeLog.Write($"Bỏ qua số lượng dòng {row + 1}");
                    break;
                case "quantity" when Ui.TryNumber(text, out var quantity):
                    line.Quantity = scenario.QuantityDecimals < 0 ? quantity
                        : Math.Round(quantity, scenario.QuantityDecimals, MidpointRounding.AwayFromZero);
                    line.Amount = Math.Round(line.Quantity * line.UnitPrice, 0, MidpointRounding.AwayFromZero);
                    Recalculate(line);
                    break;
                case "unit_price" when Ui.TryNumber(text, out var price):
                    line.UnitPrice = price;
                    line.Amount = Math.Round(line.Quantity * price, 0, MidpointRounding.AwayFromZero);
                    Recalculate(line);
                    break;
                case "amount" when Ui.TryNumber(text, out var amount):
                    line.Amount = amount;
                    // MISA derives the unit price from the typed amount.
                    line.UnitPrice = line.Quantity == 0 ? 0 : Math.Round(amount / line.Quantity, 2, MidpointRounding.AwayFromZero)
                        + scenario.UnitPriceDrift;
                    Recalculate(line);
                    if (scenario.RowRedrawAfterAmountMilliseconds > 0) RedrawRow(line);
                    // Tab from Thành tiền moves to a column right of the view; R20 scrolls the grid sideways.
                    if (scenario.ScrollSidewaysAfterAmount) grid.ScrollHorizontally(150);
                    break;
                case "tax_rate" when Ui.TaxRate(text) is { } rate:
                    // R20 does not take a typed KCT/KKKNT: the cell keeps 0% (report 0.2.201, HĐ 18534); only the list sets it.
                    line.TaxRate = scenario.TaxRateValueIsKey && e.Trigger != System.Windows.Input.Key.None && rate is "KCT" or "KKKNT"
                        ? "0%" : rate;
                    Recalculate(line);
                    break;
                case "tax_amount" when Ui.TryNumber(text, out var tax): line.TaxAmount = tax; break;
            }
        }
        else return;
        dirty = true;
        grid.Refresh(force: true);
        RefreshTotals();
        if (e.Key == "code" && scenario.GhostRowAfterItemCode && focusRow is { } recreated)
        {
            grid.ShowGhostRow(recreated);
            FakeLog.Write($"ghost row over row {recreated}");
        }
        if (grid.NextEditableKey(e.Key) is { } next) grid.FocusCell(focusRow, next);
        if (e.Key == "code" && scenario.UserSwitchesTabs && !userSwitchedTabs)
        {
            userSwitchedTabs = true;
            // Right after the first item code, as a user clicking around would.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, () =>
            {
                FakeLog.Write("user switched tabs: Phiếu xuất + Thuế");
                headerTabs.SelectedIndex = 1;
                detailTabs.SelectedIndex = 1;
            });
        }
        else grid.FocusCell(null, "code");
    }

    private void Apply(LineModel line, CatalogItem item)
    {
        line.Code = item.Code;
        line.Name = item.Name;
        line.Unit = item.Unit;
        line.UnitPrice = item.Price;
        line.Amount = Math.Round(line.Quantity * item.Price, 0, MidpointRounding.AwayFromZero);
        line.TaxRate = item.TaxRate;
        if (purchase)
        {
            // MISA fills warehouse and accounts from the item's defaults; the profile expects KHH / 1561 / 331.
            // "Không qua kho" has no Kho/TK Kho: TK chi phí comes from the item (632 unless the scenario says otherwise).
            void Defaults()
            {
                line.Stock = expense ? "" : scenario.PurchaseItemStock is { Length: > 0 } stock ? stock : profile.Setting(screen, "stock", "KHH");
                line.DebitAccount = expense ? scenario.ExpenseItemAccount
                    : scenario.PurchaseItemStockAccount is { Length: > 0 } account ? account : profile.Setting(screen, "stockAccount", "1561");
                line.CreditAccount = profile.Setting(screen, "payableAccount", "331");
            }
            if (scenario.PurchaseDefaultsDelayMilliseconds <= 0) Defaults();
            else
            {
                line.Stock = line.DebitAccount = line.CreditAccount = "";
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(scenario.PurchaseDefaultsDelayMilliseconds) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Defaults();
                    FakeLog.Write("purchase defaults filled");
                    grid.Refresh(force: true);
                };
                timer.Start();
            }
        }
        Recalculate(line);
    }

    // Real R20 phiếu nhập (report 0.2.142): after Thành tiền the row is redrawn and its code cell is briefly blank.
    private void RedrawRow(LineModel line)
    {
        var code = line.Code;
        line.Code = "";
        FakeLog.Write($"row redraw: code '{code}' blank for {scenario.RowRedrawAfterAmountMilliseconds} ms");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(scenario.RowRedrawAfterAmountMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            line.Code = code;
            grid.Refresh(force: true);
        };
        timer.Start();
    }

    private void Recalculate(LineModel line)
        => line.TaxAmount = Math.Round(line.Amount * Ui.RateValue(line.TaxRate) / 100m, 0, MidpointRounding.AwayFromZero) + scenario.TaxOffset;

    private void RefreshTotals()
    {
        var amount = lines.Sum(line => line.Amount);
        var tax = lines.Sum(line => line.TaxAmount);
        subtotal.Text = Ui.Money(amount);
        taxTotal.Text = Ui.Money(tax);
        grandTotal.Text = Ui.Money(amount + tax);
    }

    private void QuickAddCustomer(TextBox box)
    {
        if (saved || header["customer"].Length > 0) return;
        FakeLog.Write("F9 new customer");
        var dialog = new NewCustomerWindow(profile, store, scenario) { Owner = main };
        if (dialog.ShowDialog() != true || dialog.Created is not { } customer) return;
        // Like R20, "Cất" in the dialog selects the new customer on the voucher.
        CommitField("customer", customer.Code);
        box.Text = header["customer"];
    }

    private void QuickAddItem(int? row)
    {
        if (saved || row is { } index && lines[index].Code.Length > 0) return;
        FakeLog.Write("F9 new item");
        // Owned by the main window like R20, so the dialog is not a UIA descendant of the voucher editor.
        var dialog = new NewItemWindow(profile, store) { Owner = main };
        if (dialog.ShowDialog() != true || dialog.Created is not { } item) return;
        var line = row is { } existing ? lines[existing] : new LineModel();
        Apply(line, item);
        if (row is null) lines.Add(line);
        dirty = true;
        grid.Refresh(force: true);
        RefreshTotals();
        grid.FocusCell(lines.IndexOf(line), "name");
    }

    // ---------- commands ----------

    private void Save()
    {
        FakeLog.Write($"Cất (saved={saved}, lines={lines.Count}, customer='{header["customer"]}', number='{header["number"]}')");
        if (saved) return;
        if (Keyboard.FocusedElement is TextBox focused && bound.FirstOrDefault(item => item.Box == focused) is { Box: not null } field)
            CommitField(field.Key, focused.Text);
        if (lines.Count == 0 || header["customer"].Length == 0)
        {
            MessageBox.Show(this, purchase ? "Chưa có nhà cung cấp hoặc dòng hàng." : "Chưa có khách hàng hoặc dòng hàng.", Prompt, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (scenario.OutOfMemoryOnSave)
        {
            MessageBox.Show(this, "Có một số lỗi xảy ra khi thực hiện chương trình. Vui lòng liên hệ với MISA để được giúp đỡ. Chi tiết: "
                + "System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.", "MISA SME 2023",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (scenario.TaxpayerStatusPrompt)
        {
            var warning = new TaxpayerStatusWindow(header["customer_tax"]) { Owner = this };
            warning.ShowDialog();
            if (!warning.Continue) return;
        }
        if (scenario.TaxRoundingPrompt)
        {
            var source = lines.Sum(line => line.TaxAmount);
            var difference = scenario.TaxRoundingDifference;
            var prompt = new TaxPromptWindow(profile, source, source + difference, scenario.TaxPromptAsLabel,
                scenario.TaxPromptBusyMilliseconds, scenario.TaxPromptPerRate) { Owner = this };
            prompt.ShowDialog();
            if (prompt.Adjust) lines[0].TaxAmount += difference;
        }
        if (store.InvoiceExists(header["series"], header["number"]))
        {
            var answer = MessageBox.Show(this,
                $"Hóa đơn có ký hiệu {header["series"]}, số hóa đơn {header["number"]} đã tồn tại. Bạn có muốn chương trình tự động tăng số hóa đơn không?",
                Prompt, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            header["number"] = store.NextInvoiceNumber();
            SyncHeader();
        }
        if (expense && lines.Any(line => line.DebitAccount == "154"))
        {
            // TK 154 is tracked by "Khoản mục chi phí", which the tool does not enter: R20 asks whether to post anyway.
            FakeLog.Write("prompt: TK 154 thiếu Khoản mục chi phí");
            var answer = MessageBox.Show(this,
                "Lỗi khi ghi sổ chứng từ: TK 154 chi tiết theo <Khoản mục chi phí>, chứng từ hạch toán thiếu <Khoản mục chi phí>. "
                + "Bạn có muốn ghi sổ không? Nếu bạn không nhìn thấy thông tin <Khoản mục chi phí> trên giao diện chứng từ, "
                + "vui lòng xem hướng dẫn TẠI ĐÂY.", "MISA SME", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            FakeLog.Write("prompt: Khoản mục chi phí → " + answer);
            if (answer != MessageBoxResult.Yes) return;
        }
        var savedLines = lines.Select(line => new SavedLine(line.Code, line.Name, line.Unit, line.DebitAccount, line.RevenueAccount,
            line.Quantity, line.UnitPrice, line.Amount, line.TaxRate, line.TaxAmount, line.Stock, line.CreditAccount)).ToArray();
        var amount = lines.Sum(line => line.Amount);
        var tax = lines.Sum(line => line.TaxAmount);
        store.Save(new SavedVoucher(header["voucher_number"], header["number"], header["series"], header["customer"], header["customer_name"],
            header["customer_tax"], header["posted_date"], header["voucher_date"], header["issued_date"], header["payment"],
            savedLines, amount, tax, amount + tax, purchase ? (expense ? "expense" : "stock") : ""));
        saved = true;
        UserMouse.Saved = true;
        dirty = false;
        main.RefreshList();
        if (scenario.SaveBusyMilliseconds > 0)
        {
            FakeLog.Write($"MISA bận {scenario.SaveBusyMilliseconds} ms sau Cất");
            Thread.Sleep(scenario.SaveBusyMilliseconds);
        }
        if (scenario.SavePostingMilliseconds > 0)
        {
            IsEnabled = false;
            FakeLog.Write($"MISA ghi sổ: editor bị khóa {scenario.SavePostingMilliseconds} ms");
            var until = DateTime.UtcNow.AddMilliseconds(scenario.SavePostingMilliseconds);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            var stalls = 0;
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow < until)
                {
                    if (stalls++ % 3 == 0) Thread.Sleep(2500);
                    return;
                }
                timer.Stop();
                IsEnabled = true;
                FakeLog.Write("MISA ghi sổ xong");
                FinishSave();
            };
            timer.Start();
            return;
        }
        FinishSave();
    }

    private void FinishSave()
    {
        if (scenario.SaveProofDelayMilliseconds > 0 && !saveProofDelayed)
        {
            saveProofDelayed = true;
            FakeLog.Write($"Đã lưu, trạng thái đã Cất hiện sau {scenario.SaveProofDelayMilliseconds} ms");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(scenario.SaveProofDelayMilliseconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (IsVisible) FinishSave();
            };
            timer.Start();
            return;
        }
        if (scenario.SaveClosesEditor) { Close(); return; }
        save.IsEnabled = scenario.SaveKeepsCatEnabled;
        // R20 turns "Ghi sổ" into an enabled "Bỏ ghi" once the voucher is saved and posted.
        post.Content = profile.Caption(screen, "unpost");
        post.IsEnabled = true;
        foreach (var command in readOnlyCommands) command.IsEnabled = true;
        foreach (var (box, _) in bound) box.IsReadOnly = true;
        grid.ShowInsertionRow = false;
        grid.Refresh(force: true);
    }

    private void CloseVoucher()
    {
        FakeLog.Write($"Đóng (dirty={dirty}, saved={saved})");
        if (saved && scenario.CloseSwallowedOnceAfterSave && !closeSwallowed)
        {
            closeSwallowed = true;
            FakeLog.Write("Đóng bị nuốt bởi click nhầm của người dùng");
            return;
        }
        if (dirty && !saved)
        {
            if (scenario.DirtyPromptDelayMilliseconds > 0 && !promptDelayed)
            {
                promptDelayed = true;
                FakeLog.Write($"Đóng: hỏi cất sau {scenario.DirtyPromptDelayMilliseconds} ms");
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(scenario.DirtyPromptDelayMilliseconds)
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    CloseVoucher();
                    promptDelayed = false;
                };
                timer.Start();
                return;
            }
            var answer = MessageBox.Show(this, "Dữ liệu đã bị thay đổi. Bạn có muốn cất không?", Prompt,
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                Save();
                if (!saved || !IsVisible) return;
            }
        }
        Close();
    }
}
