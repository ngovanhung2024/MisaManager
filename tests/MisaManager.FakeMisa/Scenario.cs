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
    /// <summary>On Cất, MISA's own crash dialog "System.OutOfMemoryException" (list grid repaint) blocks instead of saving.</summary>
    public bool OutOfMemoryOnSave { get; init; }
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
    /// <summary>Kho / TK Kho MISA proposes for an item on "nhập kho" purchase lines; empty = the profile's KHH / 1561.</summary>
    public string PurchaseItemStock { get; init; } = "";
    public string PurchaseItemStockAccount { get; init; } = "";
    /// <summary>TK chi phí MISA proposes for an item on "không qua kho" purchase lines (real R20 shows the item's 632).</summary>
    public string ExpenseItemAccount { get; init; } = "632";
    /// <summary>
    /// Report 0.2.187, HĐ 205: R20 opened the voucher form as a top-level window without owner, so UIA lists it under the
    /// desktop instead of under MISA's main window.
    /// </summary>
    public bool EditorUnowned { get; init; }
    /// <summary>Purchase voucher type R20 reopens "Thêm" with (the one chosen last), e.g. "2. Mua hàng trong nước không qua kho".</summary>
    public string VoucherTypeRemembered { get; init; } = "";
    /// <summary>After a voucher type is picked, the form keeps its old title this long (report 0.2.197, HĐ 392).</summary>
    public int VoucherTypeRetitleMilliseconds { get; init; }
    /// <summary>
    /// R20 "% thuế GTGT" cells (reports 0.2.200/0.2.201): data value through ValuePattern, label through DisplayText, and a
    /// typed KCT/KKKNT is not taken (the cell keeps 0%); the option must be picked from the list.
    /// </summary>
    public bool TaxRateValueIsKey { get; init; }
    /// <summary>
    /// Report 0.2.202, HĐ 18534: the R20 "% thuế GTGT" drop-down-list cell drops its list on a click on the cell itself
    /// (a further click on the cell keeps it open); the arrow then toggles the list closed.
    /// </summary>
    public bool TaxRateListOpensOnClick { get; init; }
    /// <summary>"Dữ liệu đã bị thay đổi…" appears this long after Bỏ (a busy R20, report 0.2.202, HĐ 18534).</summary>
    public int DirtyPromptDelayMilliseconds { get; init; }
    /// <summary>
    /// Report 0.2.203, HĐ 18534: the open "% thuế GTGT" list exposes no items through UIA; arrow keys move its selection
    /// (the cell shows the highlighted rate) and Enter closes it.
    /// </summary>
    public bool TaxRateListHiddenFromUia { get; init; }
    /// <summary>The popup exposes drawn text labels but no selectable ListItem pattern (0.2.208).</summary>
    public bool TaxRateListTextAccessible { get; init; }
    /// <summary>Unreadable list faults: close, ignore-arrows, ignore-enter or reject-selection.</summary>
    public string TaxRateListKeyFault { get; init; } = "";
    /// <summary>0.2.205: ItemStatus and combo Value keep the committed rate while its editor previews the selection.</summary>
    public bool TaxRateStatusStaleWhileOpen { get; init; }
    /// <summary>Zero-based line whose item code pops "Dữ liệu không có trong danh mục." (an error after earlier lines were entered).</summary>
    public int LineErrorRow { get; init; } = -1;
    /// <summary>"Số lượng" typed on this line is silently dropped: a readback failure with no MISA dialog open.</summary>
    public int QuantityIgnoredRow { get; init; } = -1;
    /// <summary><see cref="QuantityIgnoredRow"/> applies to the first voucher opened only: a rerun of the invoice succeeds.</summary>
    public bool QuantityIgnoredInFirstVoucherOnly { get; init; }
    /// <summary>After "Thành tiền" is committed the line's code cell reads empty this long (R20 redrawing the row).</summary>
    public int RowRedrawAfterAmountMilliseconds { get; init; }
    /// <summary>After "Thành tiền" the grid scrolls sideways: Tên hàng slides under the frozen Mã hàng column (report 0.2.143).</summary>
    public bool ScrollSidewaysAfterAmount { get; init; }
    /// <summary>Added to the price MISA derives from a typed amount (R20 showed 11.523.809,67 for 57.619.048 / 5).</summary>
    public decimal UnitPriceDrift { get; init; }
    /// <summary>Số lẻ of Số lượng in the company data: a typed quantity is rounded to it (-1: kept as typed). Report 0.2.195.</summary>
    public int QuantityDecimals { get; init; } = -1;
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
    /// <summary>"Số dòng = N" of the voucher grid is not a UIA descendant of grdDetail (VPS report 0.2.162, HĐ 81).</summary>
    public bool GridFooterOutside { get; init; }
    /// <summary>Added to the voucher grid's "Số dòng" footer, to prove the footer is still compared.</summary>
    public int GridFooterCountOffset { get; init; }
    /// <summary>
    /// On "Thuế", grdDetail also exposes a DataRowCount Tree region of disabled row copies whose cells have no area
    /// (VPS report 0.2.163, HĐ 81: "Không ánh xạ được ô native cho cột Mã hàng").
    /// </summary>
    public bool GridShadowBandOnTaxTab { get; init; }
    /// <summary>
    /// On "Thuế", the DataRowCount region's rows lie over the visible rows with no cells, and the visible rows' ids are
    /// not ordinals (VPS report 0.2.164, HĐ 81: "#0 bật [458,538 1003x23] … 0 ô").
    /// </summary>
    public bool GridShadowOverlayOnTaxTab { get; init; }
    /// <summary>
    /// R20 on the VPS (0.2.166, HĐ 81): while the voucher window is not the active window (the tool had activated MISA's
    /// main window; focus sat on the Bán hàng list), its grid on "Thuế" exposes only the DataRowCount region's ordinal rows,
    /// laid over the visible rows with no cells ("#0 bật [458,538 1003x23] … 0 ô"). Once active again it shows its cells.
    /// </summary>
    public bool GridCelllessWhileEditorInactive { get; init; }
    /// <summary>The user clicks MISA's main window once, right after the first "Tiền thuế GTGT" read (only reconcile reads it).</summary>
    public bool MainActivatedOnFirstTaxAmountRead { get; init; }
    /// <summary>
    /// Remote Desktop closed on the VPS as soon as MISA shows (report 0.2.169, HĐ 81: FOCUS_FAILED at validate): the
    /// session's input desktop is elsewhere this long, then the user reconnects.
    /// </summary>
    public int SessionDisconnectedMilliseconds { get; init; }
    /// <summary>
    /// A second enabled "-1" provider of the new row lying exactly over it, with the same values, until the row is
    /// committed again.
    /// </summary>
    public bool GhostRowAfterItemCode { get; init; }
    /// <summary>
    /// R20 "ĐVT" list (report 0.2.172, HĐ 151/164): closed lists keep their items in the UIA tree over the next row, the
    /// first list opens 800 ms late, and a click on the add row starts a line with the item above.
    /// </summary>
    public bool R20UnitList { get; init; }
    /// <summary>
    /// R20 posting a voucher (report 0.2.174, HĐ 183): after Cất the editor stays open but disabled this long, and its UI
    /// thread stops answering UIA in 2.5 s stretches, before it closes (or shows the saved state).
    /// </summary>
    public int SavePostingMilliseconds { get; init; }
    /// <summary>
    /// The voucher is already in the register after Cất, but the editor keeps Cất enabled and hides "Bỏ ghi" this long
    /// before it shows the saved state (or closes): the tool must prove the save without a second Cất.
    /// </summary>
    public int SaveProofDelayMilliseconds { get; init; }
    /// <summary>Report 0.2.171, HĐ 125: after the first Số lượng click MISA's UI thread is busy this long (UIA times out).</summary>
    public int BusyAfterQuantityClickMilliseconds { get; init; }

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
    IReadOnlyList<SavedLine> Lines, decimal Subtotal, decimal TaxTotal, decimal GrandTotal, string VoucherType = "");

public sealed record StateFile(IReadOnlyList<SavedVoucher> Vouchers, IReadOnlyList<CatalogItem> CreatedItems,
    IReadOnlyList<Customer> CreatedCustomers);
