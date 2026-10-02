using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MisaManager.Core;

namespace MisaManager.McpBridge;

internal static class Program
{
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static readonly string BridgeSessionId = "mcp-bridge:" + Guid.NewGuid().ToString("N");
    private static string? sessionNonce;
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Requests = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Capacity = new(16, 16);
    private static bool supportsStructuredContent;
    // Set when the running app is a different build; reads still work, mutations do not.
    private static string? runtimeMismatch;
    // Default surface is the five read-only agent tools; MISA-driving tools need --advanced.
    private static bool advancedMode;

    private static readonly IReadOnlyDictionary<string, string> ToolActions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["misa_wait"] = "wait_run",
        ["misa_failure"] = "failure_report",
        ["misa_ui"] = "ui_find",
        ["misa_perf"] = "perf_report",
        ["misa_compare"] = "compare_report",
        ["misa_capabilities"] = "capabilities",
        ["misa_diagnostics"] = "diagnostics",
        ["misa_live_test_state"] = "live_test_state",
        ["misa_verification_history"] = "verification_history",
        ["misa_failure_context"] = "failure_context",
        ["misa_runtime_status"] = "runtime_status",
        ["misa_focus"] = "focus",
        ["misa_observe"] = "observe",
        ["misa_inspect"] = "inspect",
        ["misa_export_tab"] = "export_tab",
        ["misa_native_read"] = "native_read",
        ["misa_native_set"] = "native_set",
        ["misa_native_invoke"] = "native_invoke",
        ["misa_native_key"] = "native_key",
        ["misa_click"] = "click",
        ["misa_type"] = "type",
        ["misa_pressKey"] = "pressKey",
        ["misa_scroll"] = "scroll",
        ["misa_selectTab"] = "selectTab",
        ["misa_reconcile"] = "reconcile",
        ["misa_recover"] = "recover",
        ["misa_start_workflow"] = "start_workflow",
        ["misa_run_workflow"] = "run_workflow",
        ["misa_live_run"] = "live_run",
        ["misa_startWorkflow"] = "startWorkflow",
        ["misa_runWorkflow"] = "runWorkflow",
        ["misa_run_live"] = "live_run",
        ["misa_advance_workflow"] = "advance",
        ["misa_pause"] = "pause",
        ["misa_resume"] = "resume",
        ["misa_workflow_status"] = "workflow_status",
        ["misa_generation_status"] = "generation_status",
        ["misa_get_operation_status"] = "get_operation_status",
        ["misa_getOperationStatus"] = "getOperationStatus",
        ["misa_correct_invoice"] = "correct_invoice",
        ["misa_edit_xml"] = "edit_xml",
        ["misa_save"] = "save",
        ["misa_discard"] = "discard",
        ["misa_cancel"] = "cancel",
        ["misa_begin_repair"] = "begin_repair",
        ["misa_complete_repair"] = "complete_repair",
        ["misa_acknowledge_restart"] = "acknowledge_restart",
        ["misa_rebind_after_restart"] = "rebind_after_restart"
        , ["misa_reload"] = "reload"
    };

    // Groups route to the existing dispatcher; old names remain callable.
    private static readonly IReadOnlyDictionary<string, string[]> ToolGroups = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["misa_read"] = ["misa_capabilities", "misa_diagnostics", "misa_live_test_state", "misa_failure_context",
            "misa_runtime_status", "misa_observe", "misa_inspect", "misa_export_tab", "misa_native_read",
            "misa_workflow_status", "misa_verification_history", "misa_generation_status", "misa_get_operation_status"],
        ["misa_control"] = ["misa_focus", "misa_native_set", "misa_native_invoke", "misa_native_key", "misa_scroll", "misa_selectTab"],
        ["misa_workflow"] = ["misa_start_workflow", "misa_advance_workflow", "misa_reconcile", "misa_recover",
            "misa_live_run", "misa_pause", "misa_resume", "misa_correct_invoice", "misa_save", "misa_discard", "misa_cancel"],
        ["misa_restart"] = ["misa_reload", "misa_acknowledge_restart", "misa_rebind_after_restart"]
    };

    private static readonly string[] AgentToolNames = ["misa_wait", "misa_failure", "misa_ui", "misa_perf", "misa_compare"];
    private static readonly string[] AdvancedToolNames = ["misa_read", "misa_control", "misa_workflow",
        "misa_run_workflow", "misa_live_run", "misa_begin_repair", "misa_complete_repair", "misa_restart"];
    private static string[] AdvertisedToolNames => advancedMode ? [.. AgentToolNames, .. AdvancedToolNames] : AgentToolNames;
    private static readonly Lazy<JsonElement> ToolCatalog = new(() => JsonSerializer.SerializeToElement(
        AdvertisedToolNames.Select(name => new
        {
            name, description = DescribeTool(name), inputSchema = BuildToolSchema(name),
            annotations = ToolAnnotations(name)
        }), ContractJson.Compact));

    private const string ServerInstructions =
        "MISA R20 observer. The user runs the tool by hand (open Excel, click 'Thêm vào MISA'). "
        + "Flow: misa_wait (blocks until the run finishes) -> if failed, misa_failure (error, step, MISA dialogs and focused element captured at failure time) "
        + "-> misa_ui only if more UI evidence is needed -> misa_compare / misa_perf on request. "
        + "All tools are read-only and never send input to MISA. Do not poll; do not read UI while the tool is running.";

    private static object ToolAnnotations(string name)
        => AgentToolNames.Contains(name)
            ? new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false }
            : name is "misa_read"
                ? new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false }
                : new { readOnlyHint = false, destructiveHint = true, idempotentHint = false, openWorldHint = false };

    private static readonly IReadOnlyDictionary<string, string> ToolDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["misa_wait"] = "Block until the current 'Thêm vào MISA' run finishes, then return a compact summary: outcome, failed step, first error line, comparison mismatches, slowest steps. "
            + "waitForStart=true (default) also waits for the user to click the button. Status accepted = timed out, call again. Never sends input.",
        ["misa_failure"] = "Everything needed to diagnose the last failure in one call: error code/message/stack, failed step and its last steps with timings, expected invoice values and active line, "
            + "comparison mismatches, MISA dialogs + focused element captured AT failure time (uiAtFailure) and now (uiNow), the app's error popup, and source files to inspect. Never sends input.",
        ["misa_ui"] = "Find MISA UI elements by text (case/diacritic-insensitive, matches Name or AutomationId), optional scopeAutomationId and controlType. "
            + "Auto-selects the workflow's MISA window. Returns compact elements with value, state and ancestor path. Refuses while the tool is driving MISA unless force=true. Never sends input.",
        ["misa_perf"] = "Per-step timing of the latest run: checkpoints, cost per step type (line:*:column), slowest steps, native queue p50/p95 and recent run durations. detail=true lists steps. Never sends input.",
        ["misa_compare"] = "Reconciliation (đối chứng) result of the latest run, or by runId/invoiceNumber: outcome, matched flag and each mismatched field with expected vs actual. includeMatched=true adds matched fields.",
        ["misa_capabilities"] = "Connect to the running MisaManager app and return the current control capabilities.",
        ["misa_diagnostics"] = "Collect fresh MISA window/process observation, workflow checkpoint and queue health without input.",
        ["misa_live_test_state"] = "Poll the current workflow and fresh MISA native state without sending input.",
        ["misa_verification_history"] = "Read persisted end-to-end verification records for successful, discarded, and failed live invoice runs.",
        ["misa_failure_context"] = "Build a bounded failure packet with workflow error, operation, runtime identity, fresh native evidence and recovery guidance without sending input.",
        ["misa_runtime_status"] = "Read app process, source/profile revision, initialization and native queue health without sending input.",
        ["misa_observe"] = "Read a fresh bounded UIA3/Win32 observation without sending input.",
        ["misa_inspect"] = "Read a fresh query or scope-filtered UIA3/Win32 observation without sending input.",
        ["misa_export_tab"] = "Export the complete bounded UIA3 element tree for one current MISA tab to a hashed JSON snapshot without sending input.",
        ["misa_start_workflow"] = "Start a typed invoice workflow at Ready; advance performs one guarded checkpoint.",
        ["misa_run_workflow"] = "Run guarded checkpoints in one call, stopping before Save. Supply a new invoice with window identity OR runId and expectedCheckpoint for an existing confirmed run. Paused/blocked/uncertain runs require explicit recovery. A step_limit result is idle, not background work. Use a stable idempotencyKey when recovering a lost response.",
        ["misa_live_run"] = "Run one invoice end-to-end with explicit executionMode=live_test or live_production. live_test always discards after reconciliation; live_production may save only after a match. Every outcome is persisted separately. Errors are terminal for this attempt; checkpoint continuation is never allowed.",
        ["misa_run_live"] = "Alias for misa_live_run.",
        ["misa_runWorkflow"] = "Compatibility alias for misa_run_workflow; runs guarded checkpoints and stops before Save.",
        ["misa_click"] = "Compatibility alias for native_invoke; requires windowHandle and a unique automationId or name.",
        ["misa_type"] = "Compatibility alias for native_set; requires windowHandle, a unique target, and value.",
        ["misa_pressKey"] = "Compatibility alias for native_key; requires windowHandle, a unique target, and key.",
        ["misa_advance_workflow"] = "Advance exactly one guarded workflow checkpoint and return native readback/input disposition.",
        ["misa_reconcile"] = "Reconcile the active voucher against the typed workbook using fresh native readback.",
        ["misa_save"] = "Explicitly Cất the active voucher after reconciliation and confirmed=true.",
        ["misa_discard"] = "Explicitly Bỏ the active voucher with confirmed=true.",
        ["misa_pause"] = "Pause the active workflow without saving or discarding the MISA voucher.",
        ["misa_resume"] = "Resume a paused workflow after matching its expected invoice revision.",
        ["misa_recover"] = "Recover a paused, blocked, or uncertain workflow with a fresh window identity and expected checkpoint.",
        ["misa_cancel"] = "Cancel the workflow without saving or discarding; resetWorkflow requires fresh readback and explicit confirmation.",
        ["misa_correct_invoice"] = "Apply a typed correction to the imported invoice draft after matching expectedRevision.",
        ["misa_edit_xml"] = "Deprecated and blocked; use typed XLSX correction or reimport the workbook.",
        ["misa_begin_repair"] = "Bind the current failed incident and return the repair packet plus a fresh MISA observation.",
        ["misa_complete_repair"] = "Record a checked source handoff; candidate restart and fresh rebind remain required.",
        ["misa_acknowledge_restart"] = "Acknowledge the checked candidate restart using release and fresh MISA identity.",
        ["misa_rebind_after_restart"] = "Freshly rebind the acknowledged candidate. For live-run incidents set restartFromBeginning=true to rerun the same invoice from Ready; never resume the failed checkpoint. Legacy checkpoint continuation remains available only for older non-live recovery.",
        ["misa_reload"] = "Schedule a guarded MisaManager candidate restart from release_directory; no MISA input is sent."
    };

    private static readonly IReadOnlyDictionary<string, object> ToolSchemas = new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["misa_wait"] = new { type = "object", properties = new {
            waitForStart = new { type = "boolean", description = "Wait for a run to start if none is running (default true)." },
            timeoutSeconds = new { type = "integer", minimum = 1, maximum = ControlActions.MaxWaitSeconds, description = "Default 900." } },
            additionalProperties = false },
        ["misa_failure"] = new { type = "object", properties = new {
            ui = new { type = "boolean", description = "Also read current MISA dialogs/focus (default true)." },
            force = new { type = "boolean", description = "Read UI even while the tool is running (slows it)." } },
            additionalProperties = false },
        ["misa_ui"] = new { type = "object", properties = new {
            text = new { type = "string", maxLength = 256, description = "Substring of Name or AutomationId; omit to list elements in scope." },
            scopeAutomationId = new { type = "string", maxLength = 256 },
            controlType = new { type = "string", description = "UIA control type, e.g. Edit, Button, Window, DataItem, Text." },
            windowHandle = new { type = "integer", description = "Only when several MISA windows are open." },
            maxResults = new { type = "integer", minimum = 1, maximum = 200, description = "Default 40." },
            force = new { type = "boolean" } },
            additionalProperties = false },
        ["misa_perf"] = new { type = "object", properties = new {
            detail = new { type = "boolean" },
            history = new { type = "integer", minimum = 0, maximum = 50, description = "Recent runs to include (default 10)." } },
            additionalProperties = false },
        ["misa_compare"] = new { type = "object", properties = new {
            runId = new { type = "string", maxLength = 256 },
            invoiceNumber = new { type = "string", maxLength = 256 },
            includeMatched = new { type = "boolean" } },
            additionalProperties = false },
        ["misa_capabilities"] = new { type = "object", additionalProperties = false },
        ["misa_diagnostics"] = new { type = "object", additionalProperties = false },
        ["misa_live_test_state"] = new { type = "object", properties = new { windowHandle = new { type = "integer" } }, additionalProperties = false },
        ["misa_verification_history"] = new { type = "object", additionalProperties = false },
        ["misa_failure_context"] = new { type = "object", properties = new { includeNative = new { type = "boolean" } }, additionalProperties = false },
        ["misa_runtime_status"] = new { type = "object", additionalProperties = false },
        ["misa_focus"] = new { type = "object", required = new[] { "windowHandle" }, properties = new { windowHandle = new { type = "integer" } }, additionalProperties = false },
        ["misa_observe"] = new { type = "object", required = new[] { "windowHandle" }, properties = new { windowHandle = new { type = "integer" }, query = new { type = "string" }, scopeAutomationId = new { type = "string" } }, additionalProperties = false },
        ["misa_inspect"] = new { type = "object", required = new[] { "windowHandle" }, properties = new { windowHandle = new { type = "integer" }, query = new { type = "string" }, scopeAutomationId = new { type = "string" } }, additionalProperties = false },
        ["misa_export_tab"] = new { type = "object", required = new[] { "windowHandle", "scopeAutomationId" }, properties = new { windowHandle = new { type = "integer" }, scopeAutomationId = new { type = "string" } }, additionalProperties = false },
        ["misa_native_read"] = DirectSchema(false, false),
        ["misa_native_set"] = DirectSchema(true, false),
        ["misa_native_invoke"] = DirectSchema(false, false),
        ["misa_native_key"] = DirectSchema(false, true),
        ["misa_click"] = DirectSchema(false, false),
        ["misa_type"] = DirectSchema(true, false),
        ["misa_pressKey"] = DirectSchema(false, true),
        ["misa_selectTab"] = new { type = "object", required = new[] { "windowHandle", "name" }, properties = new { windowHandle = new { type = "integer" }, name = new { type = "string" } }, additionalProperties = false },
        ["misa_scroll"] = DirectSchema(false, false),
        ["misa_start_workflow"] = new { type = "object", required = new[] { "invoice", "windowIdentity" }, properties = new { invoice = new { type = "object" }, companyIdentity = new { type = "string" }, windowIdentity = new { type = "string" }, liveValidation = new { type = "boolean" }, allowKgToTon = new { type = "boolean" } }, additionalProperties = false },
        ["misa_run_workflow"] = BatchSchema(),
        ["misa_live_run"] = LiveRunSchema(),
        ["misa_startWorkflow"] = new { type = "object", required = new[] { "invoice", "windowIdentity" }, properties = new { invoice = new { type = "object" }, companyIdentity = new { type = "string" }, windowIdentity = new { type = "string" }, liveValidation = new { type = "boolean" }, allowKgToTon = new { type = "boolean" } }, additionalProperties = false },
        ["misa_runWorkflow"] = BatchSchema(),
        ["misa_run_live"] = LiveRunSchema(),
        ["misa_advance_workflow"] = new { type = "object", required = new[] { "expectedRevision" }, properties = new { expectedRevision = new { type = "string" } }, additionalProperties = false },
        ["misa_reconcile"] = new { type = "object", required = new[] { "expectedRevision" }, properties = new { expectedRevision = new { type = "string" } }, additionalProperties = false },
        ["misa_save"] = new { type = "object", required = new[] { "expectedRevision", "confirmed" }, properties = new { expectedRevision = new { type = "string" }, confirmed = new { type = "boolean", @enum = new[] { true } } }, additionalProperties = false },
        ["misa_discard"] = new { type = "object", required = new[] { "expectedRevision", "confirmed" }, properties = new { expectedRevision = new { type = "string" }, confirmed = new { type = "boolean", @enum = new[] { true } }, resolveSaveUncertain = new { type = "boolean" } }, additionalProperties = false },
        ["misa_workflow_status"] = new { type = "object", additionalProperties = false },
        ["misa_recover"] = new { type = "object", required = new[] { "expectedRevision", "windowIdentity", "expectedStatus" }, properties = new { expectedRevision = new { type = "string", minLength = 1 }, windowIdentity = new { type = "string", minLength = 1 }, expectedStatus = new { type = "string", @enum = Enum.GetNames<WorkflowStatus>() } }, additionalProperties = false },
        ["misa_pause"] = new { type = "object", additionalProperties = false },
        ["misa_resume"] = new { type = "object", required = new[] { "expectedRevision" }, properties = new { expectedRevision = new { type = "string", minLength = 1 } }, additionalProperties = false },
        ["misa_correct_invoice"] = new { type = "object", required = new[] { "expectedRevision", "invoice", "correction" }, properties = new { expectedRevision = new { type = "string", minLength = 1 }, invoice = new { type = "object" }, correction = new { type = "object" } }, additionalProperties = false },
        ["misa_edit_xml"] = new { type = "object", additionalProperties = false },
        ["misa_cancel"] = new { type = "object", properties = new { resetWorkflow = new { type = "boolean" }, confirmed = new { type = "boolean", @enum = new[] { true } }, expectedRevision = new { type = "string" }, windowHandle = new { type = "integer" } }, additionalProperties = false },
        ["misa_generation_status"] = new { type = "object", additionalProperties = false },
        ["misa_get_operation_status"] = new { type = "object", required = new[] { "operationId" }, properties = new { operationId = new { type = "string" } }, additionalProperties = false },
        ["misa_getOperationStatus"] = new { type = "object", required = new[] { "operationId" }, properties = new { operationId = new { type = "string" } }, additionalProperties = false },
        ["misa_begin_repair"] = new { type = "object", required = new[] { "incident_id", "source_root" }, properties = new { incident_id = new { type = "string" }, source_root = new { type = "string" } }, additionalProperties = false },
        ["misa_complete_repair"] = new { type = "object", required = new[] { "incident_id", "release_directory", "root_cause", "affected_callers", "message" }, properties = new { incident_id = new { type = "string" }, release_directory = new { type = "string" }, root_cause = new { type = "string" }, affected_callers = new { type = "array", items = new { type = "string" } }, message = new { type = "string" } }, additionalProperties = false },
        ["misa_acknowledge_restart"] = new { type = "object", required = new[] { "incident_id", "release_directory", "window_identity" }, properties = new { incident_id = new { type = "string" }, release_directory = new { type = "string" }, window_identity = new { type = "string" } }, additionalProperties = false },
        ["misa_rebind_after_restart"] = new { type = "object", required = new[] { "incident_id", "expectedStatus", "window_identity" }, properties = new { incident_id = new { type = "string" }, expectedStatus = new { type = "string", @enum = Enum.GetNames<WorkflowStatus>() }, window_identity = new { type = "string" }, expectedRevision = new { type = "string" }, restartFromBeginning = new { type = "boolean", description = "For a failed live run, rerun the same invoice from Ready after the repaired candidate starts; never resume a checkpoint." }, continueLiveTest = new { type = "boolean" }, until = new { type = "string", @enum = Enum.GetNames<WorkflowStatus>() }, maxSteps = new { type = "integer", minimum = 1, maximum = 20 } }, additionalProperties = false },
        ["misa_reload"] = new { type = "object", properties = new { release_directory = new { type = "string" } }, additionalProperties = false }
    };

    private static object BatchSchema()
    {
        var checkpoint = new { type = "string", @enum = Enum.GetValues<WorkflowStatus>()
            .Where(value => value >= WorkflowStatus.Ready && value <= WorkflowStatus.AwaitingSave)
            .Select(value => value.ToString()).ToArray() };
        return new
        {
            type = "object", required = new[] { "expectedRevision", "idempotencyKey" },
            oneOf = new object[] {
                new { required = new[] { "invoice", "windowIdentity" } },
                new { required = new[] { "runId", "expectedCheckpoint" } }
            },
            properties = new
            {
                invoice = new { type = "object" }, companyIdentity = new { type = "string" },
                windowIdentity = new { type = "string" }, liveValidation = new { type = "boolean" },
                allowKgToTon = new { type = "boolean", description = "Explicit authorization: prefer registered Kg; if natively proven absent, convert Kg to Ton (quantity / 1000, price * 1000), preserving amount and tax. Retained only for this workbook/window." },
                runId = new { type = "string" }, expectedCheckpoint = checkpoint,
                expectedRevision = new { type = "string", minLength = 1 },
                idempotencyKey = new { type = "string", minLength = 1, maxLength = 256 },
                operationId = new { type = "string", minLength = 1, maxLength = 256 },
                until = checkpoint, maxSteps = new { type = "integer", minimum = 1, maximum = 20 }
            },
            additionalProperties = false
        };
    }

    private static object LiveRunSchema() => new
    {
        type = "object",
        required = new[] { "invoice", "windowIdentity", "executionMode", "idempotencyKey" },
        properties = new
        {
            invoice = new { type = "object" },
            companyIdentity = new { type = "string" },
            windowIdentity = new { type = "string", minLength = 1 },
            liveValidation = new { type = "boolean" },
            executionMode = new { type = "string", @enum = new[] { "live_test", "live_production" } },
            allowKgToTon = new { type = "boolean" },
            idempotencyKey = new { type = "string", minLength = 1, maxLength = 256 },
            operationId = new { type = "string", minLength = 1, maxLength = 256 }
        },
        additionalProperties = false
    };

    private static object DirectSchema(bool requireValue, bool requireKey)
    {
        var required = new List<string> { "windowHandle" };
        if (requireValue) required.Add("value");
        if (requireKey) required.Add("key");
        return new
        {
            type = "object",
            required,
            properties = new
            {
                windowHandle = new { type = "integer" },
                automationId = new { type = "string" },
                name = new { type = "string" },
                value = new { type = "string" },
                key = new { type = "string" },
                scopeAutomationId = new { type = "string" }
            },
            additionalProperties = false
        };
    }

    public static async Task<int> Main(string[] args)
    {
        advancedMode = args.Contains("--advanced", StringComparer.Ordinal)
            || Environment.GetEnvironmentVariable("MISA_MCP_ADVANCED") == "1";
        args = args.Where(arg => arg != "--advanced").ToArray();
        if (args.Length > 0)
        {
            if (args[0] == "--call")
            {
                try
                {
                    if (args.Length < 2 || !ToolActions.Values.Contains(args[1])) throw new ArgumentException("Unknown control action.");
                    string? Option(string key)
                    {
                        var index = Array.IndexOf(args, key);
                        return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing value for " + key);
                    }
                    var outputPath = CommandOutputPath.Resolve(Option("--output"));
                    var payload = Option("--arguments-file") is { } file
                        ? JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(file)) : EmptyPayload();
                    var id = Option("--request-id") ?? Guid.NewGuid().ToString("N");
                    var cliKey = Option("--idempotency-key");
                    if (ControlActions.MayMutate(args[1]) && string.IsNullOrWhiteSpace(cliKey))
                        throw new ArgumentException("Mutation requires --idempotency-key; retain it and the identical body for lost-response recovery.");
                    var revision = Option("--expected-revision");
                    if (payload.TryGetProperty("expectedRevision", out var payloadRevision))
                    {
                        if (payloadRevision.ValueKind != JsonValueKind.String
                            || revision is not null && revision != payloadRevision.GetString())
                            throw new ArgumentException("Conflicting or invalid expectedRevision.");
                        revision ??= payloadRevision.GetString();
                    }
                    var request = new ControlRequest(ProductIdentity.ControlProtocolVersion, id,
                        cliKey ?? "cli:" + id, Option("--session-id") ?? BridgeSessionId,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Add(ControlActions.RequestTimeout(args[1])), args[1], revision, payload);
                    var result = await ForwardRequestAsync(request);
                    var json = JsonSerializer.Serialize(result, ContractJson.Options);
                    if (outputPath is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                        await File.WriteAllTextAsync(outputPath, json, new UTF8Encoding(false));
                    }
                    else Console.WriteLine(json);
                    return result.Status is "completed" or "accepted" ? 0 : 2;
                }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            }
            if (args.Length != 1 || args[0] != "--health-check")
            {
                Console.Error.WriteLine("Use --health-check, --call <action> [--arguments-file <json>] [--output <json>], or no arguments for MCP stdio.");
                return 2;
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "healthy", product = ProductIdentity.TechnicalName + ".McpBridge",
                version = typeof(Program).Assembly.GetName().Version?.ToString(),
                sourceRevision = ProductIdentity.SourceRevision, coreAssemblySha256 = ProductIdentity.RuntimeAssemblySha256,
                protocolVersion = ProductIdentity.ControlProtocolVersion, schemaVersion = ProductIdentity.StateSchemaVersion,
                workbookSchemaVersion = ProductIdentity.WorkbookSchemaVersion,
                locatorProfileVersion = ProductIdentity.LocatorProfileVersion,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                inputSent = false, toolCount = AdvertisedToolNames.Length, compatibilityToolCount = ToolActions.Count, advancedMode
            }, ContractJson.Compact));
            return 0;
        }
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var reader = new StreamReader(input, Encoding.UTF8);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
        var pending = new List<Task>();
        using var outputGate = new SemaphoreSlim(1, 1);
        while (true)
        {
            string? line;
            try { line = await MisaManager.Control.ControlServer.ReadLineAsync(reader, CancellationToken.None); }
            catch (InvalidDataException error)
            {
                await outputGate.WaitAsync();
                try { await writer.WriteLineAsync(Error("null", -32600, error.Message)); }
                finally { outputGate.Release(); }
                break;
            }
            if (line is null) break;
            pending.RemoveAll(task => task.IsCompleted);
            pending.Add(RespondAsync(line));
        }
        await Task.WhenAll(pending);
        return 0;

        async Task RespondAsync(string line)
        {
            try
            {
                var response = await HandleLineAsync(line);
                if (response is null) return;
                await outputGate.WaitAsync();
                try { await writer.WriteLineAsync(response); }
                finally { outputGate.Release(); }
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); }
        }
    }

    private static async Task<string?> HandleLineAsync(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error("null", -32600, "Request must be an object.");
            return root.TryGetProperty("jsonrpc", out _) || root.TryGetProperty("method", out _)
                ? await HandleJsonRpcAsync(root) : await ForwardTypedAsync(body);
        }
        catch (JsonException error)
        {
            return Error("null", -32700, error.Message);
        }
        catch (Exception error) { return Error("null", -32603, error.Message); }
    }

    private static async Task<string?> HandleJsonRpcAsync(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idValue) ? idValue.GetRawText() : "null";
        if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"
            || !root.TryGetProperty("method", out var methodValue) || methodValue.ValueKind != JsonValueKind.String
            || (root.TryGetProperty("id", out _) && idValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)))
            return Error("null", -32600, "Expected JSON-RPC 2.0, a method and a string/number request ID.");
        var method = methodValue.GetString();
        if (!root.TryGetProperty("id", out _))
        {
            if (method == "notifications/cancelled" && root.TryGetProperty("params", out var cancelParams)
                && cancelParams.ValueKind == JsonValueKind.Object && cancelParams.TryGetProperty("requestId", out var cancelId)
                && Requests.TryGetValue(cancelId.GetRawText(), out var cancelled))
            {
                try { cancelled.Cancel(); }
                catch (ObjectDisposedException) { /* The request completed before cancellation arrived. */ }
            }
            return null;
        }
        if (root.TryGetProperty("params", out var suppliedParams) && suppliedParams.ValueKind != JsonValueKind.Object)
            return Error(id, -32602, "params must be an object.");
        if (method == "ping") return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = JsonId(id), result = new { } }, ContractJson.Compact);
        if (method == "initialize")
        {
            if (!root.TryGetProperty("params", out var init) || !init.TryGetProperty("protocolVersion", out var protocol)
                || protocol.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(protocol.GetString()))
                return Error(id, -32602, "initialize requires protocolVersion.");
            var negotiated = protocol.GetString()!;
            if (negotiated is not ("2024-11-05" or "2025-03-26" or "2025-06-18")) negotiated = "2024-11-05";
            supportsStructuredContent = negotiated != "2024-11-05";
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = JsonId(id), result = new { protocolVersion = negotiated, capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = ProductIdentity.TechnicalName, version = typeof(Program).Assembly.GetName().Version?.ToString() }, instructions = ServerInstructions } }, ContractJson.Compact);
        }
        if (method == "tools/list")
        {
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = JsonId(id), result = new { tools = ToolCatalog.Value } }, ContractJson.Compact);
        }
        if (method == "tools/call")
        {
            if (!root.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("name", out var nameValue)
                || nameValue.ValueKind != JsonValueKind.String)
                return Error(id, -32602, "Unknown or missing tool name.");
            var arguments = parameters.TryGetProperty("arguments", out var args) ? args.Clone() : EmptyPayload();
            var toolName = nameValue.GetString() ?? "";
            if (ToolGroups.TryGetValue(toolName, out var group))
            {
                if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("action", out var selector)
                    || selector.ValueKind != JsonValueKind.String)
                    return Error(id, -32602, "Grouped tools require an action from their schema.");
                var selected = group.SingleOrDefault(name => ToolActions[name] == selector.GetString());
                if (selected is null) return Error(id, -32602, "Unknown action for this tool group.");
                var payload = JsonNode.Parse(arguments.GetRawText())!.AsObject();
                payload.Remove("action");
                var allowed = BuildToolSchema(selected)["properties"]!.AsObject();
                if (payload.Any(pair => !allowed.ContainsKey(pair.Key)))
                    return Error(id, -32602, "Arguments contain fields not supported by the selected action.");
                arguments = JsonSerializer.SerializeToElement(payload, ContractJson.Compact);
                toolName = selected;
            }
            if (!ToolActions.TryGetValue(toolName, out var action))
                return Error(id, -32602, "Unknown or missing tool name.");
            if (!advancedMode && ControlActions.MayMutate(action))
                return Error(id, -32602, "This tool can drive MISA and is disabled in observer mode. Start the bridge with --advanced (or MISA_MCP_ADVANCED=1) to enable it.");
            if (arguments.ValueKind != JsonValueKind.Object
                || (arguments.TryGetProperty("idempotencyKey", out var suppliedKey) && suppliedKey.ValueKind != JsonValueKind.String)
                || (arguments.TryGetProperty("expectedRevision", out var suppliedRevision) && suppliedRevision.ValueKind != JsonValueKind.String))
                return Error(id, -32602, "arguments must be an object; idempotencyKey and expectedRevision must be strings.");
            var requestId = BridgeSessionId + ":" + id;
            var key = arguments.TryGetProperty("idempotencyKey", out var keyValue) ? keyValue.GetString() : null;
            var revision = arguments.TryGetProperty("expectedRevision", out var revisionValue) ? revisionValue.GetString() : null;
            if (ControlActions.MayMutate(action) && string.IsNullOrWhiteSpace(key))
                return Error(id, -32602, "Mutation requires a stable idempotencyKey. Reuse the key and identical arguments only to retrieve a lost response; never replay uncertain input.");
            var request = new ControlRequest(ProductIdentity.ControlProtocolVersion, requestId, key ?? requestId, BridgeSessionId,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Add(ControlActions.RequestTimeout(action)), action, revision, arguments);
            using var cancellation = new CancellationTokenSource();
            var remaining = request.Deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return Error(id, -32602, "Request deadline expired before dispatch.");
            cancellation.CancelAfter(remaining);
            if (Requests.Count >= 256) return Error(id, -32603, "Too many pending requests.");
            if (!Requests.TryAdd(id, cancellation)) return Error(id, -32600, "Request ID is already running.");
            OperationResult response;
            var entered = false;
            try
            {
                await Capacity.WaitAsync(cancellation.Token);
                entered = true;
                response = await ForwardRequestAsync(request, cancellation.Token);
            }
            catch (OperationCanceledException) { response = new(requestId, "cancelled", "Request cancelled before dispatch."); }
            catch (InvalidDataException error) { return Error(id, -32602, error.Message); }
            catch (Exception error) { return Error(id, -32603, error.Message); }
            finally
            {
                Requests.TryRemove(id, out _);
                if (entered) Capacity.Release();
            }
            if (ControlActions.IsAgentAction(action)) return AgentResult(id, response);
            var result = new Dictionary<string, object>
            {
                ["content"] = new[] { new { type = "text", text = JsonSerializer.Serialize(response, ContractJson.Compact) } },
                ["isError"] = response.Status is "error" or "blocked" or "expired" or "cancelled" or "uncertain"
            };
            if (supportsStructuredContent) result["structuredContent"] = response;
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = JsonId(id), result }, ContractJson.Compact);
        }
        return Error(id, -32601, "Method not found.");
    }

    // Flat, null-free text keeps agent responses small; status/message lead.
    private static string AgentResult(string id, OperationResult response)
    {
        var body = new JsonObject { ["status"] = response.Status, ["message"] = response.Message };
        if (runtimeMismatch is not null) body["warning"] = runtimeMismatch;
        if (response.Data is not null && JsonSerializer.SerializeToNode(response.Data, ContractJson.Compact) is { } data)
        {
            if (data is JsonObject fields)
                foreach (var pair in fields.ToArray()) { fields.Remove(pair.Key); body[pair.Key] = pair.Value; }
            else body["data"] = data;
        }
        Prune(body);
        var result = new
        {
            content = new[] { new { type = "text", text = body.ToJsonString(AgentJson) } },
            isError = response.Status is "error" or "expired" or "cancelled" or "uncertain"
        };
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = JsonId(id), result }, ContractJson.Compact);
    }

    // Vietnamese text stays readable (and cheaper in tokens) instead of \uXXXX escapes.
    private static readonly JsonSerializerOptions AgentJson = new(ContractJson.Compact)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Returns true when the node is empty after removing nulls and empty containers.
    private static bool Prune(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
                if (pair.Value is null || Prune(pair.Value)) obj.Remove(pair.Key);
            return obj.Count == 0;
        }
        if (node is JsonArray array)
        {
            foreach (var item in array) Prune(item);
            return array.Count == 0;
        }
        return false;
    }

    private static async Task<string> ForwardTypedAsync(string body)
    {
        if (!await Capacity.WaitAsync(0))
            return JsonSerializer.Serialize(new { status = "blocked", code = "CAPACITY_EXCEEDED" }, ContractJson.Compact);
        try
        {
            var request = JsonSerializer.Deserialize<ControlRequest>(body, ContractJson.Options) ?? throw new JsonException("Empty request.");
            if (!advancedMode && ControlActions.MayMutate(request.Action))
                return JsonSerializer.Serialize(new { status = "blocked", code = "OBSERVER_MODE", message = "Mutations require --advanced." }, ContractJson.Compact);
            return JsonSerializer.Serialize(await ForwardRequestAsync(request), ContractJson.Compact);
        }
        catch (Exception error) { return JsonSerializer.Serialize(new { status = "error", code = "BRIDGE_ERROR", message = error.Message }, ContractJson.Compact); }
        finally { Capacity.Release(); }
    }

    private static async Task<OperationResult> ForwardRequestAsync(ControlRequest request, CancellationToken token = default)
    {
        ContractValidation.Validate(request);
        token.ThrowIfCancellationRequested();
        if (request.Action == "capabilities" && string.IsNullOrWhiteSpace(request.SessionNonce))
            return await SendRequestAsync(request, token);

        var sessionResult = await EnsureSessionAsync(request, token);
        if (sessionResult is not null)
            return sessionResult with { RequestId = request.RequestId };

        if (runtimeMismatch is not null && ControlActions.MayMutate(request.Action))
            return new(request.RequestId, "error", runtimeMismatch + " Mutations are blocked; reads still work.");
        var response = await SendRequestAsync(request with { SessionNonce = sessionNonce }, token);
        if (response.Status == "error" && response.Message.Contains("nonce", StringComparison.OrdinalIgnoreCase))
        {
            sessionNonce = null;
            var retryResult = await EnsureSessionAsync(request, token);
            if (retryResult is not null)
                return retryResult with { RequestId = request.RequestId };
            return await SendRequestAsync(request with { SessionNonce = sessionNonce }, token);
        }
        return response;
    }

    private static async Task<OperationResult?> EnsureSessionAsync(ControlRequest request, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(sessionNonce)) return null;
        await SessionGate.WaitAsync(token);
        try
        {
            if (!string.IsNullOrWhiteSpace(sessionNonce)) return null;
            var now = DateTimeOffset.UtcNow;
            var deadline = request.Deadline < now.AddSeconds(30) ? request.Deadline : now.AddSeconds(30);
            if (deadline <= now)
                return new(request.RequestId, "expired", "Control handshake deadline expired.");
            var handshake = new ControlRequest(
                ProductIdentity.ControlProtocolVersion,
                "capabilities-" + Guid.NewGuid().ToString("N"),
                "mcp-capabilities-" + Guid.NewGuid().ToString("N"),
                string.IsNullOrWhiteSpace(request.SessionId) ? BridgeSessionId : request.SessionId,
                now,
                deadline,
                "capabilities",
                null,
                EmptyPayload());
            var result = await SendRequestAsync(handshake, token);
            if (result.Status is "error" or "blocked" or "expired" or "cancelled" or "uncertain")
                return new(request.RequestId, result.Status, "Control handshake failed: " + result.Message, result.InputMayHaveBeenSent, result.Data);
            if (!TryReadSessionNonce(result.Data, out var nonce))
                return new(request.RequestId, "error", "Control handshake did not return a session nonce.");
            sessionNonce = nonce;
            return null;
        }
        finally { SessionGate.Release(); }
    }

    private static async Task<OperationResult> SendRequestAsync(ControlRequest request, CancellationToken token = default)
    {
        var sent = false;
        try
        {
            var remaining = request.Deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return new(request.RequestId, "expired", "Control deadline expired before send.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(remaining);
            using var pipe = new NamedPipeClientStream(".", ProductIdentity.TechnicalName + ".control", PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(Math.Clamp((int)remaining.TotalMilliseconds, 1, 30_000), deadline.Token);
            using var pipeReader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            await using var pipeWriter = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            sent = true;
            await pipeWriter.WriteLineAsync(JsonSerializer.Serialize(request, ContractJson.Compact).AsMemory(), deadline.Token);
            var result = JsonSerializer.Deserialize<OperationResult>(await MisaManager.Control.ControlServer.ReadLineAsync(pipeReader, deadline.Token)
                ?? throw new IOException("Control app closed the connection."), ContractJson.Options) ?? throw new JsonException("Control response is empty.");
            if (request.Action == "capabilities" && result.Status == "completed")
            {
                // A reloaded app is a different build than this long-lived bridge;
                // keep the session for reads and block only mutations.
                runtimeMismatch = MatchesRuntime(result.Data) ? null
                    : "BRIDGE_APP_IDENTITY_MISMATCH: app and bridge are different builds (version/source/Core SHA/protocol/schema). Restart Codex's MCP bridge from the same release.";
                if (TryReadSessionNonce(result.Data, out var nonce)) sessionNonce = nonce;
                if (runtimeMismatch is not null && request.SessionNonce is null && !request.RequestId.StartsWith("capabilities-", StringComparison.Ordinal))
                    return result with { Status = "error", Message = runtimeMismatch + " No action was dispatched." };
            }
            return result;
        }
        catch (Exception error) when (!sent && (error is TimeoutException or IOException))
        {
            return new(request.RequestId, "blocked", "MisaManager.App is not running or the named pipe is not ready.");
        }
        catch (Exception error)
        {
            if (sent && token.IsCancellationRequested && request.Action != "cancel_request" && request.SessionNonce is not null)
            {
                var now = DateTimeOffset.UtcNow;
                var cancelId = "cancel-" + Guid.NewGuid().ToString("N");
                var cancel = new ControlRequest(ProductIdentity.ControlProtocolVersion, cancelId, cancelId, request.SessionId,
                    now, now.AddSeconds(5), "cancel_request", null,
                    JsonSerializer.SerializeToElement(new { requestId = request.RequestId }), request.SessionNonce);
                await SendRequestAsync(cancel);
            }
            var uncertain = sent && MisaManager.Control.ControlServer.MayMutate(request.Action);
            return new(request.RequestId, uncertain ? "uncertain" : error is OperationCanceledException ? "cancelled" : "error",
                error.Message, sent && ControlActions.MaySendInput(request.Action));
        }
    }

    private static bool TryReadSessionNonce(object? data, out string nonce)
    {
        nonce = "";
        if (data is JsonElement element && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("sessionNonce", out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            nonce = value.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(nonce);
        }
        if (data is CapabilitySnapshot snapshot && !string.IsNullOrWhiteSpace(snapshot.SessionNonce))
        {
            nonce = snapshot.SessionNonce;
            return true;
        }
        return false;
    }

    private static bool MatchesRuntime(object? data)
    {
        var capability = data is CapabilitySnapshot snapshot ? snapshot
            : data is JsonElement element && element.ValueKind == JsonValueKind.Object
                ? element.Deserialize<CapabilitySnapshot>(ContractJson.Options) : null;
        return capability is not null && capability.AppVersion == ProductIdentity.RuntimeVersion
            && capability.SourceRevision == ProductIdentity.SourceRevision
            && capability.CoreAssemblySha256 == ProductIdentity.RuntimeAssemblySha256
            && capability.ControlProtocolVersion == ProductIdentity.ControlProtocolVersion
            && capability.StateSchemaVersion == ProductIdentity.StateSchemaVersion
            && capability.LocatorProfileVersion == ProductIdentity.LocatorProfileVersion;
    }

    private static string DescribeTool(string name)
    {
        if (ToolGroups.ContainsKey(name)) return name switch
        {
            "misa_read" => "Read-only native/state queries. Start with action=live_test_state for a current workflow (includes fresh observation, checkpoint, revision and runtime), or diagnostics to discover windows (includes native observations). Handshake is automatic. Use failure_context for a failure packet; runtime_status for runtime only. Do not repeat reads already answered by a fresh response; saved workflow_status is not current UI.",
            "misa_control" => "Direct MISA control: choose action and its target fields. Requires a stable idempotencyKey. Responses include readback or its unavailability; do not add an observe call when the returned readback already answers the question. Uncertain input must not be replayed.",
            "misa_workflow" => "Workflow commands. Prefer live_run for a normal invoice: it enters, reconciles, and saves/discards in one end-to-end operation. The checkpoint APIs remain for guarded recovery only. Requires a stable idempotencyKey; reuse identical arguments only to retrieve a lost response.",
            "misa_restart" => "Guarded candidate restart: reload, acknowledge_restart, or rebind_after_restart. Preserve the same bridge session and incident. For a live-run incident set restartFromBeginning=true to rerun the same invoice from Ready; never resume its failed checkpoint.",
            _ => throw new InvalidOperationException("Unknown tool group.")
        };
        var action = ControlActions.Canonical(ToolActions[name]);
        var canonicalName = ToolActions.First(pair => pair.Value == action).Key;
        var description = ToolDescriptions.GetValueOrDefault(name)
            ?? ToolDescriptions.GetValueOrDefault(canonicalName)
            ?? $"Execute {action} through the shared app dispatcher using the declared payload.";
        return ControlActions.MayMutate(action)
            ? action == "live_run"
                ? description + " Requires a stable idempotencyKey; reuse identical arguments only for a lost response. A failed attempt must be rebuilt and submitted from Ready."
                : description + " Requires a stable idempotencyKey; reuse identical arguments only for a lost response. Uncertain input needs fresh observation/recovery."
            : description;
    }

    private static JsonObject InvoiceSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("invoiceId", "revision", "fingerprint", "identity", "source", "buyer", "template", "currency", "paymentMethod", "lines"),
        ["properties"] = new JsonObject
        {
            ["invoiceId"] = new JsonObject { ["type"] = "string" },
            ["revision"] = new JsonObject { ["type"] = "integer" },
            ["fingerprint"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
            ["identity"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false },
            ["source"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false },
            ["seller"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false },
            ["buyer"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false },
            ["template"] = new JsonObject { ["type"] = "string" },
            ["currency"] = new JsonObject { ["type"] = "string" },
            ["paymentMethod"] = new JsonObject { ["type"] = "string" },
            ["lines"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false } }
        },
        ["additionalProperties"] = false
    };

    private static JsonObject CorrectionSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("invoiceId", "expectedRevision", "field", "value", "confirmation"),
        ["properties"] = new JsonObject
        {
            ["invoiceId"] = new JsonObject { ["type"] = "string" },
            ["expectedRevision"] = new JsonObject { ["type"] = "integer" },
            ["field"] = new JsonObject { ["type"] = "string" },
            ["value"] = new JsonObject { ["type"] = "string" },
            ["provenance"] = new JsonObject { ["const"] = "user_override" },
            ["confirmation"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 }
        },
        ["additionalProperties"] = false
    };

    private static JsonNode BuildToolSchema(string name)
    {
        if (ToolGroups.TryGetValue(name, out var group)) return BuildGroupSchema(group);
        var schema = JsonSerializer.SerializeToNode(ToolSchemas[name], ContractJson.Compact)!.AsObject();
        var properties = schema["properties"]?.AsObject() ?? new JsonObject();
        if (schema["properties"] is null) schema["properties"] = properties;
        if (properties.ContainsKey("invoice")) properties["invoice"] = InvoiceSchema();
        if (properties.ContainsKey("correction")) properties["correction"] = CorrectionSchema();
        var action = ControlActions.Canonical(ToolActions[name]);
        if (action is "recover" or "rebind_after_restart")
        {
            var allowed = action == "recover" ? new[] { WorkflowStatus.AwaitingSave, WorkflowStatus.Saved, WorkflowStatus.Discarded }
                : action == "rebind_after_restart"
                    ? Enum.GetValues<WorkflowStatus>().Where(value => value <= WorkflowStatus.AwaitingSave
                        || value is WorkflowStatus.Saved or WorkflowStatus.Failed).ToArray()
                    : Enum.GetValues<WorkflowStatus>().Where(value => value <= WorkflowStatus.AwaitingSave || value == WorkflowStatus.Saved).ToArray();
            properties["expectedStatus"]!["enum"] = new JsonArray(allowed.Select(value =>
                JsonSerializer.SerializeToNode(value, ContractJson.Compact)).ToArray());
        }
        if (ControlActions.IsDirect(action) && action != "selectTab")
            schema["anyOf"] = JsonSerializer.SerializeToNode(new[] {
                new { required = new[] { "automationId" } }, new { required = new[] { "name" } } });
        if (ControlActions.MayMutate(action))
        {
            properties["idempotencyKey"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 };
            properties["operationId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 };
            var required = schema["required"]?.AsArray() ?? new JsonArray();
            if (schema["required"] is null) schema["required"] = required;
            if (!required.Any(value => value?.GetValue<string>() == "idempotencyKey")) required.Add("idempotencyKey");
        }
        return schema;
    }

    private static JsonObject BuildGroupSchema(string[] group)
    {
        var properties = new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string",
                ["enum"] = new JsonArray(group.Select(name => (JsonNode?)JsonValue.Create(ToolActions[name])).ToArray()) }
        };
        var variants = new JsonArray();
        foreach (var name in group)
        {
            var schema = BuildToolSchema(name).AsObject();
            foreach (var property in schema["properties"]!.AsObject())
                properties[property.Key] = property.Value?.DeepClone();
            var required = schema["required"]?.DeepClone().AsArray() ?? new JsonArray();
            required.Add("action");
            var variant = new JsonObject
            {
                ["required"] = required,
                ["properties"] = new JsonObject { ["action"] = new JsonObject { ["const"] = ToolActions[name] } }
            };
            if (schema["anyOf"] is { } alternatives) variant["anyOf"] = alternatives.DeepClone();
            variants.Add(variant);
        }
        return new JsonObject { ["type"] = "object", ["required"] = new JsonArray("action"),
            ["properties"] = properties, ["oneOf"] = variants, ["additionalProperties"] = false };
    }

    private static JsonElement EmptyPayload() => JsonSerializer.SerializeToElement(new { });
    private static JsonElement JsonId(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
    private static string Error(string id, int code, string message) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = JsonId(id), error = new { code, message } }, ContractJson.Compact);
}
