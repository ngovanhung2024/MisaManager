using System.Globalization;
using System.Text.Json;
using MisaManager.Automation;
using MisaManager.Core;

namespace MisaManager.App;

public sealed record AgentUiError(string Message, string Details, DateTimeOffset OccurredAt);

// Read-only views for the observing MCP agent. While the tool is driving MISA
// these use in-memory state only; native UIA reads wait until it is idle.
public sealed partial class AppServices
{
    private readonly object agentGate = new();
    private bool uiBatchActive;
    private long uiBatchSequence;
    private DateTimeOffset? uiBatchStartedAt;
    private DateTimeOffset? uiBatchEndedAt;
    private string? uiBatchOutcome;
    private string? uiBatchInvoice;
    private AgentUiError? lastUiError;
    private LiveRunResult? lastLiveRun;
    private UiFailureSnapshot? failureUi;
    private string? failureUiRunId;
    private string? failureUiStep;

    public void BeginUiBatch(string invoiceNumber)
    {
        lock (agentGate)
        {
            uiBatchActive = true;
            uiBatchSequence++;
            uiBatchStartedAt = DateTimeOffset.UtcNow;
            uiBatchEndedAt = null;
            uiBatchOutcome = null;
            uiBatchInvoice = invoiceNumber;
            lastUiError = null;
        }
    }

    public void EndUiBatch(string outcome)
    {
        lock (agentGate)
        {
            uiBatchActive = false;
            uiBatchEndedAt = DateTimeOffset.UtcNow;
            uiBatchOutcome = outcome;
        }
    }

    public void RecordUiError(string message, string details)
    {
        lock (agentGate) lastUiError = new(message, Clip(details, 20000)!, DateTimeOffset.UtcNow);
    }

    private void RecordLiveRun(LiveRunResult result)
    {
        lock (agentGate) lastLiveRun = result;
    }

    private bool AutomationBusy
    {
        get
        {
            lock (agentGate) if (uiBatchActive) return true;
            return operations.Values.Any(operation => operation.Status == "running" && ControlActions.MayMutate(operation.Action));
        }
    }

    private async Task CaptureFailureUiAsync(WorkflowContext context, string step)
    {
        if (!TryReadHandle(context.Snapshot.WindowIdentity ?? context.WindowIdentity, out var handle)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        UiFailureSnapshot snapshot;
        try { snapshot = await Native.InspectFailureAsync(handle, FailureTargets(step), timeout.Token); }
        catch (Exception error) { snapshot = new(handle, "", [], [], [], DateTimeOffset.UtcNow, error.Message); }
        lock (agentGate) { failureUi = snapshot; failureUiRunId = context.Snapshot.RunId; failureUiStep = step; }
    }

    private string[] FailureTargets(string? step)
        => step is not null && WorkflowProfile.Fields.TryGetValue(step, out var selector) && selector.AutomationId.Length > 0
            ? [selector.AutomationId] : [];

    private static bool TryReadHandle(string? serialized, out nint handle)
    {
        handle = 0;
        var part = serialized?.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2)).FirstOrDefault(item => item.Length == 2 && item[0] == "hwnd");
        if (part is null || !long.TryParse(part[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value == 0)
            return false;
        handle = (nint)value;
        return true;
    }

    private async Task<OperationResult> DispatchAgentAsync(ControlRequest request, CancellationToken token)
    {
        try
        {
            return request.Action switch
            {
                "wait_run" => await WaitRunAsync(request, token),
                "failure_report" => await FailureReportAsync(request, token),
                "ui_find" => await UiFindAsync(request, token),
                "perf_report" => await PerfReportAsync(request, token),
                "compare_report" => await CompareReportAsync(request, token),
                _ => new(request.RequestId, "error", "Unknown agent action.")
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException error) { return new(request.RequestId, "error", error.Message); }
        catch (AutomationException error) { return new(request.RequestId, "blocked", error.Code + ": " + error.Message); }
    }

    private async Task<OperationResult> WaitRunAsync(ControlRequest request, CancellationToken token)
    {
        var timeout = ReadInt(request.Payload, "timeoutSeconds", 900, 1, ControlActions.MaxWaitSeconds);
        var waitForStart = ReadBool(request.Payload, "waitForStart", true);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
        var started = AutomationBusy;
        if (!started && !waitForStart)
            return new(request.RequestId, "completed", "Không có lần chạy nào đang diễn ra.", Data: await RunSummaryAsync("idle", token));
        while (true)
        {
            var busy = AutomationBusy;
            if (busy) started = true;
            else if (started)
            {
                // Bridge the short gap between queued invoices before reporting.
                await Task.Delay(500, token);
                if (!AutomationBusy)
                    return new(request.RequestId, "completed", "Lần chạy đã kết thúc.", Data: await RunSummaryAsync("finished", token));
                continue;
            }
            if (DateTimeOffset.UtcNow >= deadline)
                return new(request.RequestId, "accepted",
                    started ? "Hết thời gian chờ; tool vẫn đang chạy. Gọi lại misa_wait để chờ tiếp." : "Hết thời gian chờ; chưa có lần chạy nào bắt đầu.",
                    Data: await RunSummaryAsync(started ? "running" : "waiting_for_start", token));
            await Task.Delay(busy ? 750 : 400, token);
        }
    }

    private async Task<object> RunSummaryAsync(string state, CancellationToken token)
    {
        var current = Workflow.Current;
        var snapshot = current?.Snapshot;
        var steps = StepsFor(snapshot?.RunId);
        var live = LiveFor(snapshot?.RunId);
        var verification = live?.Verification ?? (snapshot is null ? null : await LatestVerificationAsync(snapshot.RunId, token));
        var failed = snapshot?.Status is WorkflowStatus.Failed or WorkflowStatus.Blocked or WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain
            || live?.Outcome == "failed";
        AgentUiError? uiError;
        lock (agentGate) uiError = lastUiError;
        return new
        {
            state,
            batch = BatchSummary(),
            activity = Activity is { } activity ? new { activity.Field, runningMs = Milliseconds(DateTimeOffset.UtcNow - activity.StartedAt) } : null,
            run = RunHeader(current),
            outcome = live?.Outcome,
            failedStep = FailedStep(steps),
            error = failed ? Clip(FirstLine(live?.Error ?? snapshot?.ErrorChain ?? snapshot?.LastMessage), 1500) : null,
            comparison = CompactComparison(verification, includeMatched: false, limit: 10),
            timing = new
            {
                checkpointsMs = steps.Where(step => step.Kind == "checkpoint").Sum(step => step.DurationMilliseconds),
                slowest = steps.Where(step => step.Kind == "step").OrderByDescending(step => step.DurationMilliseconds).Take(5)
                    .Select(step => new { step.Step, ms = step.DurationMilliseconds, step.Outcome }).ToArray()
            },
            uiError = uiError is null ? null : new { uiError.Message, uiError.OccurredAt },
            next = failed || uiError is not null ? "Gọi misa_failure để xem lỗi đầy đủ, dialog MISA và element tại thời điểm lỗi." : null
        };
    }

    private async Task<OperationResult> FailureReportAsync(ControlRequest request, CancellationToken token)
    {
        var readUi = ReadBool(request.Payload, "ui", true);
        var force = ReadBool(request.Payload, "force", false);
        var current = Workflow.Current;
        var snapshot = current?.Snapshot;
        var steps = StepsFor(snapshot?.RunId);
        var live = LiveFor(snapshot?.RunId);
        var verification = live?.Verification ?? (snapshot is null ? null : await LatestVerificationAsync(snapshot.RunId, token));
        var failedStep = FailedStep(steps);
        AgentUiError? uiError;
        UiFailureSnapshot? atFailure = null;
        string? atFailureStep = null;
        lock (agentGate)
        {
            uiError = lastUiError;
            if (snapshot is not null && failureUiRunId == snapshot.RunId) { atFailure = failureUi; atFailureStep = failureUiStep; }
        }
        UiFailureSnapshot? freshUi = null;
        string? freshUiSkipped = null;
        if (!readUi) freshUiSkipped = "ui=false";
        else if (AutomationBusy && !force) freshUiSkipped = "Tool đang chạy; bỏ qua đọc UI để không làm chậm (force=true để ép).";
        else if (!TryReadHandle(snapshot?.WindowIdentity ?? current?.WindowIdentity, out var handle))
            freshUiSkipped = "Chưa có cửa sổ MISA gắn với workflow; dùng misa_ui.";
        else
        {
            try { freshUi = await Native.InspectFailureAsync(handle, FailureTargets(failedStep?.Step ?? atFailureStep), token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { freshUiSkipped = "Không đọc được UI hiện tại: " + error.Message; }
        }
        var lineIndex = snapshot?.ActiveLine;
        var activeLine = lineIndex is >= 0 && current is not null && lineIndex < current.Invoice.Lines.Length
            ? current.Invoice.Lines[lineIndex.Value] : null;
        return new(request.RequestId, "completed", snapshot is null ? "Chưa có workflow nào." : "Failure report.", Data: new
        {
            run = RunHeader(current),
            outcome = live?.Outcome,
            failedStep,
            errorCode = snapshot?.LastErrorCode ?? verification?.ErrorCode,
            message = Clip(snapshot?.LastMessage ?? verification?.Error, 2000),
            errorChain = Clip(live?.Error is { } liveError && liveError != snapshot?.ErrorChain
                ? liveError + Environment.NewLine + snapshot?.ErrorChain : snapshot?.ErrorChain ?? verification?.ErrorChain, 8000),
            inputMayHaveBeenSent = snapshot?.InputMayHaveBeenSent,
            lastSteps = steps.Where(step => step.Kind == "step").TakeLast(8)
                .Select(step => new { step.Step, ms = step.DurationMilliseconds, step.Outcome }).ToArray(),
            expected = current is null ? null : new
            {
                number = current.Invoice.Identity.Number, series = current.Invoice.Identity.Series,
                buyer = current.Invoice.Buyer.Name, buyerTaxCode = current.Invoice.Buyer.TaxCode,
                current.Invoice.VoucherDate, current.Invoice.PostedDate,
                current.Invoice.Subtotal, current.Invoice.TaxTotal, current.Invoice.GrandTotal,
                lineCount = current.Invoice.Lines.Length, activeLineIndex = lineIndex, activeLine
            },
            comparison = CompactComparison(verification, includeMatched: false, limit: 50),
            uiAtFailure = atFailure,
            uiAtFailureStep = atFailureStep,
            uiNow = freshUi,
            uiNowSkipped = freshUiSkipped,
            uiError = uiError is null ? null : new { uiError.Message, details = Clip(uiError.Details, 6000), uiError.OccurredAt },
            sourceHints = SourceHints(failedStep?.Step, snapshot?.LastErrorCode)
        });
    }

    private async Task<OperationResult> UiFindAsync(ControlRequest request, CancellationToken token)
    {
        var text = ReadText(request.Payload, "text");
        var scope = ReadText(request.Payload, "scopeAutomationId");
        var controlType = ReadText(request.Payload, "controlType");
        var maxResults = ReadInt(request.Payload, "maxResults", 40, 1, 200);
        var force = ReadBool(request.Payload, "force", false);
        if (AutomationBusy && !force)
            return new(request.RequestId, "blocked", "Tool đang điều khiển MISA; đọc UI lúc này làm chậm tool. Dùng misa_wait trước, hoặc force=true.");
        nint handle;
        if (request.Payload.TryGetProperty("windowHandle", out var handleValue))
        {
            if (!handleValue.TryGetInt64(out var value) || value == 0) throw new ArgumentException("windowHandle phải là số nguyên khác 0.");
            handle = (nint)value;
        }
        else
        {
            var candidates = await Native.FindCandidatesAsync(token);
            var bound = TryReadHandle(Workflow.Current?.Snapshot.WindowIdentity, out var workflowHandle)
                && candidates.Any(candidate => candidate.WindowHandle == workflowHandle) ? workflowHandle : (nint?)null;
            if (bound is { } selected) handle = selected;
            else if (candidates.Count == 1) handle = candidates[0].WindowHandle;
            else
                return new(request.RequestId, "blocked", candidates.Count == 0 ? "Không thấy cửa sổ MISA nào." : "Có nhiều cửa sổ MISA; truyền windowHandle.",
                    Data: new { candidates = candidates.Select(candidate => new { windowHandle = candidate.WindowHandle, candidate.ProcessId, candidate.Title }).ToArray() });
        }
        var result = await Native.FindAsync(handle, text, scope, controlType, maxResults, scope is null ? 8000 : 20000, token);
        return new(request.RequestId, "completed", $"{result.Elements.Count} element (đã duyệt {result.Visited}).", Data: result);
    }

    private async Task<OperationResult> PerfReportAsync(ControlRequest request, CancellationToken token)
    {
        var detail = ReadBool(request.Payload, "detail", false);
        var history = ReadInt(request.Payload, "history", 10, 0, 50);
        var current = Workflow.Current;
        var (timelineRun, all) = workflowGateway.Timeline();
        var steps = all.Where(step => step.Kind == "step").ToArray();
        var origin = all.Count == 0 ? DateTimeOffset.UtcNow : all.Min(step => step.StartedAt);
        var records = history == 0 ? [] : (await Workflow.ReadLiveVerificationHistoryAsync(token)).Take(history).ToArray();
        return new(request.RequestId, "completed", "Performance report.", Data: new
        {
            runId = timelineRun,
            invoiceNumber = current?.Snapshot.RunId == timelineRun ? current?.Invoice.Identity.Number : null,
            batch = BatchSummary(),
            totalStepMs = steps.Sum(step => step.DurationMilliseconds),
            checkpoints = all.Where(step => step.Kind == "checkpoint")
                .Select(step => new { step.Step, ms = step.DurationMilliseconds, step.Outcome }).ToArray(),
            byStepType = steps.GroupBy(step => StepGroup(step.Step))
                .Select(group => new { step = group.Key, count = group.Count(), totalMs = group.Sum(step => step.DurationMilliseconds),
                    avgMs = (long)group.Average(step => step.DurationMilliseconds), maxMs = group.Max(step => step.DurationMilliseconds) })
                .OrderByDescending(group => group.totalMs).ToArray(),
            slowest = steps.OrderByDescending(step => step.DurationMilliseconds).Take(15)
                .Select(step => new { step.Step, ms = step.DurationMilliseconds, atMs = Milliseconds(step.StartedAt - origin), step.Outcome }).ToArray(),
            steps = detail ? steps.Take(400).Select(step => new { step.Step, ms = step.DurationMilliseconds, atMs = Milliseconds(step.StartedAt - origin), step.Outcome }).ToArray() : null,
            nativeQueue = nativeOwner.Metrics,
            history = records.Select(record => new { record.RunId, record.InvoiceId, record.Outcome, record.Matched,
                durationMs = Milliseconds(record.CompletedAt - record.StartedAt), record.CompletedAt }).ToArray()
        });
    }

    private async Task<OperationResult> CompareReportAsync(ControlRequest request, CancellationToken token)
    {
        var runId = ReadText(request.Payload, "runId");
        var invoiceNumber = ReadText(request.Payload, "invoiceNumber");
        var includeMatched = ReadBool(request.Payload, "includeMatched", false);
        var records = await Workflow.ReadLiveVerificationHistoryAsync(token);
        LiveVerificationRecord? record;
        if (runId is not null) record = records.FirstOrDefault(item => item.RunId == runId);
        else if (invoiceNumber is not null)
        {
            var runs = (await Workflow.ReadHistoryAsync(token))
                .Where(item => string.Equals(item.Invoice.Identity.Number, invoiceNumber, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Snapshot.RunId).ToHashSet(StringComparer.Ordinal);
            if (Workflow.Current is { } active && string.Equals(active.Invoice.Identity.Number, invoiceNumber, StringComparison.OrdinalIgnoreCase))
                runs.Add(active.Snapshot.RunId);
            record = records.FirstOrDefault(item => runs.Contains(item.RunId));
        }
        else record = records.FirstOrDefault();
        if (record is null) return new(request.RequestId, "completed", "Chưa có kết quả đối chiếu phù hợp.");
        return new(request.RequestId, "completed", record.Matched ? "Đối chiếu khớp." : "Đối chiếu có sai lệch hoặc chưa đối chiếu.", Data: new
        {
            record.RunId, record.InvoiceId, record.InvoiceRevision, record.Outcome, record.Reconciled, record.Matched,
            durationMs = Milliseconds(record.CompletedAt - record.StartedAt), record.CompletedAt,
            record.ErrorCode, error = Clip(record.Error, 1500),
            comparison = CompactComparison(record, includeMatched, limit: includeMatched ? 300 : 100)
        });
    }

    private object BatchSummary()
    {
        lock (agentGate)
            return new
            {
                active = uiBatchActive, sequence = uiBatchSequence, invoice = uiBatchInvoice,
                startedAt = uiBatchStartedAt, endedAt = uiBatchEndedAt, outcome = uiBatchOutcome,
                elapsedMs = uiBatchStartedAt is { } start ? Milliseconds((uiBatchEndedAt ?? DateTimeOffset.UtcNow) - start) : (long?)null
            };
    }

    private static object? RunHeader(WorkflowContext? current)
    {
        if (current is null) return null;
        var snapshot = current.Snapshot;
        return new
        {
            snapshot.RunId, invoiceNumber = current.Invoice.Identity.Number, status = snapshot.Status,
            mode = current.ExecutionMode, snapshot.ActiveLine, snapshot.LastErrorCode,
            lastMessage = Clip(snapshot.LastMessage, 600), snapshot.InputMayHaveBeenSent, snapshot.UpdatedAt
        };
    }

    private IReadOnlyList<WorkflowStepTiming> StepsFor(string? runId)
    {
        var (timelineRun, steps) = workflowGateway.Timeline();
        return runId is null || timelineRun == runId ? steps : [];
    }

    private LiveRunResult? LiveFor(string? runId)
    {
        lock (agentGate) return lastLiveRun is { } live && (runId is null || live.Context.Snapshot.RunId == runId) ? live : null;
    }

    private async Task<LiveVerificationRecord?> LatestVerificationAsync(string runId, CancellationToken token)
        => (await Workflow.ReadLiveVerificationHistoryAsync(token)).FirstOrDefault(record => record.RunId == runId);

    private static FailedStepView? FailedStep(IReadOnlyList<WorkflowStepTiming> steps)
        => steps.LastOrDefault(step => step.Kind == "step" && step.Outcome == "failed") is { } step
            ? new FailedStepView(step.Step, step.DurationMilliseconds, step.StartedAt) : null;

    private sealed record FailedStepView(string Step, long Ms, DateTimeOffset StartedAt);

    private static object? CompactComparison(LiveVerificationRecord? record, bool includeMatched, int limit)
    {
        if (record?.Comparison is not { } comparison) return null;
        var mismatches = comparison.Values.Where(value => !value.Matched).ToArray();
        return new
        {
            matched = comparison.Matched, total = comparison.Values.Count, mismatchCount = mismatches.Length,
            mismatches = mismatches.Take(limit).Select(value => new { value.Field, value.Expected, value.Actual, value.Source }).ToArray(),
            matchedValues = includeMatched
                ? comparison.Values.Where(value => value.Matched).Take(limit).Select(value => new { value.Field, value.Expected, value.Actual }).ToArray()
                : null
        };
    }

    private static string[] SourceHints(string? step, string? errorCode)
    {
        var hints = new List<string>();
        if (errorCode is "DIALOG_REQUIRES_ATTENTION")
            hints.Add("src/MisaManager.Automation/NativeWorkflowOperations.cs (ThrowIfBlockingDialog / dialog handling)");
        if (step?.StartsWith("line:", StringComparison.Ordinal) == true)
            hints.AddRange(["src/MisaManager.Automation/NativeWorkflowGateway.cs (item lines)",
                "src/MisaManager.Automation/NativeTableTraversal.cs", "src/MisaManager.Automation/NativeItemCatalog.cs"]);
        else if (step is "reconcile")
            hints.AddRange(["src/MisaManager.Automation/NativeWorkflowGateway.cs (ReadReconciliationPassAsync)", "src/MisaManager.Core/NativeComparison.cs"]);
        else if (step is "save")
            hints.Add("src/MisaManager.Automation/NativeWorkflowGateway.cs (save) + NativeWorkflowOperations.cs");
        else if (step is not null)
            hints.Add("src/MisaManager.Automation/NativeWorkflowGateway.cs (field " + step + ") + profiles/misa-sme2023-r20.json");
        hints.Add("src/MisaManager.Core/Workflow.cs (checkpoint/live run flow)");
        return hints.Distinct(StringComparer.Ordinal).ToArray();
    }

    // "line:12:quantity" -> "line:*:quantity" so per-column cost aggregates across rows.
    private static string StepGroup(string step)
    {
        var parts = step.Split(':');
        return parts.Length == 3 && parts[0] == "line" ? "line:*:" + parts[2] : step;
    }

    private static long Milliseconds(TimeSpan span) => Math.Max(0, (long)span.TotalMilliseconds);

    private static string? FirstLine(string? value)
    {
        if (value is null) return null;
        var index = value.IndexOf("   at ", StringComparison.Ordinal);
        return (index > 0 ? value[..index] : value).Trim();
    }

    private static string? Clip(string? value, int max) => value is null || value.Length <= max ? value : value[..max] + "…";

    private static string? ReadText(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(name + " phải là chuỗi.");
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Length > 256 ? throw new ArgumentException(name + " quá dài.") : text;
    }

    private static bool ReadBool(JsonElement payload, string name, bool fallback)
    {
        if (!payload.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException(name + " phải là boolean.")
        };
    }

    private static int ReadInt(JsonElement payload, string name, int fallback, int min, int max)
    {
        if (!payload.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            throw new ArgumentException($"{name} phải là số nguyên từ {min} đến {max}.");
        return number;
    }
}
