using MisaManager.Core;

namespace MisaManager.Automation;

public sealed record MisaProfileLocator(string Key, string AutomationId, string Caption,
    bool Writable, string ReadbackSource);

public sealed class MisaR20Profile
{
    public const string ProfileId = "misa-sme2023-r20-enterprise";
    public const string MisaVersion = "MISA SME 2023 R20 Enterprise";
    public const string ProfileRevision = "r20-workflow-readiness-20260926";
    public string Id => ProfileId;
    public string Revision => ProfileRevision;
    public string Status => "supported";

    public IReadOnlyList<MisaProfileLocator> Locators { get; } =
    [
        new("sales_surface", "frmSAVoucher", "Bán hàng", false, "UIA3.AutomationId+Name"),
        new("sales_tab", "TabItem Key SAVoucher", "Bán hàng", false, "UIA3.AutomationId+Name"),
        new("voucher_editor", "frmSAVoucherDetail", "", false, "UIA3.AutomationId+Name"),
        new("voucher_date", "dteRefDate", "", false, "UIA3.ValuePattern"),
        new("posted_date", "dtePostedDate", "", false, "UIA3.ValuePattern"),
        new("voucher_number", "txtRefNo", "", false, "UIA3.ValuePattern"),
        new("customer", "cboAccountObjectID", "", false, "UIA3.ValuePattern"),
        new("customer_name", "txtAccountObjectName", "", false, "UIA3.ValuePattern"),
        new("description", "txtJournalMemo", "", false, "UIA3.ValuePattern"),
        new("line_amount", "creTotalTotalSaleAmountOC", "", false, "UIA3.ValuePattern"),
        new("line_tax", "creTotalVATAmountOC", "", false, "UIA3.ValuePattern"),
        new("grand_total", "creTotalAmountOC", "", false, "UIA3.ValuePattern")
    ];

    public AutomationCapability Capability => new(ProfileId, MisaVersion, "read_write",
        ["sales_surface_observation", "window_identity", "native_frame", "workflow_input", "reconciliation", "save_discard"]);

    public bool IsSalesSurface(IReadOnlyList<ElementIdentity> elements)
        => elements.Any(element => element.AutomationId == "frmSAVoucher"
            && string.Equals(element.Name, "Bán hàng", StringComparison.Ordinal)
            && element.Enabled && !element.Offscreen)
        && elements.Any(element => element.AutomationId == "TabItem Key SAVoucher"
            && string.Equals(element.Name, "Bán hàng", StringComparison.Ordinal));

    public bool HasVoucherEditorForCompany(IReadOnlyList<ElementIdentity> elements, string expectedCompany,
        out string? actualCompany)
    {
        actualCompany = null;
        var editors = elements.Where(element => element.AutomationId == "frmSAVoucherDetail"
            && element.ControlType == "Window" && element.Enabled && !element.Offscreen).ToArray();
        if (editors.Length == 0) return false;
        actualCompany = editors.Length == 1 ? ExtractCompany(editors[0].Name) : null;
        return editors.Length == 1 && string.Equals(actualCompany, expectedCompany, StringComparison.Ordinal);
    }

    public string? VoucherEditorCompany(IReadOnlyList<ElementIdentity> elements)
    {
        var editor = elements.Where(element => element.AutomationId == "frmSAVoucherDetail"
            && element.ControlType == "Window" && element.Enabled && !element.Offscreen).ToArray();
        return editor.Length == 1 ? ExtractCompany(editor[0].Name) : null;
    }

    private static string? ExtractCompany(string title)
    {
        var separator = title.LastIndexOf(" - ", StringComparison.Ordinal);
        return separator >= 0 && separator + 3 < title.Length ? title[(separator + 3)..].Trim() : null;
    }

    public IReadOnlyList<string> MissingLiveMatrix(WorkflowStatus target)
        => target switch
        {
            WorkflowStatus.SalesSurface => [],
            WorkflowStatus.VoucherCreated => ["open_voucher", "voucher_editor_identity"],
            WorkflowStatus.CustomerVerified => ["customer_exact_code_name_tax_identity"],
            WorkflowStatus.DatesVerified => ["voucher_date_readback", "posted_date_readback", "issued_date_readback"],
            WorkflowStatus.InvoiceVerified => ["series_number_type_currency_payment_readback"],
            WorkflowStatus.ItemsVerified => ["item_grid_row_identity", "line_input_readback"],
            WorkflowStatus.TotalsVerified => ["line_total_readback", "invoice_total_readback"],
            WorkflowStatus.AwaitingSave => ["save_boundary", "authoritative_unsaved_readback"],
            _ => []
        };
}
