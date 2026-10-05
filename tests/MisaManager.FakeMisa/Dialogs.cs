using System.Windows;
using System.Windows.Controls;

namespace MisaManager.FakeMisa;

/// <summary>"Thêm Vật tư, hàng hóa, dịch vụ" opened with F9 from the Mã hàng cell.</summary>
public sealed class NewItemWindow : Window
{
    private const string Prompt = "MISA SME 2023";
    private readonly FakeStore store;
    private readonly TextBox code, name;
    private readonly ComboBox unit, stock, tax;

    public NewItemWindow(FakeProfile profile, FakeStore store)
    {
        this.store = store;
        Title = "Thêm Vật tư, hàng hóa, dịch vụ";
        Width = 700;
        Height = 420;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Chrome;
        this.IgnoreBareAlt();
        string Id(string key) => profile.Id("item_new", key);
        code = Ui.Text(Id("code"), value: store.NextItemCode());
        name = Ui.Text(Id("name"));
        unit = Ui.Combo(Id("unit"), store.Units);
        stock = Ui.Combo(Id("stock"), store.Stocks);
        tax = Ui.Combo(Id("tax_rate"), store.TaxRates);
        var form = Ui.Form(150, ("Tính chất", new ComboBox { ItemsSource = new[] { "Vật tư hàng hóa" }, SelectedIndex = 0, Margin = new Thickness(2) }),
            ("Mã (*)", code), ("Tên (*)", name), ("ĐVT chính", unit), ("Kho ngầm định", stock), ("Thuế suất GTGT (%)", tax),
            ("Nhóm VTHH", Ui.Text()), ("Mô tả", Ui.Text()));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        var save = Ui.Button(profile.Caption("item_new", "save"), automationId: Id("save"), width: 80);
        save.Click += (_, _) => Save();
        var cancel = Ui.Button("Hủy bỏ", width: 80);
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(save);
        buttons.Children.Add(Ui.Button("Cất & Thêm", width: 90));
        buttons.Children.Add(cancel);
        var tabs = new TabControl { Margin = new Thickness(6) };
        tabs.Items.Add(new TabItem { Header = "1. Thông tin chung", Content = form });
        tabs.Items.Add(new TabItem { Header = "2. Ngầm định" });
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    public CatalogItem? Created { get; private set; }

    private void Save()
    {
        var itemCode = code.Text.Trim();
        if (itemCode.Length == 0 || name.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Mã và Tên không được để trống.", Prompt, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (store.ItemCodeTaken(itemCode))
        {
            MessageBox.Show(this, $"Mã <{itemCode}> đã tồn tại trong danh mục Vật tư, hàng hóa. Vui lòng kiểm tra lại.", Prompt,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!store.Units.Contains(unit.Text.Trim(), StringComparer.Ordinal))
        {
            MessageBox.Show(this, $"ĐVT <{unit.Text}> không có trong danh mục Đơn vị tính.", Prompt, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Created = new CatalogItem(itemCode, store.CreatedItemNameOverride ?? name.Text.Trim(), unit.Text.Trim(), Ui.TaxRate(tax.Text) ?? "");
        store.AddItem(Created);
        DialogResult = true;
    }
}

/// <summary>MISA's warning when the voucher VAT total differs from the per-rate calculation.</summary>
public sealed class TaxPromptWindow : Window
{
    public TaxPromptWindow(FakeProfile profile, decimal source, decimal calculated)
    {
        Ui.Id(this, profile.ScopeId("tax_rounding_prompt"));
        Title = "MISA SME 2023";
        Width = 560;
        Height = 230;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.IgnoreBareAlt();
        var message = $"Tổng tiền thuế GTGT <{Ui.Money(source)}> khác (Tiền hàng - Tiền chiết khấu) * Thuế suất <{Ui.Money(calculated)}>, "
            + $"chênh lệch <{Ui.Money(source - calculated)}>. Bạn có muốn chương trình tự điều chỉnh số tiền chênh lệch này vào dòng hàng đầu tiên "
            + "có tiền thuế không? Xem hướng dẫn <a href=\"https://helpsme.misa.vn/2023/kb/canh_bao_tong_tien_thue_chenh_lech_khi_cat_chung_tu_ban_hang/\">TẠI ĐÂY</a>";
        var text = new TextBox { Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 120, Margin = new Thickness(8) }
            .Id(profile.Id("tax_rounding_prompt", "message"));
        var yes = Ui.Button("Có", width: 80);
        yes.Click += (_, _) => { Adjust = true; Close(); };
        var no = Ui.Button("Không", automationId: profile.Id("tax_rounding_prompt", "no"), width: 80);
        no.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(text);
        Content = root;
    }

    public bool Adjust { get; private set; }
}
