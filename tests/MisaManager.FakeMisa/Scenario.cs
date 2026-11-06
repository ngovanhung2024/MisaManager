using System.Text.Json;
using System.Text.Json.Serialization;

namespace MisaManager.FakeMisa;

/// <summary>Switches for behaviours real MISA shows in different situations.</summary>
public sealed record Scenario
{
    /// <summary>R20 normally closes the voucher after Cất; false keeps it open read-only with Cất disabled.</summary>
    public bool SaveClosesEditor { get; init; } = true;
    /// <summary>Vouchers already in the database (shown in the list, trigger the duplicate-number prompt).</summary>
    public IReadOnlyList<SavedVoucher> ExistingVouchers { get; init; } = [];
    /// <summary>Shows MISA's "tổng tiền thuế chênh lệch" prompt on Cất.</summary>
    public bool TaxRoundingPrompt { get; init; }
    /// <summary>Difference MISA reports in that prompt; "Có" adds it to the first line.</summary>
    public decimal TaxRoundingDifference { get; init; } = 1;
    /// <summary>Prompt text in a label exposing only Name (no Value/Text pattern), as real MISA's message box does.</summary>
    public bool TaxPromptAsLabel { get; init; }
    /// <summary>MISA's UI thread stops answering (UIA times out) for this long right after the tax prompt shows.</summary>
    public int TaxPromptBusyMilliseconds { get; init; }
    /// <summary>The per-rate wording "Tiền thuế GTGT của từng loại thuế suất ... Chênh lệch &lt;d&gt;" with Yes/No buttons.</summary>
    public bool TaxPromptPerRate { get; init; }
    /// <summary>On Cất, warns that the buyer's MST may have stopped operating and asks whether to keep saving.</summary>
    public bool TaxpayerStatusPrompt { get; init; }
    /// <summary>Added to every computed line VAT, to simulate MISA rounding differently from the source.</summary>
    public decimal TaxOffset { get; init; }
    /// <summary>Item codes the new-item dialog refuses with "đã tồn tại" (created by another user meanwhile).</summary>
    public IReadOnlyList<string> TakenItemCodes { get; init; } = [];
    /// <summary>Extra rows used to model a live MISA catalog differing from a local export.</summary>
    public IReadOnlyList<CatalogItem> AdditionalItems { get; init; } = [];
    public bool IncompleteItemLookup { get; init; }
    /// <summary>Lookup builds showing voucher-style column captions instead of Mã/Tên/ĐVT chính.</summary>
    public bool ItemLookupUsesVoucherColumns { get; init; }
    public string? CreatedItemNameOverride { get; init; }
    /// <summary>Also opens the "MISA SME 2023" login window next to the session window.</summary>
    public bool ShowLoginWindow { get; init; }
    /// <summary>R20 builds whose "Hóa đơn" tab shows the buyer MST in a control the profile selector does not match.</summary>
    public bool CustomerTaxUnlisted { get; init; }
    /// <summary>
    /// User's hand on the mouse: "always" or "after_save". A click whose press is not preceded by the
    /// tool's own move to the same point (cursor placed separately) lands where the user moved the cursor.
    /// </summary>
    public string UserMouseNudge { get; init; } = "";
    /// <summary>User clicks other tabs mid-entry: header → "Phiếu xuất" after the posted date and the invoice number, detail → "Thuế" after the first item code.</summary>
    public bool UserSwitchesTabs { get; init; }
    /// <summary>The first "Đóng" after Cất is swallowed (a stray user click took it).</summary>
    public bool CloseSwallowedOnceAfterSave { get; init; }
    /// <summary>MISA is busy posting right after Cất: the UI thread stops answering (UIA times out) for this long.</summary>
    public int SaveBusyMilliseconds { get; init; }
    /// <summary>With the editor kept open after Cất, Cất stays enabled; only "Bỏ ghi" shows the voucher was posted.</summary>
    public bool SaveKeepsCatEnabled { get; init; }
    /// <summary>The "Địa chỉ" box of "Thêm Khách hàng" (organisation) refuses input, so the tool fails before the dialog's Cất.</summary>
    public bool NewCustomerAddressReadOnly { get; init; }
    /// <summary>The "Mua hàng" module: purchase list and "Mua hàng hóa, dịch vụ" editor (purchase_list / purchase_voucher screens).</summary>
    public bool Purchase { get; init; }
    /// <summary>
    /// Real R20 purchase lines: Kho/TK Kho/TK công nợ appear this long after the item code, and typing into Kho shows
    /// "Dữ liệu không có trong danh mục." (failure report 0.2.137).
    /// </summary>
    public int PurchaseDefaultsDelayMilliseconds { get; init; }
    /// <summary>Zero-based line whose item code pops "Dữ liệu không có trong danh mục." (an error after earlier lines were entered).</summary>
    public int LineErrorRow { get; init; } = -1;
    /// <summary>"Số lượng" typed on this line is silently dropped: a readback failure with no MISA dialog open.</summary>
    public int QuantityIgnoredRow { get; init; } = -1;
    /// <summary>After "Thành tiền" is committed the line's code cell reads empty this long (R20 redrawing the row).</summary>
    public int RowRedrawAfterAmountMilliseconds { get; init; }
    /// <summary>After "Thành tiền" the grid scrolls sideways: Tên hàng slides under the frozen Mã hàng column (report 0.2.143).</summary>
    public bool ScrollSidewaysAfterAmount { get; init; }
    /// <summary>Added to the price MISA derives from a typed amount (R20 showed 11.523.809,67 for 57.619.048 / 5).</summary>
    public decimal UnitPriceDrift { get; init; }
    /// <summary>Extra suppliers (found by MST) on top of the built-in ones.</summary>
    public IReadOnlyList<Customer> AdditionalSuppliers { get; init; } = [];
    /// <summary>
    /// After the detail tab first switches to "Thuế", grid cells answer UIA with "element not available" this long
    /// (report 0.2.159, HĐ 82: RECONCILIATION_PENDING "Native element disappeared during readback").
    /// </summary>
    public int GridUnavailableAfterTaxTabMilliseconds { get; init; }
    /// <summary>
    /// After the detail tab first switches to "Thuế", reading a "Tiền thuế GTGT" cell makes the grid recreate all rows and
    /// cells; references to old rows/cells answer "element not available" for good (VPS reports 0.2.160/0.2.161, HĐ 81/82).
    /// Line entry's own tax-tab read (Mã hàng, % thuế GTGT) is unaffected, as on the VPS.
    /// </summary>
    public bool GridCellsRecreatedOnReadAfterTaxTab { get; init; }

    [JsonIgnore] public string ListScreen => Purchase ? "purchase_list" : "sales_list";
    [JsonIgnore] public string VoucherScreen => Purchase ? "purchase_voucher" : "sales_voucher";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static Scenario Load(string? path)
    {
        var scenario = path is null ? null : JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), Json);
        return scenario is null ? new Scenario() : scenario with
        {
            ExistingVouchers = scenario.ExistingVouchers ?? [],
            TakenItemCodes = scenario.TakenItemCodes ?? [],
            AdditionalItems = scenario.AdditionalItems ?? [],
            AdditionalSuppliers = scenario.AdditionalSuppliers ?? []
        };
    }
}

public sealed record SavedLine(string Code, string Name, string Unit, string DebitAccount, string RevenueAccount,
    decimal Quantity, decimal UnitPrice, decimal Amount, string TaxRate, decimal TaxAmount, string Stock = "", string CreditAccount = "");

public sealed record SavedVoucher(string VoucherNumber, string InvoiceNumber, string Series, string CustomerCode,
    string CustomerName, string CustomerTaxCode, string PostedDate, string VoucherDate, string IssuedDate, string Payment,
    IReadOnlyList<SavedLine> Lines, decimal Subtotal, decimal TaxTotal, decimal GrandTotal);

public sealed record StateFile(IReadOnlyList<SavedVoucher> Vouchers, IReadOnlyList<CatalogItem> CreatedItems,
    IReadOnlyList<Customer> CreatedCustomers);
