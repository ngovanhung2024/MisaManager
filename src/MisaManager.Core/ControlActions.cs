namespace MisaManager.Core;

public static class ControlActions
{
    public static IReadOnlyDictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["click"] = "native_invoke", ["type"] = "native_set", ["pressKey"] = "native_key",
        ["startWorkflow"] = "start_workflow", ["runWorkflow"] = "run_workflow", ["getOperationStatus"] = "get_operation_status",
        ["select_tab"] = "selectTab", ["press_key"] = "native_key", ["focus_misa_window"] = "focus"
        , ["hot_reload"] = "reload", ["reload_app"] = "reload"
        , ["runLive"] = "live_run", ["run_live"] = "live_run", ["liveRun"] = "live_run"
    };
    public static string Canonical(string action) => Aliases.GetValueOrDefault(action) ?? action;
    // Include all checkpoint budgets and transport overhead; a batch must not
    // cancel a ten-minute item transition after spending time on its headers.
    public static TimeSpan RequestTimeout(string action) => Canonical(action) switch
    {
        "run_workflow" or "live_run" or "rebind_after_restart" => TimeSpan.FromMinutes(30),
        "advance" => TimeSpan.FromMinutes(11),
        // Recovery can perform a complete native reconciliation for an item
        // checkpoint. Keep its transport budget aligned with the longest
        // guarded checkpoint instead of cancelling a read-only proof early.
        "recover" => TimeSpan.FromMinutes(15),
        "save" => TimeSpan.FromMinutes(6),
        _ => TimeSpan.FromMinutes(4)
    };
    public static bool IsDirect(string action) => Canonical(action) is "native_read" or "native_set" or "native_invoke" or "native_key" or "selectTab" or "scroll";
    public static bool MayMutate(string action) => Canonical(action) is "native_set" or "native_invoke" or "native_key"
        or "selectTab" or "scroll" or "advance" or "start_workflow" or "run_workflow" or "save" or "discard" or "resume" or "reconcile"
        or "correct_invoice" or "focus" or "recover" or "rebind_after_restart" or "pause" or "cancel"
        or "begin_repair" or "complete_repair" or "acknowledge_restart" or "reload" or "live_run";
    public static bool MaySendInput(string action) => Canonical(action) is "native_set" or "native_invoke" or "native_key"
        or "selectTab" or "scroll" or "advance" or "start_workflow" or "run_workflow" or "live_run" or "save" or "discard" or "resume" or "focus";
}
