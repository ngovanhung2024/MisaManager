using System.Text.Json;

namespace MisaManager.Core;

public sealed record WorkflowContext(
    InvoiceDocument Invoice,
    WorkflowSnapshot Snapshot,
    string CompanyIdentity,
    string WindowIdentity,
    bool LiveValidation = false,
    System.Collections.Immutable.ImmutableDictionary<string, string>? NativeValues = null,
    string ProfileRevision = "",
    IReadOnlyList<ReconciliationAttempt>? Reconciliations = null,
    bool AllowKgToTon = false,
    bool LiveMode = false,
    WorkflowExecutionMode ExecutionMode = WorkflowExecutionMode.Interactive);

public sealed record ReconciliationAttempt(DateTimeOffset OccurredAt, string Phase,
    ReconciliationResult? Result, string? Error = null);

public sealed record WorkflowProgress(string Key, string Value, string NativeFrame, int? ActiveLine = null,
    IReadOnlyDictionary<string, string>? Values = null);

public sealed record WorkflowTransition(
    WorkflowStatus From,
    WorkflowStatus To,
    string Checkpoint,
    string ExpectedNativeState,
    string Action,
    string Verification,
    TimeSpan Timeout);

public interface IWorkflowNativeGateway
{
    Task<WorkflowObservation> ObserveAsync(WorkflowContext context, CancellationToken cancellationToken);
    Task<WorkflowObservation> ExecuteAsync(WorkflowContext context, WorkflowTransition transition,
        Func<WorkflowProgress, CancellationToken, Task> progress, CancellationToken cancellationToken);
    Task<ReconciliationResult> ReconcileAsync(WorkflowContext context, CancellationToken cancellationToken);
}

public sealed record WorkflowObservation(
    bool Confirmed,
    WorkflowStatus Status,
    string NativeFrame,
    string Message,
    InputDisposition InputDisposition = InputDisposition.NotSent,
    bool InputMayHaveBeenSent = false,
    string? ErrorCode = null, string? ErrorChain = null,
    ReconciliationResult? Comparison = null,
    // Null means unknown: fall back to InputMayHaveBeenSent. Tab selection alone is not value input.
    bool? ValueInputMayHaveBeenSent = null);

public sealed record WorkflowBatchResult(WorkflowContext Context, int AttemptedSteps, int CompletedSteps,
    string StopReason, bool InputMayHaveBeenSent, InputDisposition InputDisposition,
    string? Error = null);

public sealed class WorkflowPolicy
{
    private static readonly IReadOnlyList<WorkflowTransition> Transitions =
    [
        new(WorkflowStatus.Ready, WorkflowStatus.PreflightPassed, "preflight", "No active run; source revision matches.", "validate", "Typed invoice has no blocking issue.", TimeSpan.FromSeconds(10)),
        new(WorkflowStatus.PreflightPassed, WorkflowStatus.SalesSurface, "sales_surface", "Selected MISA sales surface is bound.", "bind_sales", "Native window identity is read back.", TimeSpan.FromSeconds(30)),
        // These transitions include identity checks, native input and several
        // readbacks. Their total budget must exceed one bounded provider call.
        new(WorkflowStatus.SalesSurface, WorkflowStatus.VoucherCreated, "voucher", "Blank voucher editor is visible.", "open_voucher", "Voucher editor identity is read back.", TimeSpan.FromSeconds(120)),
        new(WorkflowStatus.VoucherCreated, WorkflowStatus.CustomerVerified, "customer", "Customer editor contains the selected exact customer.", "select_customer", "Exact code/name/tax identity is read back.", TimeSpan.FromSeconds(120)),
        new(WorkflowStatus.CustomerVerified, WorkflowStatus.DatesVerified, "dates", "Three explicit dates are present.", "set_dates", "Each date is read back from a fresh frame.", TimeSpan.FromSeconds(120)),
        new(WorkflowStatus.DatesVerified, WorkflowStatus.InvoiceVerified, "invoice", "Invoice metadata editor is visible.", "set_invoice", "Series/number/type/currency/payment are read back.", TimeSpan.FromSeconds(120)),
        // R20 may recreate the Infragistics row and value-list providers for
        // every line/unit update. Four-line invoices can therefore need more
        // than the old three-minute transition budget even when every native
        // call is progressing normally.
        new(WorkflowStatus.InvoiceVerified, WorkflowStatus.ItemsVerified, "items", "Item grid is bound to the selected tab and row identities.", "set_items", "Every line and row identity is read back.", TimeSpan.FromMinutes(10)),
        new(WorkflowStatus.ItemsVerified, WorkflowStatus.TotalsVerified, "totals", "Totals section is visible.", "verify_totals", "Line and invoice totals match source values.", TimeSpan.FromSeconds(120)),
        new(WorkflowStatus.TotalsVerified, WorkflowStatus.AwaitingSave, "awaiting_save", "Voucher remains open and unsaved.", "stop_before_save", "User sees explicit AwaitingSave state.", TimeSpan.FromSeconds(120))
    ];

    public WorkflowTransition Next(WorkflowStatus status)
        => Transitions.SingleOrDefault(item => item.From == status)
            ?? throw new InvalidOperationException($"Không có transition workflow từ {status}.");

    public IReadOnlyList<WorkflowTransition> All => Transitions;
}

public sealed class WorkflowRunner(IWorkflowNativeGateway native, IStateStore stateStore, RunEventStore? eventStore = null)
{
    private readonly WorkflowPolicy policy = new();
    private readonly RunEventStore runEvents = eventStore ?? new RunEventStore();
    private readonly object gate = new();
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? activeOperation;
    /// <summary>Most recent live run result; the UI reads <see cref="LiveRunResult.AutoRerunSafe"/> from it.</summary>
    public LiveRunResult? LastLiveRun { get; private set; }
    private bool pauseRequested;
    private bool cancelRequested;
    private bool initialized;
    private WorkflowContext? context;

    public WorkflowContext? Current { get { lock (gate) return context; } }

    public async Task<IReadOnlyList<WorkflowContext>> ReadHistoryAsync(CancellationToken token = default)
    {
        var root = Path.Combine(stateStore.RootPath, "runs");
        if (!Directory.Exists(root)) return [];
        var records = new List<WorkflowContext>();
        foreach (var path in StateFiles.Files(root).Where(path => Path.GetFileName(path) == "workflow.json"))
        {
            var record = await stateStore.ReadAsync<WorkflowContext>("workflow-context",
                Path.GetRelativePath(stateStore.RootPath, path), token);
            if (record is not null) records.Add(record);
        }
        return records.OrderByDescending(record => record.Snapshot.UpdatedAt).ToArray();
    }

    public async Task<int> DeleteAllProfileHistoryAsync(CancellationToken token = default)
    {
        await transitionGate.WaitAsync(token);
        try
        {
            var current = Current;
            var records = (await ReadHistoryAsync(token)).ToArray();
            if (current is { } activeCurrent
                && records.All(record => record.Snapshot.RunId != activeCurrent.Snapshot.RunId))
                records = [.. records, activeCurrent];

            var deletedRuns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                var workflowPath = StateFiles.UnderRoot(stateStore.RootPath,
                    Path.Combine("runs", record.Snapshot.RunId, "workflow.json"));
                var readbacks = StateFiles.UnderRoot(stateStore.RootPath,
                    Path.Combine("reconciliation", "runs", record.Snapshot.RunId, "readbacks.json"));
                var verification = StateFiles.UnderRoot(stateStore.RootPath,
                    Path.Combine("verification", "runs", record.Snapshot.RunId + ".json"));
                if (File.Exists(workflowPath)) File.Delete(workflowPath);
                if (File.Exists(readbacks)) File.Delete(readbacks);
                if (File.Exists(verification)) File.Delete(verification);
                var runDirectory = StateFiles.UnderRoot(stateStore.RootPath,
                    Path.Combine("runs", record.Snapshot.RunId));
                if (Directory.Exists(runDirectory) && !Directory.EnumerateFileSystemEntries(runDirectory).Any())
                    Directory.Delete(runDirectory);
                var readbackDirectory = StateFiles.UnderRoot(stateStore.RootPath,
                    Path.Combine("reconciliation", "runs", record.Snapshot.RunId));
                if (Directory.Exists(readbackDirectory) && !Directory.EnumerateFileSystemEntries(readbackDirectory).Any())
                    Directory.Delete(readbackDirectory);
                deletedRuns.Add(record.Snapshot.RunId);
            }

            var deletedSessions = 0;
            foreach (var sessionName in new[] { "session.json", "session-live-test.json" })
            {
                var sessionPath = StateFiles.UnderRoot(stateStore.RootPath, sessionName);
                if (File.Exists(sessionPath))
                {
                    File.Delete(sessionPath);
                    deletedSessions++;
                }
            }

            lock (gate)
            {
                context = null;
                cancellation?.Cancel();
                cancellation = null;
                activeOperation?.Cancel();
                activeOperation = null;
            }
            return Math.Max(deletedRuns.Count, deletedSessions);
        }
        finally { transitionGate.Release(); }
    }

    private static WorkflowContext WithComparison(WorkflowContext current, string phase,
        ReconciliationResult? result, string? error = null)
        => current with { Reconciliations = [.. current.Reconciliations ?? [],
            new ReconciliationAttempt(DateTimeOffset.UtcNow, phase, result, error)] };

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await transitionGate.WaitAsync(token);
        try
        {
            if (initialized) return;
            WorkflowContext? saved;
            try
            {
                saved = await stateStore.ReadAsync<WorkflowContext>("workflow-session", "session.json", token);
            }
            catch (JsonException error)
            {
                // A session written by an older test build must never prevent a
                // fresh production import. Preserve it for diagnostics and start
                // with no active workflow instead of guessing its meaning.
                ArchiveIncompatibleSession(error.Message);
                saved = null;
            }
            catch (InvalidDataException error) when (error.Message.Contains("session", StringComparison.OrdinalIgnoreCase))
            {
                ArchiveIncompatibleSession(error.Message);
                saved = null;
            }
            if (saved is not null)
            {
                // Legacy live sessions were persisted without an execution mode.
                // Keep them out of a production import until explicitly inspected.
                if (saved.LiveMode && saved.ExecutionMode == WorkflowExecutionMode.Interactive)
                {
                    MoveSession("session.json", "session-live-test.json");
                    saved = null;
                }
                if (saved is not null)
                {
                    if (saved.Invoice.InvoiceId != saved.Snapshot.InvoiceId || saved.Invoice.Revision != saved.Snapshot.InvoiceRevision
                    || saved.CompanyIdentity != saved.Snapshot.CompanyIdentity || saved.WindowIdentity != saved.Snapshot.WindowIdentity)
                        throw new InvalidDataException("Workflow state và hóa đơn không cùng revision/target; không tự khôi phục.");
                    if (saved.Snapshot.InputPending)
                        saved = saved with { Snapshot = saved.Snapshot with
                        {
                            PausedFromStatus = saved.Snapshot.Status,
                            Status = saved.Snapshot.PendingAction == "save" ? WorkflowStatus.SaveUncertain : WorkflowStatus.InputUncertain,
                            InputMayHaveBeenSent = true, LastMessage = "Phiên trước dừng giữa thao tác và readback; không replay input."
                        } };
                    else if (!IsTerminal(saved.Snapshot.Status) && saved.Snapshot.Status is not (WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain or WorkflowStatus.Paused or WorkflowStatus.Blocked))
                        saved = Replace(saved, WorkflowStatus.Paused, null, saved.Snapshot.Status, "Đã mở lại phiên; cần đối chiếu MISA mới trước khi tiếp tục.");
                    lock (gate) { context = saved; cancellation = new(); }
                    await PersistAsync(saved, CancellationToken.None);
                }
            }
            initialized = true;
        }
        finally { transitionGate.Release(); }
    }

    /// <summary>LastErrorCode of a Discarded run whose invoice number already exists in MISA.</summary>
    public const string ExistingInvoiceCode = "INVOICE_ALREADY_EXISTS";

    public static bool IsTerminal(WorkflowStatus status) => status is WorkflowStatus.Saved or WorkflowStatus.Discarded
        or WorkflowStatus.Cancelled or WorkflowStatus.Failed;

    public const string ResolveStaleAction = "resolve_stale";
    private string? resolvedSavedRunId;
    private string? staleRunNote;

    /// <summary>
    /// A run that ended in doubt (chưa rõ, bị chặn, tạm dừng) blocks every new run. Once its voucher is no longer open
    /// in the same MISA session, a read-only check settles it: the sales register decides Saved or not saved.
    /// Nothing is typed, clicked or replayed; when it cannot be settled the run stays active.
    /// </summary>
    private async Task ResolveStaleRunAsync(string windowIdentity, CancellationToken token)
    {
        await InitializeAsync(token);
        await transitionGate.WaitAsync(token);
        try
        {
            staleRunNote = null;
            var stale = Current;
            if (stale is null || IsTerminal(stale.Snapshot.Status) || stale.Snapshot.Status == WorkflowStatus.Saved) return;
            // Absence in another MISA session (restart, other data) proves nothing about this voucher.
            if (stale.WindowIdentity != windowIdentity)
            {
                staleRunNote = "Lần chạy trước thuộc phiên MISA khác (MISA đã mở lại hoặc đổi dữ liệu).";
                return;
            }
            WorkflowObservation observation;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                observation = await native.ExecuteAsync(stale, new(stale.Snapshot.Status, WorkflowStatus.Discarded, ResolveStaleAction,
                        "Voucher no longer open", ResolveStaleAction, "Complete sales register readback", TimeSpan.FromSeconds(90)),
                    (_, _) => Task.CompletedTask, deadline.Token);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                observation = new(false, stale.Snapshot.Status, "", error.Message);
            }
            if (!observation.Confirmed || observation.Status is not (WorkflowStatus.Saved or WorkflowStatus.Discarded))
            {
                staleRunNote = observation.Message;
                return;
            }
            var resolved = Replace(stale, observation.Status, observation.NativeFrame, message: observation.Message);
            lock (gate) context = resolved;
            await PersistAsync(resolved, CancellationToken.None);
            // The register just proved the save with the editor closed; StartAsync need not prove it again.
            if (observation.Status == WorkflowStatus.Saved) resolvedSavedRunId = resolved.Snapshot.RunId;
        }
        finally { transitionGate.Release(); }
    }

    public async Task<WorkflowContext> StartAsync(InvoiceDocument invoice, string companyIdentity,
        string windowIdentity, CancellationToken cancellationToken = default, bool liveValidation = false, string profileRevision = "",
        string? requestId = null, string? operationId = null, bool allowKgToTon = false,
        bool liveMode = false,
        WorkflowExecutionMode executionMode = WorkflowExecutionMode.Interactive)
    {
        await InitializeAsync(cancellationToken);
        await transitionGate.WaitAsync(cancellationToken);
        try
        {
            var validationIssues = InvoiceValidation.Validate(invoice);
            if (invoice.IsBlocked || validationIssues.Any(issue => issue.Severity == IssueSeverity.Blocking))
            {
                var blocking = validationIssues.Concat(invoice.Issues.IsDefault
                        ? [] : invoice.Issues.Where(issue => issue is not null))
                    .Where(issue => issue.Severity == IssueSeverity.Blocking)
                    .Select(issue => issue.Code + ": " + issue.Message).Distinct();
                throw new InvalidOperationException("Hóa đơn còn lỗi chặn; chưa được phép bắt đầu workflow. "
                    + string.Join(" | ", blocking));
            }
            var previous = Current;
            if (previous?.Snapshot.Status == WorkflowStatus.Saved && previous.Snapshot.RunId != resolvedSavedRunId)
            {
                // Recovery can confirm Save while its read-only editor remains
                // open. Finish that verified editor before replacing its run.
                if (previous.CompanyIdentity != companyIdentity)
                    throw new InvalidOperationException("Finish the saved voucher in its bound MISA session before switching targets.");
                // A process restart changes HWND/PID. Rebind only this verified
                // saved outcome, and require fresh native proof before replacing it.
                var candidate = previous with { WindowIdentity = windowIdentity,
                    Snapshot = previous.Snapshot with { WindowIdentity = windowIdentity } };
                using var preparationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                preparationDeadline.CancelAfter(TimeSpan.FromSeconds(120));
                var proof = await native.ExecuteAsync(candidate,
                    new(WorkflowStatus.Saved, WorkflowStatus.Saved, "prepare_next", "Verified saved voucher",
                        "verify_only", "Native saved proof and read-only editor close", TimeSpan.FromSeconds(120)),
                    (_, _) => Task.CompletedTask, preparationDeadline.Token);
                if (!proof.Confirmed) throw new InvalidOperationException("Cannot prepare the next invoice: " + proof.Message);
                previous = Replace(candidate, WorkflowStatus.Saved, proof.NativeFrame, message: proof.Message);
                lock (gate) context = previous;
            }
            lock (gate)
            {
                if (context is not null && !IsTerminal(context.Snapshot.Status))
                    throw new InvalidOperationException(
                        $"Hóa đơn {context.Invoice.Identity.Number} của lần chạy trước chưa xác định được kết quả ({context.Snapshot.Status}). "
                        + (staleRunNote ?? context.Snapshot.LastMessage ?? "")
                        + " Kiểm tra chứng từ đó trong MISA, Cất hoặc đóng nó, mở lại danh sách Bán hàng rồi bấm Thêm vào MISA: "
                        + "tool sẽ tự đọc danh sách MISA để biết đã Cất hay chưa.");
                cancellation?.Dispose();
                cancellation = new CancellationTokenSource();
                context = NewContext(invoice, companyIdentity, windowIdentity, requestId, operationId) with
                {
                    LiveValidation = liveValidation, ProfileRevision = profileRevision,
                    LiveMode = liveMode, ExecutionMode = executionMode,
                    AllowKgToTon = allowKgToTon || previous?.AllowKgToTon == true
                        && previous.Invoice.Source.Sha256 == invoice.Source.Sha256
                        && previous.CompanyIdentity == companyIdentity && previous.WindowIdentity == windowIdentity
                };
            }
            await PersistAsync(context!, CancellationToken.None);
            return context!;
        }
        finally { transitionGate.Release(); }
    }

    public async Task<WorkflowContext> AdvanceAsync(CancellationToken cancellationToken = default)
    {
        await transitionGate.WaitAsync(cancellationToken);
        try { return await AdvanceCoreAsync(cancellationToken); }
        finally { transitionGate.Release(); }
    }

    // Hold the same transition lease across the batch; pause can still cancel active native work.
    public async Task<WorkflowBatchResult> RunUntilAsync(string runId, string revision,
        WorkflowStatus expectedCheckpoint, WorkflowStatus until, int maxSteps,
        CancellationToken token = default, bool allowKgToTon = false)
    {
        if (until < WorkflowStatus.Ready || until > WorkflowStatus.AwaitingSave || maxSteps is < 1 or > 20)
            throw new ArgumentException("Invalid batch target or step limit.");
        await transitionGate.WaitAsync(token);
        try
        {
            var current = Current ?? throw new AutomationException("BATCH_NO_RUN", "No active workflow.");
            if (current.Snapshot.RunId != runId || current.Invoice.Fingerprint != revision
                || current.Snapshot.Status != expectedCheckpoint)
                throw new AutomationException("BATCH_CONFLICT", "Run, invoice revision or checkpoint changed; no batch input sent.");
            if (current.Snapshot.Status < WorkflowStatus.Ready || current.Snapshot.Status > WorkflowStatus.AwaitingSave
                || current.Snapshot.InputPending || until < current.Snapshot.Status)
                throw new AutomationException("BATCH_RECOVERY_REQUIRED", "Batch requires a confirmed forward checkpoint; recover explicitly first.");
            if (allowKgToTon && !current.AllowKgToTon)
            {
                current = current with { AllowKgToTon = true };
                await PersistAsync(current, CancellationToken.None);
            }
            var attempted = 0;
            var completed = 0;
            var sent = false;
            var disposition = InputDisposition.NotSent;
            while (current.Snapshot.Status != until && attempted < maxSteps)
            {
                lock (gate)
                    if (pauseRequested || cancelRequested || token.IsCancellationRequested)
                        return new(current, attempted, completed, "interrupted", sent, disposition);
                var mayInput = policy.Next(current.Snapshot.Status).Action is
                    "open_voucher" or "select_customer" or "set_dates" or "set_invoice" or "set_items";
                attempted++;
                try { current = await AdvanceCoreAsync(token); }
                catch (OperationCanceledException error)
                {
                    current = Current ?? current;
                    var sentOnCancel = sent || mayInput || current.Snapshot.InputPending || current.Snapshot.InputMayHaveBeenSent;
                    return new(current, attempted, completed, "interrupted", sentOnCancel,
                        sentOnCancel ? InputDisposition.Uncertain : disposition, error.ToString());
                }
                catch (Exception error)
                {
                    current = Current ?? current;
                    // A persistence failure can occur after input; never report it as not sent.
                    sent |= mayInput || current.Snapshot.InputPending || current.Snapshot.InputMayHaveBeenSent;
                    return new(current, attempted, completed, "error", sent,
                        sent ? InputDisposition.Uncertain : disposition, error.ToString());
                }
                sent |= current.Snapshot.InputMayHaveBeenSent;
                if (current.Snapshot.LastInputDisposition != InputDisposition.NotSent)
                    disposition = current.Snapshot.LastInputDisposition;
                if (current.Snapshot.Status < WorkflowStatus.Ready || current.Snapshot.Status > WorkflowStatus.AwaitingSave)
                    return new(current, attempted, completed, "checkpoint_failed", sent, disposition);
                completed++;
            }
            return new(current, attempted, completed,
                current.Snapshot.Status == until ? "target_reached" : "step_limit", sent, disposition);
        }
        finally { transitionGate.Release(); }
    }

    /// <summary>
    /// Execute one invoice exactly as a live user does: enter it, reconcile the
    /// complete voucher, then save on a match or discard on a mismatch.  A
    /// failure is terminal for this attempt; callers must build/restart and
    /// submit the invoice again from Ready rather than resuming a checkpoint.
    /// </summary>
    public async Task<LiveRunResult> RunLiveAsync(InvoiceDocument invoice, string companyIdentity,
        string windowIdentity, CancellationToken token = default, bool liveValidation = true,
        string profileRevision = "", bool allowKgToTon = false, string? requestId = null,
        string? operationId = null,
        WorkflowExecutionMode executionMode = WorkflowExecutionMode.LiveProduction)
    {
        if (executionMode == WorkflowExecutionMode.LiveTest)
            throw new ArgumentException("Live test mode was retired; use the fake MISA integration tests instead.", nameof(executionMode));
        var startedAt = DateTimeOffset.UtcNow;
        WorkflowContext? current = null;
        ReconciliationResult? comparison = null;
        try
        {
            await ResolveStaleRunAsync(windowIdentity, token);
            current = await StartAsync(invoice, companyIdentity, windowIdentity, token, liveValidation,
                profileRevision, requestId, operationId, allowKgToTon,
                executionMode: executionMode);
            var batch = await RunUntilAsync(current.Snapshot.RunId, invoice.Fingerprint, WorkflowStatus.Ready,
                WorkflowStatus.AwaitingSave, 20, token, allowKgToTon);
            current = batch.Context;
            if (batch.StopReason != "target_reached" || current.Snapshot.Status != WorkflowStatus.AwaitingSave)
            {
                var message = batch.Error ?? current.Snapshot.LastMessage
                    ?? $"Live run stopped at {current.Snapshot.Status}: {batch.StopReason}.";
                var failureCode = current.Snapshot.LastErrorCode;
                var nothingOpened = !current.Snapshot.InputMayHaveBeenSent && !VoucherWasOpened(current);
                if (batch.StopReason is not "interrupted")
                    current = await TryDiscardFailedVoucherAsync(current);
                var clean = batch.StopReason is not "interrupted"
                    && (current.Snapshot.Status == WorkflowStatus.Discarded || nothingOpened);
                var rerunSafe = batch.StopReason == "checkpoint_failed" && InterferenceRetry.IsTransient(failureCode) && clean;
                current = await MarkLiveFailureAsync(current, message, current.Snapshot.LastErrorCode ?? failureCode ?? "LIVE_RUN_FAILED", token);
                return await CompleteLiveRecordAsync(current, startedAt, "failed", false, false, null, message, token, rerunSafe, clean);
            }

            comparison = await ReconcileWithRetryAsync(token);
            if (comparison.Matched)
            {
                current = await FinishAsync(save: true, token: token);
                if (current.Snapshot.Status == WorkflowStatus.Saved)
                    return await CompleteLiveRecordAsync(current, startedAt, "saved", true, true, comparison, null, token);
                if (current.Snapshot.Status == WorkflowStatus.Blocked && current.Snapshot.LastErrorCode == ExistingInvoiceCode)
                {
                    // MISA refused the save because the invoice number exists and "No" was answered:
                    // close the unsaved voucher and let the caller move on to the next invoice.
                    var existing = (current.Snapshot.LastMessage ?? "Hóa đơn đã tồn tại trong MISA.")
                        + " Đã đóng chứng từ, chuyển sang hóa đơn tiếp theo.";
                    current = await FinishAsync(save: false, token: token);
                    if (current.Snapshot.Status == WorkflowStatus.Discarded)
                    {
                        current = current with { Snapshot = current.Snapshot with { LastErrorCode = ExistingInvoiceCode, LastMessage = existing } };
                        lock (gate) context = current;
                        await PersistAsync(current, token);
                        return await CompleteLiveRecordAsync(current, startedAt, "existing", true, true, comparison, existing, token);
                    }
                }

                var message = current.Snapshot.LastMessage ?? "Cất hóa đơn chưa được xác nhận.";
                current = await MarkLiveFailureAsync(current, message, current.Snapshot.LastErrorCode ?? "SAVE_UNCERTAIN", token);
                return await CompleteLiveRecordAsync(current, startedAt, "failed", true, true, comparison, message, token);
            }

            // A mismatch is a completed business decision, not a workflow error:
            // discard the open voucher and keep the mismatch as the proof.
            current = await FinishAsync(save: false, token: token);
            if (current.Snapshot.Status == WorkflowStatus.Discarded)
                return await CompleteLiveRecordAsync(current, startedAt, "discarded", true, false, comparison,
                    "Đối chiếu không khớp; đã Bỏ chứng từ và lưu hồ sơ kiểm chứng.", token);

            var discardMessage = current.Snapshot.LastMessage ?? "Không xác nhận được thao tác Bỏ.";
            current = await MarkLiveFailureAsync(current, discardMessage, current.Snapshot.LastErrorCode ?? "DISCARD_UNCERTAIN", token);
            return await CompleteLiveRecordAsync(current, startedAt, "failed", true, false, comparison, discardMessage, token);
        }
        catch (OperationCanceledException error)
        {
            if (current is null) throw;
            current = Refreshed(current);
            current = await MarkLiveFailureAsync(current, error.Message, "LIVE_RUN_CANCELLED", CancellationToken.None);
            return await CompleteLiveRecordAsync(current, startedAt, "failed", comparison is not null,
                comparison?.Matched == true, comparison, error.Message, CancellationToken.None);
        }
        catch (Exception error)
        {
            if (current is null) throw;
            current = Refreshed(current);
            var nothingOpened = !current.Snapshot.InputMayHaveBeenSent && !VoucherWasOpened(current)
                && current.Snapshot.Status is not (WorkflowStatus.SaveUncertain or WorkflowStatus.Saved);
            current = await TryDiscardFailedVoucherAsync(current);
            var clean = current.Snapshot.Status == WorkflowStatus.Discarded || nothingOpened;
            var rerunSafe = error is AutomationException { Code: var code } && InterferenceRetry.IsTransient(code)
                && current.Snapshot.Status == WorkflowStatus.Discarded;
            current = await MarkLiveFailureAsync(current, error.Message,
                current.Snapshot.LastErrorCode ?? (error as AutomationException)?.Code ?? "LIVE_RUN_ERROR", CancellationToken.None, error.ToString());
            return await CompleteLiveRecordAsync(current, startedAt, "failed", comparison is not null,
                comparison?.Matched == true, comparison, error.Message, CancellationToken.None, rerunSafe, clean);
        }
    }

    // The local copy can be stale after a failed step (e.g. Save clicked, then persisting failed and the
    // runner moved to SaveUncertain); writing Failed over that would allow a duplicate Save on rerun.
    private WorkflowContext Refreshed(WorkflowContext local)
        => Current is { } latest && latest.Snapshot.RunId == local.Snapshot.RunId ? latest : local;

    /// <summary>Reconciliation only reads (and switches view tabs), so a disturbed read is simply repeated.</summary>
    private async Task<ReconciliationResult> ReconcileWithRetryAsync(CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await ReconcileAsync(token); }
            catch (AutomationException error) when (attempt <= InterferenceRetry.ReconcileRetries
                && InterferenceRetry.IsTransient(error.Code) && !token.IsCancellationRequested)
            {
                await Task.Delay(InterferenceRetry.Delay(attempt), token);
            }
        }
    }

    private static bool VoucherWasOpened(WorkflowContext value)
    {
        var status = value.Snapshot.Status;
        return status >= WorkflowStatus.VoucherCreated && status <= WorkflowStatus.AwaitingSave
            || value.Snapshot.PausedFromStatus is { } paused && paused >= WorkflowStatus.VoucherCreated
                && paused <= WorkflowStatus.AwaitingSave;
    }

    public async Task<IReadOnlyList<LiveVerificationRecord>> ReadLiveVerificationHistoryAsync(CancellationToken token = default)
    {
        var root = Path.Combine(stateStore.RootPath, "verification", "runs");
        if (!Directory.Exists(root)) return [];
        var records = new List<LiveVerificationRecord>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            var record = await stateStore.ReadAsync<LiveVerificationRecord>("live-verification",
                Path.GetRelativePath(stateStore.RootPath, path), token);
            if (record is not null) records.Add(record);
        }
        return records.OrderByDescending(item => item.CompletedAt).ToArray();
    }

    private async Task<WorkflowContext> MarkLiveFailureAsync(WorkflowContext value, string message,
        string errorCode, CancellationToken token, string? errorChain = null)
    {
        // A save uncertainty must remain recoverable as SaveUncertain; turning
        // it into Failed could permit a duplicate invoice on the next run.
        var failureStatus = value.Snapshot.Status == WorkflowStatus.SaveUncertain
            ? WorkflowStatus.SaveUncertain : WorkflowStatus.Failed;
        var failed = Replace(value, failureStatus, value.Snapshot.NativeFrame,
            value.Snapshot.PausedFromStatus, message);
        failed = failed with { Snapshot = failed.Snapshot with
        {
            LastErrorCode = errorCode,
            ErrorChain = errorChain ?? value.Snapshot.ErrorChain ?? message,
            InputMayHaveBeenSent = value.Snapshot.InputMayHaveBeenSent,
            LastInputDisposition = value.Snapshot.LastInputDisposition,
            InputPending = false,
            PendingAction = null
        } };
        lock (gate) context = failed;
        await PersistAsync(failed, token);
        return failed;
    }

    private async Task<WorkflowContext> TryDiscardFailedVoucherAsync(WorkflowContext value)
    {
        var status = value.Snapshot.Status;
        if (!VoucherWasOpened(value) || status is WorkflowStatus.SaveUncertain or WorkflowStatus.Saved
            or WorkflowStatus.Discarded)
            return value;
        try
        {
            var discarded = await FinishAsync(save: false, token: CancellationToken.None);
            return discarded.Snapshot.Status == WorkflowStatus.Discarded ? discarded : value;
        }
        catch (Exception error)
        {
            // The original failure remains authoritative.  The repair packet
            // will include this cleanup failure without replaying input.
            return value with { Snapshot = value.Snapshot with
            {
                LastMessage = value.Snapshot.LastMessage + " Cleanup Bỏ thất bại: " + error.Message,
                ErrorChain = (value.Snapshot.ErrorChain ?? "") + "\nCleanup: " + error
            } };
        }
    }

    private async Task<LiveRunResult> CompleteLiveRecordAsync(WorkflowContext value, DateTimeOffset startedAt,
        string outcome, bool reconciled, bool matched, ReconciliationResult? comparison, string? error,
        CancellationToken token, bool autoRerunSafe = false, bool misaClean = false)
    {
        var verification = new LiveVerificationRecord(value.Snapshot.RunId, value.Invoice.InvoiceId,
            value.Invoice.Revision, value.Invoice.Fingerprint, value.Invoice.Source.Sha256, startedAt,
            DateTimeOffset.UtcNow, outcome, reconciled, matched, comparison, value.Snapshot.LastErrorCode,
            error, value.Snapshot.ErrorChain,
            outcome is "failed" && value.Snapshot.Status != WorkflowStatus.SaveUncertain,
            value.Snapshot.RuntimeIdentity);
        await stateStore.WriteAsync("live-verification", Path.Combine("verification", "runs",
            value.Snapshot.RunId + ".json"), verification, token);
        // A clean failure must never hide a save in doubt.
        misaClean &= value.Snapshot.Status != WorkflowStatus.SaveUncertain;
        var result = new LiveRunResult(value, verification, outcome, verification.RetryFromStart, error, autoRerunSafe, misaClean);
        LastLiveRun = result;
        return result;
    }

    private async Task<WorkflowContext> AdvanceCoreAsync(CancellationToken cancellationToken)
    {
            WorkflowContext current;
            CancellationToken runToken;
            lock (gate)
            {
                current = context ?? throw new InvalidOperationException("Chưa có workflow active.");
                runToken = cancellation?.Token ?? CancellationToken.None;
            }
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(runToken, cancellationToken);
            var token = operation.Token;
            lock (gate)
            {
                activeOperation = operation;
                if (pauseRequested || cancelRequested) operation.Cancel();
            }
            try
            {
                token.ThrowIfCancellationRequested();
                if (current.Snapshot.Status is WorkflowStatus.Paused or WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain)
                    throw new InvalidOperationException("Workflow đang dừng ở trạng thái cần xử lý; không tự replay input.");
                var transition = policy.Next(current.Snapshot.Status);
                // Hash the workbook once per run; FinishAsync checks it again before Save.
                if (current.Snapshot.Status == WorkflowStatus.Ready
                    && await FileDigest.Sha256Async(current.Invoice.Source.FilePath, token) != current.Invoice.Source.Sha256)
                    throw new InvalidDataException("Workbook đã thay đổi; nhập lại dữ liệu trước khi tiếp tục.");
                var mayInput = transition.Action is "open_voucher" or "select_customer" or "set_dates" or "set_invoice" or "set_items";
                // Disk checkpoints only where a crash changes what is safe to do next: before a voucher
                // exists, once it exists, and before/after Save. Other steps stay in memory.
                var durable = transition.Action == "open_voucher" || transition.To is WorkflowStatus.VoucherCreated or WorkflowStatus.AwaitingSave;
                if (mayInput)
                {
                    current = current with { Snapshot = current.Snapshot with { InputPending = true, PendingAction = transition.Action } };
                    lock (gate) context = current;
                    if (durable) await PersistAsync(current, CancellationToken.None);
                }
                WorkflowObservation observation;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(transition.Timeout);
                        observation = await native.ExecuteAsync(current, transition, (progress, _) =>
                        {
                            current = ApplyProgress(current, progress);
                            lock (gate) context = current;
                            return Task.CompletedTask;
                        }, deadline.Token);
                    }
                    catch (AutomationException error)
                    {
                        observation = new(false, error.InputMayHaveBeenSent ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked,
                            current.Snapshot.NativeFrame ?? "", error.Message, error.InputMayHaveBeenSent ? InputDisposition.Uncertain : InputDisposition.NotSent,
                            error.InputMayHaveBeenSent, error.Code, error.ToString());
                    }
                    catch (Exception error)
                    {
                        observation = new(false, mayInput ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked,
                            current.Snapshot.NativeFrame ?? "", error.Message, mayInput ? InputDisposition.Uncertain : InputDisposition.NotSent, mayInput,
                            error is OperationCanceledException ? "CANCELLED" : "WORKFLOW_ERROR", error.ToString());
                    }
                    // Replaying a step is only safe when it changed no voucher value (tab switches are view-only).
                    if (observation.Confirmed || attempt > InterferenceRetry.StepRetries
                        || (observation.ValueInputMayHaveBeenSent ?? observation.InputMayHaveBeenSent)
                        || !InterferenceRetry.IsTransient(observation.ErrorCode) || token.IsCancellationRequested)
                        break;
                    lock (gate)
                    {
                        if (pauseRequested || cancelRequested) break;
                        current = current with { Snapshot = current.Snapshot with { LastMessage =
                            $"MISA bị gián đoạn ({observation.Message}); tự thử lại bước {transition.Checkpoint} lần {attempt}/{InterferenceRetry.StepRetries}…" } };
                        context = current;
                    }
                    try { await Task.Delay(InterferenceRetry.Delay(attempt), token); }
                    catch (OperationCanceledException) { break; }
                }
                // A "confirmed" step that landed on another checkpoint is not a success; input may
                // already be in MISA, so it must stop as uncertain instead of being replayed.
                if (observation.Confirmed && observation.Status != transition.To)
                    observation = observation with { Confirmed = false, InputMayHaveBeenSent = mayInput || observation.InputMayHaveBeenSent,
                        InputDisposition = mayInput ? InputDisposition.Uncertain : observation.InputDisposition,
                        ErrorCode = "CHECKPOINT_MISMATCH",
                        Message = $"MISA ở checkpoint {observation.Status}, không phải {transition.To}; kiểm tra chứng từ trước khi chạy tiếp." };
                if (observation.Comparison is not null || transition.To == WorkflowStatus.TotalsVerified)
                    current = WithComparison(current, transition.Checkpoint, observation.Comparison,
                        observation.Comparison is null ? observation.Message : null);
                current = current with { Snapshot = current.Snapshot with { InputPending = false, PendingAction = null,
                    InputMayHaveBeenSent = observation.InputMayHaveBeenSent,
                    LastInputDisposition = observation.InputDisposition, LastErrorCode = observation.ErrorCode,
                    ErrorChain = observation.ErrorChain } };
                if (!observation.Confirmed)
                {
                    var uncertain = Replace(current, observation.InputMayHaveBeenSent ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked,
                        observation.NativeFrame, current.Snapshot.Status, observation.Message);
                    lock (gate) context = uncertain;
                    await PersistAsync(uncertain, CancellationToken.None);
                    return uncertain;
                }
                if (observation.Status != transition.To)
                    throw new InvalidDataException("Native observation không khớp checkpoint đích.");
                var next = Replace(current, transition.To, observation.NativeFrame, message: observation.Message);
                // Replace clears stale uncertainty during recovery; a completed
                // transition must retain the input evidence from its own gateway call.
                next = next with { Snapshot = next.Snapshot with
                {
                    InputMayHaveBeenSent = observation.InputMayHaveBeenSent,
                    LastInputDisposition = observation.InputDisposition
                } };
                lock (gate) context = next;
                if (durable) await PersistAsync(next, CancellationToken.None);
                return next;
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(activeOperation, operation)) activeOperation = null;
                }
            }
    }

    public async Task PauseAsync(CancellationToken token = default)
    {
        lock (gate)
        {
            var current = context ?? throw new InvalidOperationException("Chưa có workflow active.");
            if (activeOperation is null && current.Snapshot.Status is (WorkflowStatus.Paused or WorkflowStatus.Blocked or WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain
                or WorkflowStatus.Cancelled or WorkflowStatus.Saved or WorkflowStatus.Discarded))
                throw new InvalidOperationException("Workflow cannot be paused in its current state.");
            pauseRequested = true;
            activeOperation?.Cancel();
        }
        try { await transitionGate.WaitAsync(token); }
        catch
        {
            lock (gate) pauseRequested = false;
            throw;
        }
        try
        {
            WorkflowContext current;
            lock (gate) current = context ?? throw new InvalidOperationException("Chưa có workflow active.");
            if (current.Snapshot.Status is WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain or WorkflowStatus.Paused)
                return;
            if (current.Snapshot.Status is WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain or WorkflowStatus.Cancelled
                or WorkflowStatus.Saved or WorkflowStatus.Discarded)
                throw new InvalidOperationException("Workflow cannot be paused in its current state.");
            // A step cancelled by this pause ends as Blocked with the real checkpoint in PausedFromStatus; keep it so Resume works.
            var resumeFrom = current.Snapshot.Status == WorkflowStatus.Blocked
                ? current.Snapshot.PausedFromStatus ?? current.Snapshot.Status : current.Snapshot.Status;
            var paused = Replace(current, WorkflowStatus.Paused, null, resumeFrom);
            lock (gate) context = paused;
            await PersistAsync(paused, CancellationToken.None);
        }
        finally
        {
            lock (gate) pauseRequested = false;
            transitionGate.Release();
        }
    }

    public async Task<WorkflowContext> ResumeAsync(CancellationToken token = default)
    {
        await transitionGate.WaitAsync(token);
        try
        {
            WorkflowContext current;
            lock (gate) current = context ?? throw new InvalidOperationException("No active workflow.");
            if (current.Snapshot.Status is not (WorkflowStatus.Paused or WorkflowStatus.Blocked) || current.Snapshot.PausedFromStatus is null)
                throw new InvalidOperationException("Workflow is not paused or has no resume checkpoint.");
            if (await FileDigest.Sha256Async(current.Invoice.Source.FilePath, token) != current.Invoice.Source.Sha256)
                throw new InvalidDataException("Workbook đã thay đổi; không resume revision cũ.");
            var observation = await native.ObserveAsync(current, token);
            if (!observation.Confirmed || observation.Status != current.Snapshot.PausedFromStatus.Value)
            {
                var blocked = Replace(current, observation.InputMayHaveBeenSent ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked,
                    observation.NativeFrame, current.Snapshot.PausedFromStatus, observation.Message);
                blocked = blocked with { Snapshot = blocked.Snapshot with {
                    InputMayHaveBeenSent = observation.InputMayHaveBeenSent,
                    LastInputDisposition = observation.InputDisposition, LastErrorCode = observation.ErrorCode,
                    ErrorChain = observation.ErrorChain } };
                lock (gate) context = blocked;
                await PersistAsync(blocked, CancellationToken.None);
                throw new InvalidOperationException("Không thể tiếp tục: native readback chưa xác nhận đúng MISA checkpoint; không replay input.");
            }
            var resumed = Replace(current, current.Snapshot.PausedFromStatus.Value, observation.NativeFrame);
            lock (gate) context = resumed;
            await PersistAsync(resumed, CancellationToken.None);
            return resumed;
        }
        finally { transitionGate.Release(); }
    }

    public async Task CancelAsync(CancellationToken token = default)
    {
        lock (gate)
        {
            cancelRequested = true;
            activeOperation?.Cancel();
        }
        try { await transitionGate.WaitAsync(token); }
        catch
        {
            lock (gate) cancelRequested = false;
            throw;
        }
        try
        {
        WorkflowContext current;
        lock (gate)
        {
            current = context ?? throw new InvalidOperationException("Chưa có workflow active.");
        }
        if (current.Snapshot.InputMayHaveBeenSent || current.Snapshot.InputPending
            || current.Snapshot.Status is WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain
            || current.Snapshot.PausedFromStatus == WorkflowStatus.SaveUncertain
            || current.NativeValues?.Count > 0
            || HasVoucherCheckpoint(current.Snapshot.Status)
            || current.Snapshot.PausedFromStatus is { } previous && HasVoucherCheckpoint(previous))
            throw new InvalidOperationException("Đã có hoặc chưa rõ input MISA; giữ phiên để đối chiếu, không xóa trạng thái bằng Cancel.");
        lock (gate) cancellation?.Cancel();
        var cancelled = Replace(current, WorkflowStatus.Cancelled, null);
        lock (gate) context = cancelled;
        await PersistAsync(cancelled, CancellationToken.None);
        }
        finally
        {
            lock (gate) cancelRequested = false;
            transitionGate.Release();
        }
    }

    public async Task<ReconciliationResult> ReconcileAsync(CancellationToken token = default)
    {
        await transitionGate.WaitAsync(token);
        try
        {
            var current = Current ?? throw new InvalidOperationException("Chưa có workflow để đối chiếu.");
            if (await FileDigest.Sha256Async(current.Invoice.Source.FilePath, token) != current.Invoice.Source.Sha256)
                throw new InvalidDataException("Workbook đã thay đổi; readback không áp dụng cho revision này.");
            ReconciliationResult result;
            try { result = await native.ReconcileAsync(current, token); }
            catch (Exception error)
            {
                await PersistAsync(WithComparison(current, "manual", null, error.Message), CancellationToken.None);
                throw;
            }
            await PersistAsync(WithComparison(current, "manual", result), CancellationToken.None);
            await stateStore.WriteAsync("reconciliation", Path.Combine("runs", current.Snapshot.RunId, "readbacks.json"), result, token);
            return result;
        }
        finally { transitionGate.Release(); }
    }

    public async Task<WorkflowContext> FinishAsync(bool save, bool resolveSaveUncertain = false, CancellationToken token = default)
    {
        await transitionGate.WaitAsync(token);
        try
        {
            var current = Current ?? throw new InvalidOperationException("Chưa có workflow.");
            if (save && current.Snapshot.Status != WorkflowStatus.AwaitingSave)
                throw new InvalidOperationException("Chỉ Cất sau khi đối chiếu toàn bộ chứng từ.");
            var discardUncertain = !save && resolveSaveUncertain
                && ((current.Snapshot.Status == WorkflowStatus.SaveUncertain
                        && current.Snapshot.PausedFromStatus == WorkflowStatus.AwaitingSave)
                    || (current.Snapshot.Status == WorkflowStatus.Blocked
                        && (current.Snapshot.PausedFromStatus == WorkflowStatus.SaveUncertain
                            || current.Snapshot.PausedFromStatus == WorkflowStatus.Blocked
                                && current.Snapshot.LastErrorCode == "EDITOR_NOT_FOUND")));
            if ((current.Snapshot.Status == WorkflowStatus.SaveUncertain && !discardUncertain) || IsTerminal(current.Snapshot.Status)
                || current.Snapshot.Status == WorkflowStatus.Blocked
                    && (current.Snapshot.PausedFromStatus == WorkflowStatus.SaveUncertain
                        || current.Snapshot.PausedFromStatus == WorkflowStatus.Blocked
                            && current.Snapshot.LastErrorCode == "EDITOR_NOT_FOUND")
                    && !discardUncertain)
                throw new InvalidOperationException("Không lặp Cất/Bỏ khi kết quả đã lưu hoặc chưa rõ.");
            if (await FileDigest.Sha256Async(current.Invoice.Source.FilePath, token) != current.Invoice.Source.Sha256)
                throw new InvalidDataException("Workbook đã thay đổi; không Cất/Bỏ với revision cũ.");
            var action = save ? "save" : "discard";
            var target = save ? WorkflowStatus.Saved : WorkflowStatus.Discarded;
            var transition = new WorkflowTransition(current.Snapshot.Status, target, action, "Same verified voucher",
                // Save includes full reconciliation before the single write
                // and native register readback afterward. Give both stages
                // their bounded budget instead of cancelling the last read.
                action, "Authoritative result and voucher identity", TimeSpan.FromSeconds(save || discardUncertain ? 300 : 30));
            current = current with { Snapshot = current.Snapshot with { InputPending = true, PendingAction = action } };
            lock (gate) context = current;
            await PersistAsync(current, CancellationToken.None);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Save is not interruptible by Dừng ngay: cancelling mid-click leaves MISA in a "saved or not?" state.
            // The stop takes effect right after the save is proven; the batch then halts.
            if (!save) lock (gate) activeOperation = deadline;
            deadline.CancelAfter(transition.Timeout);
            WorkflowObservation observation;
            try
            {
                observation = await native.ExecuteAsync(current, transition, (progress, _) =>
                {
                    current = ApplyProgress(current, progress);
                    lock (gate) context = current;
                    return Task.CompletedTask;
                }, deadline.Token);
            }
            catch (Exception error)
            {
                var sent = error is not AutomationException fault || fault.InputMayHaveBeenSent;
                observation = new(false, sent ? (save ? WorkflowStatus.SaveUncertain : WorkflowStatus.InputUncertain) : WorkflowStatus.Blocked,
                    current.Snapshot.NativeFrame ?? "", error.Message, sent ? InputDisposition.Uncertain : InputDisposition.NotSent, sent,
                    error is AutomationException automationError ? automationError.Code : "WORKFLOW_ERROR", error.ToString());
            }
            finally { lock (gate) activeOperation = null; }
            if (save)
                current = WithComparison(current, "before_save", observation.Comparison,
                    observation.Comparison is null ? observation.Message : null);
            var status = observation.Confirmed ? target : save
                ? observation.Status == WorkflowStatus.SaveUncertain ? WorkflowStatus.SaveUncertain : WorkflowStatus.Blocked
                : observation.InputMayHaveBeenSent ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked;
            WorkflowStatus? pausedFrom = observation.Confirmed
                ? null
                : discardUncertain ? WorkflowStatus.SaveUncertain : current.Snapshot.Status;
            current = Replace(current, status, observation.NativeFrame, pausedFrom, observation.Message);
            current = current with { Snapshot = current.Snapshot with { InputPending = false, PendingAction = null,
                InputMayHaveBeenSent = current.Snapshot.InputMayHaveBeenSent || observation.InputMayHaveBeenSent,
                LastInputDisposition = observation.InputDisposition, LastErrorCode = observation.ErrorCode,
                ErrorChain = observation.ErrorChain } };
            lock (gate) context = current;
            await PersistAsync(current, CancellationToken.None);
            return current;
        }
        finally { transitionGate.Release(); }
    }

    private static WorkflowContext ApplyProgress(WorkflowContext current, WorkflowProgress progress)
    {
        var values = (current.NativeValues ?? System.Collections.Immutable.ImmutableDictionary<string, string>.Empty)
            .SetItem(progress.Key, progress.Value);
        if (progress.Values is not null)
            foreach (var item in progress.Values) values = values.SetItem(item.Key, item.Value);
        return current with
        {
            NativeValues = values,
            Snapshot = current.Snapshot with { NativeFrame = progress.NativeFrame, ActiveLine = progress.ActiveLine,
                UpdatedAt = DateTimeOffset.UtcNow }
        };
    }

    private async Task PersistAsync(WorkflowContext value, CancellationToken token)
    {
        var correlation = OperationContext.Current;
        value = value with { Snapshot = value.Snapshot with
        {
            AppVersion = ProductIdentity.RuntimeVersion,
            RequestId = correlation?.RequestId,
            OperationId = correlation?.OperationId,
            RuntimeIdentity = new(ProductIdentity.RuntimeVersion, ProductIdentity.SourceRevision, value.ProfileRevision)
        } };
        lock (gate) context = value;
        // One authoritative document commits invoice, checkpoint and pending input atomically.
        try
        {
            await stateStore.WriteAsync("workflow-session", SessionPath(value.ExecutionMode), value, token);
            await stateStore.WriteAsync("workflow-context", Path.Combine("runs", value.Snapshot.RunId, "workflow.json"), value, token);
        }
        catch
        {
            lock (gate)
            {
                context = Replace(value, value.Snapshot.PendingAction == "save" || value.Snapshot.Status is WorkflowStatus.Saved or WorkflowStatus.SaveUncertain
                    ? WorkflowStatus.SaveUncertain : value.Snapshot.InputPending || value.Snapshot.InputMayHaveBeenSent
                    ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked, null,
                    message: "Không ghi được checkpoint; cần khôi phục state trước khi tiếp tục.");
            }
            throw;
        }

        // Audit logging is useful for diagnostics but must not block the
        // workflow after its authoritative state has already been committed.
        try { runEvents.Append(value); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Audit log unavailable: {0}", error.Message); }
    }

    private static WorkflowContext NewContext(InvoiceDocument invoice, string company, string window, string? requestId, string? operationId)
    {
        var id = Guid.NewGuid().ToString("N");
        return new(invoice, new(id, invoice.InvoiceId, invoice.Revision, WorkflowStatus.Ready, company, window, null,
            DateTimeOffset.UtcNow, ProductIdentity.RuntimeVersion, ProductIdentity.StateSchemaVersion,
            RuntimeIdentity: new RuntimeStateIdentity(ProductIdentity.RuntimeVersion, ProductIdentity.SourceRevision, ""), RequestId: requestId, OperationId: operationId), company, window);
    }

    private void ArchiveIncompatibleSession(string reason)
    {
        try
        {
            var path = StateFiles.UnderRoot(stateStore.RootPath, "session.json");
            if (!File.Exists(path)) return;
            var archive = StateFiles.UnderRoot(stateStore.RootPath,
                "session.incompatible-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".json");
            File.Move(path, archive);
            System.Diagnostics.Trace.TraceWarning("Archived incompatible workflow session: {0}; {1}", archive, reason);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not archive incompatible workflow session: {0}", error.Message);
        }
    }

    private static string SessionPath(WorkflowExecutionMode mode)
        => mode == WorkflowExecutionMode.LiveTest ? "session-live-test.json" : "session.json";

    private void MoveSession(string sourceName, string destinationName)
    {
        try
        {
            var source = StateFiles.UnderRoot(stateStore.RootPath, sourceName);
            var destination = StateFiles.UnderRoot(stateStore.RootPath, destinationName);
            if (File.Exists(source) && !File.Exists(destination)) File.Move(source, destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not separate workflow session: {0}", error.Message);
        }
    }

    private static bool HasVoucherCheckpoint(WorkflowStatus status)
        => status >= WorkflowStatus.VoucherCreated && status <= WorkflowStatus.AwaitingSave;

    private static WorkflowContext Replace(WorkflowContext value, WorkflowStatus status, string? nativeFrame,
        WorkflowStatus? pausedFromStatus = null, string? message = null)
    {
        var checkpointConfirmed = status >= WorkflowStatus.Ready && status <= WorkflowStatus.AwaitingSave;
        var terminalConfirmed = status is WorkflowStatus.Saved or WorkflowStatus.Discarded;
        var confirmed = checkpointConfirmed || terminalConfirmed;
        return value with
        {
            Snapshot = value.Snapshot with
            {
                Status = status,
                AppVersion = ProductIdentity.RuntimeVersion,
                UpdatedAt = DateTimeOffset.UtcNow,
                PausedFromStatus = pausedFromStatus,
                LastMessage = message ?? value.Snapshot.LastMessage,
                NativeFrame = string.IsNullOrWhiteSpace(nativeFrame) ? value.Snapshot.NativeFrame : nativeFrame,
                InputPending = confirmed ? false : value.Snapshot.InputPending,
                PendingAction = confirmed ? null : value.Snapshot.PendingAction,
                InputMayHaveBeenSent = confirmed ? false : value.Snapshot.InputMayHaveBeenSent,
                LastErrorCode = confirmed ? null : value.Snapshot.LastErrorCode,
                LastInputDisposition = terminalConfirmed ? InputDisposition.Verified
                    : checkpointConfirmed ? InputDisposition.NotSent : value.Snapshot.LastInputDisposition,
                ErrorChain = confirmed ? null : value.Snapshot.ErrorChain,
                RuntimeIdentity = new RuntimeStateIdentity(ProductIdentity.RuntimeVersion, ProductIdentity.SourceRevision, value.ProfileRevision)
            }
        };
    }
}
