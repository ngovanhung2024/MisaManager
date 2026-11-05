namespace MisaManager.Automation;

/// <summary>
/// "Thêm Khách hàng" (F9 = the + button on the voucher's customer field). <see cref="Title"/> is the dialog caption;
/// the organisation layout has Name, the individual layout FullName and IdNumber ("Số CMND").
/// </summary>
public sealed record CustomerDialogProfile(string Title, NativeSelector Organization, NativeSelector Individual,
    NativeSelector Code, NativeSelector Name, NativeSelector FullName, NativeSelector Address, NativeSelector TaxCode,
    NativeSelector IdNumber, NativeSelector Save, NativeSelector Cancel, string OpenKey);

/// <summary>Which MISA voucher the workflow fills: "Bán hàng" or "Mua hàng hóa, dịch vụ" (nhập kho).</summary>
public enum VoucherKind { Sales, Purchase }

/// <summary>
/// Typed view of the screens the invoice workflow drives (sales or purchase, see <see cref="Kind"/>). Selectors come from the
/// generic <see cref="ScreenProfile"/> data in profiles/*.json; other workflows read
/// <see cref="Screens"/> directly instead of growing this class.
/// </summary>
public sealed class WorkflowProfile
{
    public const string SalesList = "sales_list";
    public const string SalesVoucher = "sales_voucher";
    public const string PurchaseList = "purchase_list";
    public const string PurchaseVoucher = "purchase_voucher";
    public const string ItemNew = "item_new";
    public const string CustomerNew = "customer_new";
    public const string TaxRoundingPrompt = "tax_rounding_prompt";

    private static readonly string[] RequiredFields = ["customer", "customer_name", "customer_tax", "voucher_date", "posted_date",
        "issued_date", "number", "series", "currency", "payment", "voucher_number", "subtotal", "tax_total", "grand_total"];
    private static readonly string[] RequiredColumns = ["code", "name", "unit", "debit_account", "quantity", "unit_price", "amount", "tax_rate", "tax_amount"];
    private static readonly string[] RequiredSalesColumns = ["voucher_number", "number", "customer", "subtotal", "voucher_date"];
    // Purchases keep the series and payment MISA proposes, so those controls are not needed.
    private static readonly string[] PurchaseRequiredFields = ["customer", "customer_name", "customer_tax", "voucher_date", "posted_date",
        "issued_date", "number", "voucher_number", "subtotal", "tax_total", "grand_total"];
    private static readonly string[] PurchaseRequiredColumns = [.. RequiredColumns, "stock", "credit_account"];
    private static readonly string[] NonFieldElements = ["save", "close", "unpost"];

    private WorkflowProfile(ProfileDocument document, IReadOnlyDictionary<string, ScreenProfile> screens, VoucherKind kind)
    {
        Kind = kind;
        Id = document.Id;
        Revision = document.Revision;
        VersionTitle = document.VersionTitle;
        Status = document.Status;
        Culture = document.Culture;
        Screens = screens;
        ListScreen = kind == VoucherKind.Purchase ? PurchaseList : SalesList;
        var list = Screen(ListScreen);
        var voucher = Screen(kind == VoucherKind.Purchase ? PurchaseVoucher : SalesVoucher);
        var item = Screen(ItemNew);
        SalesScopeId = Scope(list);
        EditorId = Scope(voucher);
        SalesTab = list.Element("module_tab").Selector;
        OpenVoucher = list.Element("add").Selector;
        SaveVoucher = voucher.Element("save").Selector;
        DiscardVoucher = voucher.Element("close").Selector;
        UnpostVoucher = voucher.Elements.TryGetValue("unpost", out var unpost) ? unpost.Selector : null;
        foreach (var button in new[] { OpenVoucher, SaveVoucher, DiscardVoucher, UnpostVoucher ?? SaveVoucher })
            if (button.ControlType != "Button") throw new InvalidDataException("Nút chứng từ (add/save/close/unpost) phải có controlType Button.");
        ReceiptTab = voucher.Tab("receipt");
        InvoiceTab = voucher.Tab("invoice");
        ItemTab = voucher.Tab("items");
        TaxTab = voucher.Tab("tax");
        var items = voucher.Grid("items");
        var sales = list.Grid("vouchers");
        ItemGridId = items.AutomationId;
        SalesGridId = sales.AutomationId;
        Columns = items.Columns;
        SalesColumns = sales.Columns;
        Fields = voucher.Elements.Where(pair => !NonFieldElements.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Selector, StringComparer.Ordinal);
        FieldTabs = voucher.Elements.Where(pair => pair.Value.Tab is not null)
            .ToDictionary(pair => pair.Key, pair => voucher.Tab(pair.Value.Tab!), StringComparer.Ordinal);
        ItemNewCode = item.Element("code").Selector;
        ItemNewName = item.Element("name").Selector;
        ItemNewUnit = item.Element("unit").Selector;
        ItemNewStock = item.Element("stock").Selector;
        ItemNewTaxRate = item.Element("tax_rate").Selector;
        ItemNewSave = item.Element("save").Selector;
        DefaultItemStock = item.Setting("defaultStock", "KHH");
        PurchaseStock = voucher.Setting("stock", DefaultItemStock);
        StockAccount = voucher.Setting("stockAccount", "1561");
        PayableAccount = voucher.Setting("payableAccount", "331");
        if (screens.TryGetValue(CustomerNew, out var customer))
        {
            var title = customer.Scope?.Name is { Length: > 0 } caption ? caption
                : throw new InvalidDataException($"Màn hình '{CustomerNew}' cần scope.name (tiêu đề hộp thoại).");
            NewCustomer = new(title, customer.Element("organization").Selector, customer.Element("individual").Selector,
                customer.Element("code").Selector, customer.Element("name").Selector, customer.Element("full_name").Selector,
                customer.Element("address").Selector, customer.Element("tax_code").Selector, customer.Element("id_number").Selector,
                customer.Element("save").Selector, customer.Element("cancel").Selector, customer.Setting("openKey", "F9"));
            if (NewCustomer.Save.ControlType != "Button") throw new InvalidDataException("Nút Cất của hộp Thêm Khách hàng phải có controlType Button.");
        }
        var prompt = Screen(TaxRoundingPrompt);
        TaxPromptScopeId = Scope(prompt);
        TaxPromptMessage = prompt.Element("message").Selector;
        TaxPromptNo = prompt.Element("no").Selector;
        TaxPromptYes = prompt.Element("yes").Selector;

        foreach (var key in kind == VoucherKind.Purchase ? PurchaseRequiredFields : RequiredFields)
            if (!Fields.ContainsKey(key)) throw new InvalidDataException("Profile thiếu field " + key);
        foreach (var key in kind == VoucherKind.Purchase ? PurchaseRequiredColumns : RequiredColumns)
            if (!Columns.ContainsKey(key)) throw new InvalidDataException("Profile thiếu column " + key);
        foreach (var key in RequiredSalesColumns)
            if (!SalesColumns.ContainsKey(key)) throw new InvalidDataException("Profile thiếu sales column " + key);
    }

    public VoucherKind Kind { get; }
    public bool IsPurchase => Kind == VoucherKind.Purchase;
    /// <summary>Profile key of the voucher list screen (Bán hàng or Mua hàng hóa, dịch vụ).</summary>
    public string ListScreen { get; }
    /// <summary>Caption of the MISA list the user must open, for messages.</summary>
    public string ModuleCaption => IsPurchase ? "Mua hàng hóa, dịch vụ (phân hệ Mua hàng)" : "Bán hàng";
    /// <summary>Purchase lines: warehouse, stock account and payable account MISA must show (Kho KHH, TK Kho 1561, TK công nợ 331).</summary>
    public string PurchaseStock { get; }
    public string StockAccount { get; }
    public string PayableAccount { get; }
    public string Id { get; }
    public string Revision { get; }
    public string VersionTitle { get; }
    public string Status { get; }
    public string Culture { get; }
    public IReadOnlyDictionary<string, ScreenProfile> Screens { get; }
    public string EditorId { get; }
    public string SalesScopeId { get; }
    public NativeSelector SalesTab { get; }
    public string ItemGridId { get; }
    public string SalesGridId { get; }
    public string ReceiptTab { get; }
    public string InvoiceTab { get; }
    public string ItemTab { get; }
    public string TaxTab { get; }
    public NativeSelector OpenVoucher { get; }
    public NativeSelector SaveVoucher { get; }
    public NativeSelector DiscardVoucher { get; }
    /// <summary>"Bỏ ghi": enabled only once MISA has saved and posted the voucher; null when the profile has none.</summary>
    public NativeSelector? UnpostVoucher { get; }
    public NativeSelector ItemNewCode { get; }
    public NativeSelector ItemNewName { get; }
    public NativeSelector ItemNewUnit { get; }
    public NativeSelector ItemNewStock { get; }
    public NativeSelector ItemNewTaxRate { get; }
    public NativeSelector ItemNewSave { get; }
    /// <summary>Null when the profile has no "customer_new" screen: buyers without a code then stay unfinished.</summary>
    public CustomerDialogProfile? NewCustomer { get; }
    public IReadOnlyDictionary<string, NativeSelector> Fields { get; }
    /// <summary>Caption of the voucher tab that shows a field, for fields the profile places on a tab.</summary>
    public IReadOnlyDictionary<string, string> FieldTabs { get; }
    public IReadOnlyDictionary<string, string> Columns { get; }
    public IReadOnlyDictionary<string, string> SalesColumns { get; }
    /// <summary>Default warehouse for items created by the tool ("Kho ngầm định").</summary>
    public string DefaultItemStock { get; }
    public string TaxPromptScopeId { get; }
    public NativeSelector TaxPromptMessage { get; }
    public NativeSelector TaxPromptNo { get; }
    public NativeSelector TaxPromptYes { get; }

    /// <summary>True when the bound window shows the enabled sales module and its tab.</summary>
    public bool IsSalesSurface(IReadOnlyList<ElementIdentity> elements)
    {
        var scope = Screen(ListScreen).Scope!;
        return elements.Any(element => element.AutomationId == scope.AutomationId
                && (scope.Name.Length == 0 || string.Equals(element.Name, scope.Name, StringComparison.Ordinal))
                && element.Enabled && !element.Offscreen)
            && elements.Any(element => (SalesTab.AutomationId.Length == 0 || element.AutomationId == SalesTab.AutomationId)
                && (SalesTab.Name.Length == 0 || string.Equals(element.Name, SalesTab.Name, StringComparison.Ordinal))
                && (SalesTab.ControlType.Length == 0 || string.Equals(element.ControlType, SalesTab.ControlType, StringComparison.Ordinal)));
    }

    public ScreenProfile Screen(string key) => Screens.TryGetValue(key, out var screen) ? screen
        : throw new InvalidDataException($"Profile thiếu màn hình '{key}'.");

    private static string Scope(ScreenProfile screen) => screen.Scope is { AutomationId.Length: > 0 } scope ? scope.AutomationId
        : throw new InvalidDataException($"Màn hình '{screen.Key}' cần scope.automationId.");

    public static WorkflowProfile Load(string? path = null, VoucherKind kind = VoucherKind.Sales)
    {
        var (document, screens) = ProfileLoader.Read(path ?? ProfileLoader.DefaultPath);
        if (document.Status is not ("live_pending" or "supported") || document.Culture != "vi-VN")
            throw new InvalidDataException("Profile MISA không đúng trạng thái hoặc culture vi-VN.");
        return new(document, screens, kind);
    }
}
