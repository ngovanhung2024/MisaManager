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

/// <summary>
/// "Thêm Khách hàng" opened with F9 (the + button) from the voucher's customer field. Like R20 the editors have no
/// known AutomationId: the automation finds them by the form label on their left. The two layouts swap with the
/// "1. Tổ chức" / "2. Cá nhân" options, which (as in R20, report of invoice 1475) are not UIA RadioButtons.
/// </summary>
public sealed class NewCustomerWindow : Window
{
    private const string Prompt = "MISA SME 2023";
    private readonly FakeStore store;
    private bool individual;
    private readonly TextBox orgTax, orgCode, orgName, orgAddress, personCode, personName, personAddress, personTax, personId;

    public NewCustomerWindow(FakeProfile profile, FakeStore store, Scenario scenario)
    {
        this.store = store;
        Title = profile.ScopeName("customer_new");
        Width = 680;
        Height = 560;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Chrome;
        this.IgnoreBareAlt();
        // R20 proposes its next code (e.g. "NTD1"); the tool overwrites it.
        var proposed = store.NextCustomerCode();
        (orgTax, orgCode, orgName, orgAddress) = (Ui.Text(), Ui.Text(value: proposed), Ui.Text(), Ui.Text(readOnly: scenario.NewCustomerAddressReadOnly));
        (personCode, personName, personAddress, personTax, personId) = (Ui.Text(value: proposed), Ui.Text(), Ui.Text(), Ui.Text(), Ui.Text());
        var organizationForm = Ui.Form(120, ("Mã số thuế", orgTax), ("Mã (*)", orgCode), ("Tên (*)", orgName), ("Địa chỉ", orgAddress),
            ("Diễn giải", Ui.Text()), ("Điện thoại", Ui.Text()), ("Email", Ui.Text()));
        var personForm = Ui.Form(120, ("Mã (*)", personCode), ("Họ và tên (*)", personName), ("Địa chỉ", personAddress),
            ("Diễn giải", Ui.Text()), ("Mã số thuế", personTax), ("ĐT di động", Ui.Text()), ("Số CMND", personId));
        personForm.Visibility = Visibility.Collapsed;
        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        kinds.Children.Add(Option(profile.Caption("customer_new", "organization"), false));
        kinds.Children.Add(Option(profile.Caption("customer_new", "individual"), true));
        void Show(bool person)
        {
            individual = person;
            (organizationForm.Visibility, personForm.Visibility) = person ? (Visibility.Collapsed, Visibility.Visible) : (Visibility.Visible, Visibility.Collapsed);
            FakeLog.Write("customer kind " + (person ? "individual" : "organization"));
        }
        // Owner-drawn option: UIA sees only a Text with the caption.
        TextBlock Option(string caption, bool person)
        {
            var option = new TextBlock { Text = caption, Margin = new Thickness(6, 2, 12, 2), Cursor = System.Windows.Input.Cursors.Hand };
            option.MouseLeftButtonUp += (_, _) => Show(person);
            return option;
        }
        var forms = new Grid();
        forms.Children.Add(organizationForm);
        forms.Children.Add(personForm);
        var tabs = new TabControl { Margin = new Thickness(6) };
        tabs.Items.Add(new TabItem { Header = "1. Thông tin chung", Content = forms });
        tabs.Items.Add(new TabItem { Header = "2. Khác" });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        var save = Ui.Button(profile.Caption("customer_new", "save"), width: 80);
        save.Click += (_, _) => Save();
        var cancel = Ui.Button("Hủy bỏ", width: 80);
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(save);
        buttons.Children.Add(Ui.Button("Cất & Thêm", width: 90));
        buttons.Children.Add(cancel);
        var root = new DockPanel();
        DockPanel.SetDock(kinds, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(kinds);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    public Customer? Created { get; private set; }

    private void Save()
    {
        var person = individual;
        var code = (person ? personCode : orgCode).Text.Trim();
        var name = (person ? personName : orgName).Text.Trim();
        if (code.Length == 0 || name.Length == 0)
        {
            MessageBox.Show(this, "Mã và Tên không được để trống.", Prompt, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (store.FindCustomer(code) is not null)
        {
            MessageBox.Show(this, $"Mã <{code}> đã tồn tại trong danh mục Khách hàng. Vui lòng kiểm tra lại.", Prompt,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Created = new Customer(code, name, (person ? personAddress : orgAddress).Text.Trim(), (person ? personTax : orgTax).Text.Trim(),
            person, person ? personId.Text.Trim() : "");
        store.AddCustomer(Created);
        DialogResult = true;
    }
}

/// <summary>MISA's warning when the voucher VAT total differs from the per-rate calculation.</summary>
public sealed class TaxPromptWindow : Window
{
    public TaxPromptWindow(FakeProfile profile, decimal source, decimal calculated, bool asLabel = false, int busyMilliseconds = 0)
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
        FrameworkElement text = asLabel
            ? new Label { Content = message, Height = 120, Margin = new Thickness(8) }
            : new TextBox { Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 120, Margin = new Thickness(8) };
        text.Id(profile.Id("tax_rounding_prompt", "message"));
        var yes = Ui.Button("Có", automationId: profile.Id("tax_rounding_prompt", "yes"), width: 80);
        yes.Click += (_, _) => { FakeLog.Write("Hộp chênh lệch thuế: bấm Có"); Adjust = true; Close(); };
        var no = Ui.Button("Không", automationId: profile.Id("tax_rounding_prompt", "no"), width: 80);
        no.Click += (_, _) => { FakeLog.Write("Hộp chênh lệch thuế: bấm Không"); Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(text);
        Content = root;
        if (busyMilliseconds > 0)
            ContentRendered += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                FakeLog.Write($"MISA bận {busyMilliseconds} ms khi hiện hộp chênh lệch thuế");
                Thread.Sleep(busyMilliseconds);
            }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public bool Adjust { get; private set; }
}
