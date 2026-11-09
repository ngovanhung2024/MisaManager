namespace MisaManager.Core;

/// <summary>
/// Failures typically caused by the user touching MISA mid-run (clicking elsewhere, switching or
/// dragging tabs, moving/minimising the window). They are retried only where a retry cannot
/// duplicate data: a step that sent no value input, a read-only reconciliation, or a whole
/// invoice whose voucher was confirmed discarded.
/// </summary>
public static class InterferenceRetry
{
    public const int StepRetries = 3;
    public const int ReconcileRetries = 2;
    /// <summary>Reruns of a failed invoice back to back; after that it waits at the end of the queue between reruns.</summary>
    public const int InvoiceReruns = 2;

    private static readonly HashSet<string> TransientCodes = new(StringComparer.Ordinal)
    {
        "FOCUS_FAILED", "FOCUS_LOST", "TARGET_NOT_FOUND", "BOUNDS_UNREADABLE", "WINDOW_BOUNDS_UNREADABLE",
        "GRID_OFFSCREEN", "GRID_CHANGED", "GRID_INCOMPLETE", "GRID_DROPDOWN_MISSING", "GRID_DROPDOWN_NOT_CLOSED",
        "STALE_NATIVE_FRAME", "NATIVE_READ_TIMEOUT", "NATIVE_ELEMENT_UNAVAILABLE", "NATIVE_CALL_INTERRUPTED",
        "SCOPE_NOT_FOUND", "SCOPE_DISABLED", "EDITOR_NOT_FOUND", "SALES_SURFACE_MISSING", "COMMAND_WINDOW_MISSING",
        "READBACK_UNAVAILABLE", "DPI_UNAVAILABLE", "INPUT_UNCERTAIN", "ROW_INPUT_UNCERTAIN",
        "GRID_NOT_STABLE", "SCOPE_IDENTITY_CHANGED", "NATIVE_BUSY", "NATIVE_OWNER_UNAVAILABLE"
    };

    /// <summary>
    /// The Windows session has no input desktop (Remote Desktop closed or minimised, screen locked): Windows refuses
    /// foreground changes and keyboard/mouse input. Not interference: the runner waits for the desktop instead of
    /// spending the retry budget, and only while no voucher value may have been sent.
    /// </summary>
    public const string DesktopUnavailableCode = "SESSION_NOT_INTERACTIVE";

    public static bool IsTransient(string? code) => code is not null && TransientCodes.Contains(code);

    // Failures a rerun reproduces: source data, catalog/customer lists, MISA values that differ from the invoice,
    // or the MISA session/profile itself. They need the user, so a batch never reruns them.
    private static readonly HashSet<string> PermanentInvoiceCodes = new(StringComparer.Ordinal)
    {
        "RECONCILIATION_FAILED", "ACCOUNT_READBACK_INVALID", "UNIT_CONVERSION_PROVENANCE_INVALID", "DOCUMENT_TYPE_UNSUPPORTED",
        "LINE_IDENTITY_INVALID", "INVALID_TEXT", "WINDOW_IDENTITY_CHANGED", "WINDOW_IDENTITY_INVALID", "NOT_MISA",
        "PROFILE_MISSING", "PROFILE_REVISION_CHANGED", "PURCHASE_PROFILE_MISSING", "CUSTOMER_AMBIGUOUS", "CUSTOMER_CODE_TAKEN",
        "CUSTOMER_SAVE_BLOCKED", "SUPPLIER_AMBIGUOUS", "INVOICE_ALREADY_EXISTS", "CUSTOMER_CODE_MISSING", "ITEM_CODE_MISSING",
        "MISA_OUT_OF_MEMORY"
    };

    /// <summary>
    /// A batch may run the invoice again from the start: its voucher was Bỏ (confirmed unsaved) or never opened, and the
    /// error is one a fresh attempt can get past (MISA busy or redrawing, a list or dialog in the way…). Catalog and
    /// data errors (ITEM_*) and the codes above are excluded. Callers must also require a clean MISA state.
    /// </summary>
    public static bool InvoiceRerunAllowed(string? code)
        => !string.IsNullOrWhiteSpace(code) && !code.StartsWith("ITEM_", StringComparison.Ordinal)
            && !PermanentInvoiceCodes.Contains(code);

    /// <summary>Pause before rerun <paramref name="rerun"/> of the same invoice, so a busy MISA can settle.</summary>
    public static TimeSpan RerunDelay(int rerun) => TimeSpan.FromSeconds(rerun switch { 1 => 2, 2 => 5, _ => 15 });

    /// <summary>Back-off that gives the user's click or tab drag time to settle.</summary>
    public static TimeSpan Delay(int attempt) => TimeSpan.FromMilliseconds(attempt switch { 1 => 400, 2 => 900, _ => 1600 });
}
