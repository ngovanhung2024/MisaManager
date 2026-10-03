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
    public const int InvoiceReruns = 2;

    private static readonly HashSet<string> TransientCodes = new(StringComparer.Ordinal)
    {
        "FOCUS_FAILED", "FOCUS_LOST", "TARGET_NOT_FOUND", "BOUNDS_UNREADABLE", "WINDOW_BOUNDS_UNREADABLE",
        "GRID_OFFSCREEN", "GRID_CHANGED", "GRID_INCOMPLETE", "GRID_DROPDOWN_MISSING", "GRID_DROPDOWN_NOT_CLOSED",
        "STALE_NATIVE_FRAME", "NATIVE_READ_TIMEOUT", "NATIVE_ELEMENT_UNAVAILABLE", "NATIVE_CALL_INTERRUPTED",
        "SCOPE_NOT_FOUND", "SCOPE_DISABLED", "EDITOR_NOT_FOUND", "SALES_SURFACE_MISSING", "COMMAND_WINDOW_MISSING",
        "READBACK_UNAVAILABLE", "DPI_UNAVAILABLE", "INPUT_UNCERTAIN", "ROW_INPUT_UNCERTAIN"
    };

    public static bool IsTransient(string? code) => code is not null && TransientCodes.Contains(code);

    /// <summary>Back-off that gives the user's click or tab drag time to settle.</summary>
    public static TimeSpan Delay(int attempt) => TimeSpan.FromMilliseconds(attempt switch { 1 => 400, 2 => 900, _ => 1600 });
}
