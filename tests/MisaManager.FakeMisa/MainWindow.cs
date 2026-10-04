using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MisaManager.FakeMisa;

/// <summary>Logged-in MISA session: "Bán hàng" module with its voucher list.</summary>
public sealed class MainWindow : Window, IGridSource
{
    private const string Company = "CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT";
    private readonly FakeProfile profile;
    private readonly Scenario scenario;
    private readonly FakeStore store;
    private readonly FakeGrid list;
    private readonly string[] listKeys;

    public MainWindow(FakeProfile profile, Scenario scenario, FakeStore store)
    {
        this.profile = profile;
        this.scenario = scenario;
        this.store = store;
        Title = profile.VersionTitle + " - " + Company;
        var area = SystemParameters.WorkArea;
        // Fixed geometry for the whole run: the automation rejects frames whose bounds/DPI changed.
        Left = area.Left;
        Top = area.Top;
        Width = Math.Min(1280, area.Width);
        Height = Math.Min(800, area.Height);
        ResizeMode = ResizeMode.CanMinimize;
        Background = Ui.Chrome;

        var root = new DockPanel();
        var menu = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
        foreach (var caption in new[] { "Tệp", "Danh mục", "Nghiệp vụ", "Hệ thống", "Tiện ích", "Trợ giúp" })
            menu.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(8, 4, 8, 4) });
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var status = new TextBlock { Text = $"Máy chủ: FAKE\\SQLEXPRESS    Tên DLKT: LAVIET_2026    {profile.VersionTitle} - {Company}", Margin = new Thickness(6, 2, 6, 2) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        var navigation = new ListBox { Width = 150, ItemsSource = new[] { "Bàn làm việc", "Quỹ", "Ngân hàng", "Mua hàng", "Bán hàng", "Hóa đơn điện tử", "Kho", "Thuế", "Tổng hợp" }, SelectedIndex = 4 };
        DockPanel.SetDock(navigation, Dock.Left);
        root.Children.Add(navigation);

        var scope = new ScopePanel(profile.ScopeId("sales_list"), "Bán hàng");
        var modules = new TabControl();
        foreach (var caption in new[] { "Dashboard phân tích", "Báo giá", "Đơn đặt hàng", "Bán hàng", "Xuất hóa đơn", "Trả lại hàng bán", "Thu nợ", "Công nợ" })
        {
            var tab = new TabItem { Header = caption };
            if (caption == "Bán hàng") tab.Id(profile.Id("sales_list", "module_tab"));
            modules.Items.Add(tab);
        }
        modules.SelectedIndex = 3;
        var content = new DockPanel();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
        var add = Ui.Button(profile.Caption("sales_list", "add"), automationId: profile.Id("sales_list", "add"));
        add.Click += (_, _) => OpenVoucher();
        toolbar.Children.Add(add);
        foreach (var (caption, enabled) in new[] { ("Xem", true), ("Xóa", true), ("Ghi sổ", true), ("Bỏ ghi", false), ("Gửi email, Zalo", true), ("Xuất khẩu", true), ("Giúp", true) })
            toolbar.Children.Add(Ui.Button(caption, enabled));
        DockPanel.SetDock(toolbar, Dock.Top);
        content.Children.Add(toolbar);

        listKeys = ["posted_date", "voucher_date", "voucher_number", "number", "template", "series", "customer", "description", "subtotal", "discount", "tax_total", "grand_total"];
        string Caption(string key) => key switch
        {
            "posted_date" => "Ngày hạch toán",
            "template" => "Mẫu số HĐ",
            "series" => "Ký hiệu HĐ",
            "description" => "Diễn giải",
            "discount" => "Tiền chiết khấu",
            "tax_total" => "Tiền thuế GTGT",
            "grand_total" => "Tổng tiền thanh toán",
            _ => profile.Column("sales_list", "vouchers", key)
        };
        double ColumnWidth(string key) => key switch { "customer" => 170, "description" => 120, "template" => 60, _ => 82 };
        list = new FakeGrid(profile.GridId("sales_list", "vouchers"), 420);
        list.Bind([.. listKeys.Select(key => new GridColumn(key, Caption(key), ColumnWidth(key)))], this);
        content.Children.Add(list);
        modules.Items.Cast<TabItem>().ElementAt(3).Content = content;
        scope.Children.Add(modules);
        root.Children.Add(new ChildWindowHost(scope, "Bán hàng"));
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
            "description" => "Thu tiền bán hàng " + voucher.CustomerName,
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
