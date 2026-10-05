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
    public string DebitAccount { get; set; } = "1111";
    public string RevenueAccount { get; set; } = "5111";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string TaxRate { get; set; } = "";
    public decimal TaxAmount { get; set; }
}

/// <summary>"Chứng từ bán hàng – Bán hàng hóa, dịch vụ trong nước – Tiền mặt" editor.</summary>
public sealed class VoucherWindow : Window, IGridSource
{
    private const string Prompt = "MISA SME 2023";
    private readonly FakeProfile profile;
    private readonly Scenario scenario;
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
    private readonly TextBox subtotal, discount, taxTotal, grandTotal;
    private readonly Button save;
    private readonly List<Button> readOnlyCommands = [];
    private IReadOnlyList<GridColumn> itemLayout = [];
    private IReadOnlyList<GridColumn> taxLayout = [];
    private bool dirty;
    private bool saved;

    public VoucherWindow(FakeProfile profile, Scenario scenario, FakeStore store, MainWindow main, string company)
    {
        this.profile = profile;
        this.scenario = scenario;
        this.store = store;
        this.main = main;
        Owner = main;
        Ui.Id(this, profile.ScopeId("sales_voucher"));
        Title = "Bán hàng hóa, dịch vụ trong nước - Tiền mặt - " + company;
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
        header["voucher_number"] = store.NextVoucherNumber();
        header["issue_number"] = "XK0" + header["voucher_number"][3..];
        header["number"] = store.NextInvoiceNumber();
        header["series"] = "1C26MLV";
        header["payment"] = "Tiền mặt";
        foreach (var key in new[] { "customer", "customer_name", "address", "customer_tax", "payer", "buyer", "template" }) header[key] = "";
        header["reason"] = "Thu tiền bán hàng theo hóa đơn " + header["number"];

        var root = new DockPanel();
        var toolbar = new WrapPanel { Background = Brushes.White };
        Button Command(string caption, bool enabled, Action? action = null, bool editOnly = false)
        {
            var element = profile.Caption("sales_voucher", "save") == caption ? "save"
                : profile.Caption("sales_voucher", "close") == caption ? "close" : null;
            var button = Ui.Button(caption, enabled, element is null ? "" : profile.Id("sales_voucher", element));
            if (action is not null) button.Click += (_, _) => action();
            if (editOnly) readOnlyCommands.Add(button);
            toolbar.Children.Add(button);
            return button;
        }
        Command("Trước", false); Command("Sau", false);
        Command("Thêm", false, editOnly: true); Command("Sửa", false, editOnly: true); Command("Sửa nhanh", false);
        save = Command(profile.Caption("sales_voucher", "save"), true, Save);
        Command("Xóa", false); Command("Hoãn", true); Command("Ghi sổ", false); Command("Lập phiếu xuất", false);
        Command("Nạp", false); Command("Tiện ích", true); Command("Mẫu", true); Command("In", false);
        Command("Gửi email, Zalo", false); Command("Giúp", true);
        Command(profile.Caption("sales_voucher", "close"), true, CloseVoucher);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 0) };
        titleRow.Children.Add(new TextBlock { Text = "Chứng từ bán hàng", FontSize = 16, FontWeight = FontWeights.Bold, Width = 210 });
        titleRow.Children.Add(new ComboBox { Width = 210, ItemsSource = new[] { "1. Bán hàng hóa, dịch vụ trong nước" }, SelectedIndex = 0 });
        titleRow.Children.Add(new CheckBox { Content = "Kiêm phiếu xuất kho", IsChecked = true, Margin = new Thickness(10, 3, 0, 0) });
        titleRow.Children.Add(new CheckBox { Content = "Lập kèm hóa đơn", IsChecked = true, Margin = new Thickness(10, 3, 0, 0) });
        titleRow.Children.Add(new RadioButton { Content = "Chưa thu tiền", Margin = new Thickness(20, 3, 0, 0) });
        titleRow.Children.Add(new RadioButton { Content = "Thu tiền ngay", IsChecked = true, Margin = new Thickness(10, 3, 0, 0) });
        DockPanel.SetDock(titleRow, Dock.Top);
        root.Children.Add(titleRow);

        headerTabs = new TabControl { Height = 205, Margin = new Thickness(4, 4, 4, 0) };
        headerTabs.Items.Add(new TabItem { Header = profile.Tab("sales_voucher", "receipt"), Content = ReceiptTab() });
        headerTabs.Items.Add(new TabItem { Header = "Phiếu xuất", Content = IssueTab() });
        headerTabs.Items.Add(new TabItem { Header = profile.Tab("sales_voucher", "invoice"), Content = InvoiceTab() });
        headerTabs.SelectedIndex = 0;
        DockPanel.SetDock(headerTabs, Dock.Top);
        root.Children.Add(headerTabs);

        subtotal = Ui.Text(profile.Id("sales_voucher", "subtotal"), 140, "0", readOnly: true);
        discount = Ui.Text("", 140, "0", readOnly: true);
        taxTotal = Ui.Text(profile.Id("sales_voucher", "tax_total"), 140, "0", readOnly: true);
        grandTotal = Ui.Text(profile.Id("sales_voucher", "grand_total"), 140, "0", readOnly: true);
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

        foreach (var caption in new[] { profile.Tab("sales_voucher", "items"), profile.Tab("sales_voucher", "tax"), "3. Giá vốn", "4. Thống kê", "5. Khác" })
            detailTabs.Items.Add(new TabItem { Header = caption });
        detailTabs.SelectedIndex = 0;
        detailTabs.SelectionChanged += (_, e) => { if (e.Source == detailTabs) BindLayout(); };
        detailTabs.Margin = new Thickness(4, 4, 4, 0);
        DockPanel.SetDock(detailTabs, Dock.Top);
        root.Children.Add(detailTabs);

        grid = new FakeGrid(profile.GridId("sales_voucher", "items"), 230) { ShowInsertionRow = true, Margin = new Thickness(4, 0, 4, 0) };
        grid.CellCommitted += OnCellCommitted;
        grid.QuickAddRequested += (_, row) => QuickAddItem(row);
        grid.QuickSearchRequested += (_, _) => new ItemLookupWindow(profile, store) { Owner = main }.ShowDialog();
        root.Children.Add(grid);
        Content = root;
        BuildLayouts();
        BindLayout();
        SyncHeader();
    }

    private string Column(string key) => profile.Column("sales_voucher", "items", key);

    private void BuildLayouts()
    {
        itemLayout =
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

    int IGridSource.RowCount => lines.Count;

    string IGridSource.Value(int row, string key)
    {
        var line = lines[row];
        return key switch
        {
            "code" => line.Code,
            "name" => line.Name,
            "debit_account" => line.DebitAccount,
            "revenue_account" => line.RevenueAccount,
            "unit" => line.Unit,
            "quantity" => Ui.Quantity(line.Quantity),
            "unit_price" => Ui.Price(line.UnitPrice),
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
        var code = Field("customer", profile.Id("sales_voucher", "customer"));
        var name = Field("customer_name", profile.Id("sales_voucher", "customer_name"));
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

    private string FieldId(string element) => profile.Id("sales_voucher", element);

    private Grid ReceiptTab() => Split(
        Ui.Group("Thông tin chung", Ui.Form(100, ("Khách hàng", CustomerRow()), ("Người nộp", Field("payer")),
            ("Địa chỉ", Field("address")), ("Lý do nộp", Field("reason")), ("NV bán hàng", Ui.Text()), ("Kèm theo", Ui.Text()))),
        Ui.Group("Chứng từ", Ui.Form(100,
            ("Ngày hạch toán", Field("posted_date", FieldId("posted_date"))),
            ("Ngày chứng từ", Field("voucher_date", FieldId("voucher_date"))),
            ("Số phiếu thu", Field("voucher_number", FieldId("voucher_number"))))));

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
        Ui.Group("Hoá đơn", Ui.Form(100, ("Mẫu số HĐ", Field("template")),
            ("Ký hiệu HĐ", Field("series", FieldId("series"))),
            ("Số hóa đơn", Field("number", FieldId("number"))),
            ("Ngày hóa đơn", Field("issued_date", FieldId("issued_date"))))));

    private void CommitField(string key, string text)
    {
        FakeLog.Write($"field {key} <- '{text}'{(saved ? " (ignored: saved)" : "")}");
        if (saved) return;
        var value = text.Trim();
        if (key.EndsWith("_date", StringComparison.Ordinal) && DateTime.TryParseExact(value,
                ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            value = date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        if (header.GetValueOrDefault(key) == value && key != "customer") return;
        header[key] = value;
        dirty = true;
        if (key == "customer" && store.FindCustomer(value) is { } customer)
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
        }
        else if (e.Row is { } row)
        {
            var line = lines[row];
            switch (e.Key)
            {
                case "name": line.Name = text; break;
                case "debit_account": line.DebitAccount = text; break;
                case "revenue_account": line.RevenueAccount = text; break;
                case "unit": line.Unit = text; break;
                case "quantity" when Ui.TryNumber(text, out var quantity):
                    line.Quantity = quantity;
                    line.Amount = Math.Round(quantity * line.UnitPrice, 0, MidpointRounding.AwayFromZero);
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
                    line.UnitPrice = line.Quantity == 0 ? 0 : Math.Round(amount / line.Quantity, 2, MidpointRounding.AwayFromZero);
                    Recalculate(line);
                    break;
                case "tax_rate" when Ui.TaxRate(text) is { } rate:
                    line.TaxRate = rate;
                    Recalculate(line);
                    break;
                case "tax_amount" when Ui.TryNumber(text, out var tax): line.TaxAmount = tax; break;
            }
        }
        else return;
        dirty = true;
        grid.Refresh(force: true);
        RefreshTotals();
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
        Recalculate(line);
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
            MessageBox.Show(this, "Chưa có khách hàng hoặc dòng hàng.", Prompt, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (scenario.TaxRoundingPrompt)
        {
            var source = lines.Sum(line => line.TaxAmount);
            var difference = scenario.TaxRoundingDifference;
            var prompt = new TaxPromptWindow(profile, source, source + difference) { Owner = this };
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
        var savedLines = lines.Select(line => new SavedLine(line.Code, line.Name, line.Unit, line.DebitAccount, line.RevenueAccount,
            line.Quantity, line.UnitPrice, line.Amount, line.TaxRate, line.TaxAmount)).ToArray();
        var amount = lines.Sum(line => line.Amount);
        var tax = lines.Sum(line => line.TaxAmount);
        store.Save(new SavedVoucher(header["voucher_number"], header["number"], header["series"], header["customer"], header["customer_name"],
            header["customer_tax"], header["posted_date"], header["voucher_date"], header["issued_date"], header["payment"],
            savedLines, amount, tax, amount + tax));
        saved = true;
        UserMouse.Saved = true;
        dirty = false;
        main.RefreshList();
        if (scenario.SaveClosesEditor) { Close(); return; }
        save.IsEnabled = false;
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
