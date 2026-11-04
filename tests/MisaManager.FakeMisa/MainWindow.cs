using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MisaManager.FakeMisa;

/// <summary>Logged-in MISA session: "Bán hàng" module (or "Mua hàng" with <see cref="Scenario.Purchase"/>) with its voucher list.</summary>
public sealed class MainWindow : Window, IGridSource
{
    private const string Company = "CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT";
    private readonly FakeProfile profile;
    private readonly Scenario scenario;
    private readonly FakeStore store;
    private readonly FakeGrid list;
    private readonly string[] listKeys;
    private readonly string listScreen;
    private readonly bool purchase;

    public MainWindow(FakeProfile profile, Scenario scenario, FakeStore store)
    {
        this.profile = profile;
        this.scenario = scenario;
        this.store = store;
        purchase = scenario.Purchase;
        listScreen = scenario.ListScreen;
        Title = profile.VersionTitle + " - " + Company;
        var area = SystemParameters.WorkArea;
        // Fixed geometry for the whole run: the automation rejects frames whose bounds/DPI changed.
        Left = area.Left;
        Top = area.Top;
        Width = Math.Min(1280, area.Width);
        Height = Math.Min(800, area.Height);
        ResizeMode = ResizeMode.CanMinimize;
        Background = Ui.Chrome;
        this.IgnoreBareAlt();

        var root = new DockPanel();
        var menu = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
        foreach (var caption in new[] { "Tệp", "Danh mục", "Nghiệp vụ", "Hệ thống", "Tiện ích", "Trợ giúp" })
            menu.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(8, 4, 8, 4) });
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var status = new TextBlock { Text = $"Máy chủ: FAKE\\SQLEXPRESS    Tên DLKT: LAVIET_2026    {profile.VersionTitle} - {Company}", Margin = new Thickness(6, 2, 6, 2) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        var navigation = new ListBox { Width = 150, ItemsSource = new[] { "Bàn làm việc", "Quỹ", "Ngân hàng", "Mua hàng", "Bán hàng", "Hóa đơn điện tử", "Kho", "Thuế", "Tổng hợp" }, SelectedIndex = purchase ? 3 : 4 };
        DockPanel.SetDock(navigation, Dock.Left);
        root.Children.Add(navigation);

        // R20: the module tab strip is above the module panel (the profile scope), not inside it.
        var active = purchase ? profile.Caption(listScreen, "module_tab") : "Bán hàng";
        var scope = new ScopePanel(profile.ScopeId(listScreen), purchase ? profile.ScopeName(listScreen) : "Bán hàng");
        var modules = new TabControl();
        string[] moduleCaptions = purchase
            ? ["Dashboard phân tích", "Đơn mua hàng", active, "Trả lại hàng mua", "Giảm giá hàng mua", "Trả tiền nhà cung cấp", "Công nợ phải trả"]
            : ["Dashboard phân tích", "Báo giá", "Đơn đặt hàng", active, "Xuất hóa đơn", "Trả lại hàng bán", "Thu nợ", "Công nợ"];
        foreach (var caption in moduleCaptions)
        {
            var tab = new TabItem { Header = caption };
            // Purchase: real R20's tab ID is unknown and the profile selects it by caption, so give it an ID the profile does not know.
            if (caption == active) tab.Id(purchase ? "TabItem Key PU-real-r20" : profile.Id(listScreen, "module_tab"));
            modules.Items.Add(tab);
        }
        var activeIndex = Array.IndexOf(moduleCaptions, active);
        modules.SelectedIndex = activeIndex;
        var content = new DockPanel();
        // R20's module toolbar is a WinForms window: the automation clicks a command and binds its HWND.
        var commands = new System.Windows.Forms.FlowLayoutPanel { BackColor = System.Drawing.Color.White, WrapContents = false };
        System.Windows.Forms.Button Command(string caption, bool enabled = true)
        {
            var button = new System.Windows.Forms.Button
            {
                Text = caption, Enabled = enabled, AutoSize = true, FlatStyle = System.Windows.Forms.FlatStyle.Flat, Margin = new System.Windows.Forms.Padding(1)
            };
            button.FlatAppearance.BorderSize = 0;
            commands.Controls.Add(button);
            return button;
        }
        var add = Command(profile.Caption(listScreen, "add"));
        if (profile.Id(listScreen, "add") is { Length: > 0 } addId) add.Name = addId;
        add.Click += (_, _) => Dispatcher.BeginInvoke(OpenVoucher);
        foreach (var (caption, enabled) in new[] { ("Xem", true), ("Xóa", true), ("Ghi sổ", true), ("Bỏ ghi", false), ("Gửi email, Zalo", true), ("Xuất khẩu", true), ("Giúp", true) })
            Command(caption, enabled);
        var toolbar = new System.Windows.Forms.Integration.WindowsFormsHost { Child = commands, Height = 30 };
        DockPanel.SetDock(toolbar, Dock.Top);
        content.Children.Add(toolbar);

        listKeys = purchase
            ? ["posted_date", "voucher_date", "voucher_number", "number", "customer", "description", "subtotal", "tax_total", "grand_total"]
            : ["posted_date", "voucher_date", "voucher_number", "number", "template", "series", "customer", "description", "subtotal", "discount", "tax_total", "grand_total"];
        string Caption(string key) => key switch
        {
            "posted_date" => "Ngày hạch toán",
            "template" => "Mẫu số HĐ",
            "series" => "Ký hiệu HĐ",
            "description" => "Diễn giải",
            "discount" => "Tiền chiết khấu",
            "tax_total" => "Tiền thuế GTGT",
            "grand_total" => "Tổng tiền thanh toán",
            _ => profile.Column(listScreen, "vouchers", key)
        };
        double ColumnWidth(string key) => key switch { "customer" => 170, "description" => 120, "template" => 60, _ => 82 };
        list = new FakeGrid(profile.GridId(listScreen, "vouchers"), 420);
        list.Bind([.. listKeys.Select(key => new GridColumn(key, Caption(key), ColumnWidth(key)))], this);
        content.Children.Add(list);
        scope.Children.Add(content);
        modules.Items.Cast<TabItem>().ElementAt(activeIndex).Content = scope;
        root.Children.Add(modules);
        Content = root;
    }

    public FakeStore Store => store;

    int IGridSource.RowCount => store.Vouchers.Count;

    string IGridSource.Value(int row, string key)
    {
        var voucher = store.Vouchers[row];
        return key switch
        {
            "posted_date" => voucher.PostedDate,
            "voucher_date" => voucher.VoucherDate,
            "voucher_number" => voucher.VoucherNumber,
            "number" => voucher.InvoiceNumber,
            "series" => voucher.Series,
            "customer" => voucher.CustomerName,
            "description" => (purchase ? "Mua hàng " : "Thu tiền bán hàng ") + voucher.CustomerName,
            "subtotal" => Ui.Money(voucher.Subtotal),
            "discount" => "0",
            "tax_total" => Ui.Money(voucher.TaxTotal),
            "grand_total" => Ui.Money(voucher.GrandTotal),
            _ => ""
        };
    }

    public void RefreshList() => list.Refresh();

    private void OpenVoucher()
    {
        if (OwnedWindows.OfType<VoucherWindow>().Any()) return;
        new VoucherWindow(profile, scenario, store, this, Company).Show();
    }
}
