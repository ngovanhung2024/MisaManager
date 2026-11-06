using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed record WorkflowActivity(string RunId, string Field, DateTimeOffset StartedAt);
public sealed record WorkflowStepTiming(string RunId, string Kind, string Step, DateTimeOffset StartedAt,
    long DurationMilliseconds, string Outcome);
public sealed record ItemResolutionDiagnostic(string RunId, int LineIndex, string LineId, string SourceCode,
    string SourceName, string SourceUnit, string CodeSource, string? ResolvedCode = null,
    string? NativeCode = null, string? NativeName = null, string? NativeUnit = null);

public sealed partial class NativeWorkflowGateway : IWorkflowNativeGateway
{
    private readonly MisaWindowDiscovery windows;
    private readonly NativeInputService input;
    private readonly WorkflowProfile salesProfile;
    private readonly WorkflowProfile? purchaseProfile;
    // Profile of the voucher kind being handled; calls are serialized by the caller, so one active profile is enough.
    private WorkflowProfile profile;

    public NativeWorkflowGateway(MisaWindowDiscovery windows, NativeInputService input, WorkflowProfile profile,
        WorkflowProfile? purchaseProfile = null)
    {
        this.windows = windows;
        this.input = input;
        salesProfile = this.profile = profile;
        this.purchaseProfile = purchaseProfile;
        input.UseProfile(profile);
    }

    /// <summary>Switches to the sales or purchase screens for this invoice before anything is read or typed.</summary>
    private void Use(InvoiceDocument invoice) => Use(invoice.IsPurchase()
        ? purchaseProfile ?? throw new AutomationException("PURCHASE_PROFILE_MISSING", "Profile MISA chưa có màn hình Mua hàng hóa, dịch vụ.")
        : salesProfile);

    private void Use(WorkflowProfile next)
    {
        if (ReferenceEquals(next, profile)) return;
        profile = next;
        input.UseProfile(next);
        workflowTabs.Clear();
    }

    private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> checkpointSamples = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<long> workflowSamples = new();
    private volatile WorkflowActivity? activity;
    public WorkflowActivity? Activity => activity;
    // Per-run step timeline for the agent: in-memory only, no extra UIA calls.
    private const int MaxTimelineEntries = 4000;
    private readonly object timelineGate = new();
    private readonly List<WorkflowStepTiming> timeline = [];
    private string? timelineRunId;
    private WorkflowActivity? openStep;
    private long openStepTicks;

    private void Entering(string field)
    {
        var current = activity;
        if (current is null) return;
        SetActivity(current with { Field = field, StartedAt = DateTimeOffset.UtcNow }, "ok");
    }

    private void SetActivity(WorkflowActivity? next, string closingOutcome)
    {
        lock (timelineGate)
        {
            if (openStep is { } open)
            {
                AddTiming(open.RunId, "step", open.Field, open.StartedAt, Elapsed(openStepTicks), closingOutcome);
                openStep = null;
            }
            if (next is not null)
            {
                if (timelineRunId != next.RunId) { timeline.Clear(); timelineRunId = next.RunId; }
                openStep = next;
                openStepTicks = Stopwatch.GetTimestamp();
            }
            activity = next;
        }
    }

    private void AddTiming(string runId, string kind, string step, DateTimeOffset startedAt, long elapsed, string outcome)
    {
        lock (timelineGate)
        {
            if (timelineRunId != runId) { timeline.Clear(); timelineRunId = runId; }
            if (timeline.Count >= MaxTimelineEntries) timeline.RemoveAt(0);
            timeline.Add(new(runId, kind, step, startedAt, elapsed, outcome));
        }
    }

    public (string? RunId, IReadOnlyList<WorkflowStepTiming> Steps) Timeline()
    {
        lock (timelineGate)
        {
            var steps = new List<WorkflowStepTiming>(timeline);
            if (openStep is { } open)
                steps.Add(new(open.RunId, "step", open.Field, open.StartedAt, Elapsed(openStepTicks), "running"));
            return (timelineRunId, steps);
        }
    }

    // Called once when a checkpoint fails, before the runner discards the voucher
    // and the failing dialog/focus state disappears.
    public Func<WorkflowContext, string, Task>? FailureObserver { get; set; }
    /// <summary>Name → item-code database; required when a source line has no item code.</summary>
    public ItemCatalogStore? ItemCatalog { get; set; }
    public ItemResolutionDiagnostic? LastItemResolution { get; private set; }
    /// <summary>Customer/supplier codes; used when the source has no customer code.</summary>
    public PartyCatalogStore? PartyCatalog { get; set; }
    /// <summary>Item groups from MISA; the group code (HH/DV/NVL…) is the prefix of a created item code.</summary>
    public ItemGroupStore? ItemGroups { get; set; }
    /// <summary>Dừng ngay; shared with <see cref="WorkflowRunner.StopSignal"/>.</summary>
    public WorkflowStopSignal? StopSignal { get; set; }
    /// <summary>Menu "Tự thêm mã hàng mới": off means a line without a catalog code stops the invoice before any input.</summary>
    public bool AllowItemCreation { get; set; } = true;
    /// <summary>Menu "Tự thêm khách hàng mới": off means a buyer without a code stops the invoice before any input.</summary>
    public bool AllowPartyCreation { get; set; } = true;
    // Only entry steps stop mid-checkpoint; opening the voucher, creating an item, Cất and Bỏ always finish.
    private volatile bool stopPointsEnabled;

    // Called between whole actions only: the previous field/click/tab switch has fully completed.
    private void StopPoint()
    {
        if (stopPointsEnabled && StopSignal?.IsRequested == true)
            throw new AutomationException(WorkflowRunner.StopRequestedCode,
                "Đã tạm dừng theo yêu cầu (Dừng ngay) sau thao tác vừa hoàn tất; chứng từ chưa Cất.");
    }
    private (string RunId, string Fingerprint, long ValueInputs, DateTimeOffset At, ReconciliationResult Result)? lastReconciliation;

    private async Task NotifyFailureAsync(WorkflowContext context, string step)
    {
        if (FailureObserver is not { } observer) return;
        try { await observer(context, step); }
        catch (Exception error) { Trace.TraceWarning("Failure UI capture failed: " + error.Message); }
    }

    private static long Elapsed(long started) => (long)(1000d * (Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency);

    public NativeWorkflowMetrics Metrics
    {
        get
        {
            var checkpoints = checkpointSamples.ToDictionary(item => item.Key, item =>
            {
                var values = item.Value.ToArray();
                var last = values.Length == 0 ? 0 : values[^1];
                Array.Sort(values);
                return new NativeCheckpointMetric(values.LongLength, last, Percentile(values, .50), Percentile(values, .95));
            }, StringComparer.Ordinal);
            var metrics = input.Metrics;
            var workflows = workflowSamples.ToArray();
            var lastWorkflow = workflows.Length == 0 ? 0 : workflows[^1];
            Array.Sort(workflows);
            return metrics with
            {
                Checkpoints = checkpoints,
                WorkflowCount = workflows.LongLength,
                LastWorkflowMilliseconds = lastWorkflow,
                WorkflowP50Milliseconds = Percentile(workflows, .50),
                WorkflowP95Milliseconds = Percentile(workflows, .95)
            };
        }
    }

    private static long Percentile(long[] values, double percentile)
        => values.Length == 0 ? 0 : values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * percentile) - 1)];

    public void RecordWorkflowDuration(long started)
    {
        var elapsed = (long)(1000d * (Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency);
        workflowSamples.Enqueue(elapsed);
        while (workflowSamples.Count > 64 && workflowSamples.TryDequeue(out _)) { }
    }

    private void RecordCheckpoint(string checkpoint, long started)
    {
        var elapsed = (long)(1000d * (Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency);
        var values = checkpointSamples.GetOrAdd(checkpoint, _ => new ConcurrentQueue<long>());
        values.Enqueue(elapsed);
        while (values.Count > 128 && values.TryDequeue(out _)) { }
    }
    private CultureInfo Culture => CultureInfo.GetCultureInfo(profile.Culture);

    public async Task<WorkflowObservation> ObserveAsync(WorkflowContext context, CancellationToken token)
    {
        var status = context.Snapshot.PausedFromStatus ?? context.Snapshot.Status;
        var inputSequence = input.WorkflowInputSequence;
        try
        {
            Use(context.Invoice);
            BindTarget(context, activate: false);
            if (status is WorkflowStatus.Ready or WorkflowStatus.PreflightPassed or WorkflowStatus.SalesSurface)
            {
                ValidateDocument(context);
                if (await input.WorkflowScopeExistsAsync(BindTarget(context, activate: false), profile.EditorId, token))
                    throw new AutomationException("EXISTING_EDITOR", "An open voucher cannot be recovered as a pre-entry checkpoint.");
                var current = await BindSales(context, token, activate: false);
                return new(true, status, Frame(current), "Đã đọc lại phiên MISA và màn hình " + profile.ModuleCaption + ".");
            }
            if (status is WorkflowStatus.Saved or WorkflowStatus.Discarded)
            {
                await VerifyFinished(context, status == WorkflowStatus.Saved, token);
                return new(true, status, Frame(await input.CaptureAsync(token)), "Đã đối chiếu kết quả chứng từ trong danh sách MISA.");
            }
            if (status >= WorkflowStatus.ItemsVerified && status <= WorkflowStatus.AwaitingSave)
            {
                var result = await ReconcileAsync(context, token);
                if (!result.Matched) throw new AutomationException("READBACK_MISMATCH", "Chứng từ khác checkpoint đã lưu.");
            }
            else
            {
                var frame = await BindEditor(context, token);
                await VerifyVoucherIdentity(context, frame, token);
                if (status == WorkflowStatus.VoucherCreated)
                {
                    var itemFrame = await Tab(profile.ItemTab, token);
                    var items = await input.ReadTableAsync(itemFrame, profile.ItemGridId,
                        [profile.Columns["code"], profile.Columns["name"]], token);
                    if (items.Rows.Any(row => row.Values.Any(value => !string.IsNullOrWhiteSpace(value))))
                        throw new AutomationException("VOUCHER_NOT_EMPTY", "VoucherCreated recovery requires an empty item grid.");
                }
                if (status >= WorkflowStatus.CustomerVerified) await VerifyCustomer(context, token);
                if (status >= WorkflowStatus.DatesVerified) await VerifyDates(context, token);
                if (status >= WorkflowStatus.InvoiceVerified) await VerifyInvoice(context, token);
            }
            var frameResult = Frame(await input.CaptureAsync(token));
            var sent = input.WorkflowInputSequence != inputSequence;
            return new(true, status, frameResult, "Checkpoint đã đối chiếu bằng native readback; không nhập lại.",
                sent ? InputDisposition.Verified : InputDisposition.NotSent, sent);
        }
        catch (Exception error)
        {
            var sent = input.WorkflowInputSequence != inputSequence || error is AutomationException { InputMayHaveBeenSent: true };
            return new(false, sent ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked, "", error.Message,
                sent ? InputDisposition.Uncertain : InputDisposition.NotSent, sent,
                (error as AutomationException)?.Code ?? "WORKFLOW_ERROR", error.ToString());
        }
    }

    public async Task<WorkflowObservation> ExecuteAsync(WorkflowContext context, WorkflowTransition transition,
        Func<WorkflowProgress, CancellationToken, Task> progress, CancellationToken token)
    {
        var sent = false;
        var saveSent = false;
        ReconciliationResult? comparison = null;
        var checkpointStartedAt = DateTimeOffset.UtcNow;
        var checkpointFailed = false;
        SetActivity(new(context.Snapshot.RunId, transition.Action, checkpointStartedAt), "ok");
        var inputSequence = input.WorkflowInputSequence;
        var valueSequence = input.ValueInputSequence;
        var checkpointStarted = Stopwatch.GetTimestamp();
        try
        {
            Use(context.Invoice);
            var stale = transition.Action == WorkflowRunner.ResolveStaleAction;
            var window = BindTarget(context, activate: transition.Action is not ("verify_only" or WorkflowRunner.ResolveStaleAction),
                anyProfileRevision: stale,
                editor: transition.To is not (WorkflowStatus.PreflightPassed or WorkflowStatus.SalesSurface or WorkflowStatus.VoucherCreated));
            ValidateDocument(context);
            stopPointsEnabled = transition.Action != "verify_only"
                && transition.To is >= WorkflowStatus.CustomerVerified and <= WorkflowStatus.AwaitingSave;
            if (stale)
                return await ResolveStaleAsync(context, window, token);
            if (transition.Action == "verify_only")
            {
                if (transition.To is WorkflowStatus.Saved or WorkflowStatus.Discarded)
                {
                    if (transition.To == WorkflowStatus.Saved) await VerifySavedAndCloseEditor(context, token, afterSave: false);
                    await VerifyFinished(context, transition.To == WorkflowStatus.Saved, token);
                    // Closing the editor can invalidate the previous native root;
                    // bind the sales surface again before capturing final proof.
                    var finishedFrame = Frame(await BindSales(context, token, activate: false));
                    sent = input.WorkflowInputSequence != inputSequence;
                    return new(true, transition.To, finishedFrame, "Rebound by native readback; no input replayed.",
                        sent ? InputDisposition.Verified : InputDisposition.NotSent, sent);
                }
                // Rebind verifies the requested checkpoint without replaying input.
                var target = context with
                {
                    Snapshot = context.Snapshot with { Status = transition.To, PausedFromStatus = null }
                };
                var observed = await ObserveAsync(target, token);
                if (!observed.Confirmed || observed.Status != transition.To)
                    return observed with { Confirmed = false,
                        ErrorCode = observed.ErrorCode ?? "RECOVERY_MISMATCH" };
                var recoveredFrame = Frame(await input.CaptureAsync(token));
                sent = input.WorkflowInputSequence != inputSequence;
                return new(true, transition.To, recoveredFrame, "Rebound by native readback; no input replayed.",
                    sent ? InputDisposition.Verified : InputDisposition.NotSent, sent);
            }
            if (transition.To is WorkflowStatus.PreflightPassed or WorkflowStatus.SalesSurface)
            {
                // Fails before any input when the customer code cannot be determined or created.
                RequireCustomer(context);
                RequireItemCodes(context);
                if (await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                    throw new AutomationException("EXISTING_EDITOR", "Có editor chưa gắn với run; cần xử lý trước khi nhập mới.");
                var current = await BindSales(context, token);
                return new(true, transition.To, Frame(current), "Đã xác nhận preflight và màn hình " + profile.ModuleCaption + ".");
            }
            if (transition.To == WorkflowStatus.VoucherCreated)
            {
                if (await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                    throw new AutomationException("EXISTING_EDITOR", "Đang có editor chưa gắn với run; không nhận chứng từ đang mở làm chứng từ mới.");
                // The sales list is not read: already-saved invoices are skipped from history.
                var sales = await BindSales(context, token);
                await input.InvokeWorkflowAsync(sales, profile.OpenVoucher, token);
                sent = true;
                workflowTabs.Clear();
                await WaitEditor(context, true, token);
                await Tab(profile.ReceiptTab, token);
                var voucher = await Field("voucher_number", token);
                if (string.IsNullOrWhiteSpace(voucher.Value)) throw new AutomationException("VOUCHER_ID_MISSING", "Chưa đọc được số chứng từ mới.", true);
                await progress(new("voucher_number", voucher.Value!, Frame(voucher.Frame)), token);
            }
            else
            {
                // Entry steps skip readbacks the final reconciliation repeats (voucher number,
                // dates, invoice fields, line values). Recovery still verifies via ObserveAsync.
                // A failed entry can leave a MISA notice (e.g. "Dữ liệu không có trong danh mục.") over the editor, which
                // disables it; OK on a single-button notice saves nothing, so Bỏ can follow and the batch goes on.
                if (transition.To == WorkflowStatus.Discarded)
                    foreach (var notice in await input.AcknowledgeMessageBoxesAsync(window, token))
                        Trace.TraceInformation("Bấm OK hộp thông báo MISA trước khi Bỏ: " + notice);
                // A confirmed discard may be retried after the editor has already closed;
                // let its fresh register readback decide whether an editor is needed.
                if (transition.To != WorkflowStatus.Discarded
                    || await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                    await BindEditor(context, token);
                switch (transition.To)
                {
                    case WorkflowStatus.CustomerVerified when profile.IsPurchase:
                    {
                        await Tab(profile.ReceiptTab, token);
                        StopPoint();
                        Entering("customer");
                        // The supplier code from the loaded customer/supplier lists selects reliably; R20 does not always
                        // resolve an MST typed into "Nhà cung cấp", so the MST is only the fallback without a list entry.
                        var supplierText = Supplier(context.Invoice)?.Code ?? context.Invoice.Seller.TaxCode.Trim();
                        await Steady(async () => await input.WriteFieldAsync(await input.CaptureAsync(token), profile.Fields["customer"],
                            supplierText, supplierText, NativeValueKind.Exact,
                            profile.Culture, "Enter", token, selectedCustomer: true, verifyAfterWrite: false), token);
                        sent = true;
                        var supplier = await VerifyCustomer(context, token, selecting: true);
                        await progress(new("customer_reference", supplier.Values["customer"].Value!, Frame(supplier.Frame)), token);
                        break;
                    }
                    case WorkflowStatus.CustomerVerified:
                        await Tab(profile.ReceiptTab, token);
                        StopPoint();
                        Entering("customer");
                        var buyerCode = CreatedCustomer(context) ?? BuyerCodes.Resolve(context.Invoice.Buyer, PartyCatalog)?.Code;
                        var typeCode = true;
                        if (buyerCode is null)
                        {
                            buyerCode = await CreateCustomerAsync(context, progress, token);
                            sent = true;
                            context = context with { NativeValues = (context.NativeValues
                                ?? System.Collections.Immutable.ImmutableDictionary<string, string>.Empty).SetItem(CreatedCustomerKey, buyerCode) };
                            // "Cất" in the dialog puts the new customer into the voucher; type the code only when MISA did not.
                            typeCode = !await CustomerFilledAsync(token);
                        }
                        if (typeCode)
                            await Steady(async () => await input.WriteFieldAsync(await input.CaptureAsync(token), profile.Fields["customer"],
                                buyerCode, buyerCode, NativeValueKind.Exact,
                                profile.Culture, "Enter", token, selectedCustomer: true, verifyAfterWrite: false), token);
                        sent = true;
                        var customer = await VerifyCustomer(context, token, selecting: true);
                        await progress(new("customer_reference", customer.Values["customer"].Value!, Frame(customer.Frame)), token);
                        break;
                    case WorkflowStatus.DatesVerified:
                        await Tab(profile.ReceiptTab, token);
                        await WriteDate("posted_date", context.Invoice.PostedDate!.Value, token); sent = true;
                        await WriteDate("voucher_date", context.Invoice.VoucherDate!.Value, token);
                        await Tab(profile.InvoiceTab, token);
                        await WriteDate("issued_date", context.Invoice.Issued!.Value, token);
                        break;
                    case WorkflowStatus.InvoiceVerified:
                        await Tab(profile.InvoiceTab, token);
                        // Template first: choosing "Mẫu số HĐ" (a combo, accepted with Enter) makes the sales voucher clear
                        // "Ký hiệu HĐ" and "Số hóa đơn" (0.2.149), so both are typed after it.
                        if (InvoiceTemplate(context) is { } template)
                        {
                            await Write("template", template, template, NativeValueKind.Exact, "Enter", token); sent = true;
                        }
                        if (context.Invoice.Identity.Series.Length > 0)
                        {
                            await Write("series", context.Invoice.Identity.Series, context.Invoice.Identity.Series, NativeValueKind.Exact, "Tab", token); sent = true;
                        }
                        await Write("number", context.Invoice.Identity.Number, context.Invoice.Identity.Number, NativeValueKind.Exact, "Tab", token); sent = true;
                        break;
                    case WorkflowStatus.ItemsVerified:
                        await EnterLines(context, progress, token);
                        break;
                    case WorkflowStatus.TotalsVerified:
                        var reconciliation = await ReadReconciliationAsync(context, token);
                        comparison = reconciliation;
                        if (!reconciliation.Matched) throw new AutomationException("RECONCILIATION_FAILED", "Đối chiếu dòng/tổng chưa khớp nguồn.");
                        break;
                    case WorkflowStatus.AwaitingSave:
                        // TotalsVerified already read every field and total; reuse it unless values were input since.
                        var settled = await ReadReconciliationAsync(context, token, reuseMatched: true);
                        if (!settled.Matched) throw new AutomationException("RECONCILIATION_FAILED", "Đối chiếu dòng/tổng chưa khớp nguồn.");
                        break;
                    case WorkflowStatus.Saved:
                        var beforeSave = await ReadReconciliationAsync(context, token, reuseMatched: true);
                        comparison = beforeSave;
                        if (!beforeSave.Matched) throw new AutomationException("RECONCILIATION_FAILED", "Chứng từ đã đổi; không Cất.");
                        Entering("save");
                        // R20 exposes txtRefNo only on the receipt tab, including during a save prompt.
                        await VerifyVoucherIdentity(context, await input.CaptureAsync(token), token);
                        var saveFrame = await input.CaptureAsync(token);
                        var editorHandle = await input.BoundScopeHandleAsync(token);
                        var beforeSaveInput = input.WorkflowInputSequence;
                        try { await input.InvokeWorkflowAsync(saveFrame, profile.SaveVoucher, token, leavesScope: true); }
                        catch (AutomationException error) { saveSent = error.InputMayHaveBeenSent; throw; }
                        finally { saveSent |= input.WorkflowInputSequence != beforeSaveInput; }
                        sent = true;
                        string? duplicate = null;
                        var taxPromptAnswered = false;
                        var readRetries = 0;
                        for (var attempt = 0; ; attempt++)
                        {
                            try
                            {
                                var prompts = await AnswerSavePromptsAsync(editorHandle, attempt == 0 ? 800 : 0, token);
                                taxPromptAnswered |= prompts.TaxAnswered;
                                duplicate = prompts.Duplicate;
                                if (duplicate is null)
                                    await VerifySavedAndCloseEditor(context, token, afterSave: true, taxPromptAnswered, editorHandle);
                                break;
                            }
                            catch (Exception error) when (error is not OperationCanceledException && !token.IsCancellationRequested)
                            {
                                // MISA can stop answering UIA for a moment while it posts the voucher. Only reads
                                // are repeated (prompt check, saved proof); Cất itself is never sent again.
                                if (IsTransientRead(error) && readRetries++ < SaveReadRetries)
                                {
                                    await Task.Delay(1000, token);
                                    continue;
                                }
                                // A slow prompt makes the editor modal, so the saved-proof reads fail first
                                // (as AutomationException or a raw UIA/COM error); look for it before failing.
                                var late = await AnswerSavePromptsAsync(editorHandle, 3000, token);
                                taxPromptAnswered |= late.TaxAnswered;
                                duplicate = late.Duplicate;
                                if (duplicate is not null) break;
                                if (!late.AnyAnswered || attempt > 0) throw;
                            }
                        }
                        if (duplicate is not null)
                        {
                            // After "No" the voucher must still be the open, unsaved editor (Cất enabled).
                            await WaitEditor(context, visible: true, token);
                            await input.RequireCommandEnabledAsync(await input.CaptureAsync(token), profile.SaveVoucher, true, token);
                            saveSent = false;
                            throw new AutomationException("INVOICE_ALREADY_EXISTS",
                                $"MISA báo hóa đơn ký hiệu {context.Invoice.Identity.Series}, số {context.Invoice.Identity.Number} đã tồn tại; "
                                + "đã bấm No (không tự tăng số hóa đơn), chứng từ chưa được Cất.");
                        }
                        break;
                    case WorkflowStatus.Discarded:
                        if (context.Snapshot.Status == WorkflowStatus.SaveUncertain
                            || context.Snapshot.PausedFromStatus == WorkflowStatus.SaveUncertain
                            || context.Snapshot.Status == WorkflowStatus.Blocked
                                && context.Snapshot.PausedFromStatus == WorkflowStatus.Blocked
                                && context.Snapshot.LastErrorCode == "EDITOR_NOT_FOUND")
                        {
                            // Absence requires a fresh complete table, never saved
                            // error text or the register's currently visible page.
                            var salesFrame = await BindSales(context, token);
                            var register = await input.ReadCompleteTableAsync(salesFrame, profile.SalesGridId,
                                SalesFinishedColumns(), token);
                            if (register.Rows.Any(row =>
                                Match(Saved(context, "voucher_number"), row[profile.SalesColumns["voucher_number"]], NativeValueKind.Exact)
                                && Match(context.Invoice.Identity.Number, row[profile.SalesColumns["number"]], NativeValueKind.Exact)))
                                throw new AutomationException("DISCARD_SAVE_PROOF_PRESENT", "Register contains the expected voucher and invoice; verify Saved instead of discarding.");
                            // An earlier explicit discard may have closed the editor before
                            // its result was persisted; fresh register absence is sufficient.
                            if (!await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                                break;
                            await BindEditor(context, token);
                            await VerifyVoucherIdentity(context, await input.CaptureAsync(token), token);
                            if (await ShowsUnpostAsync(token))
                                throw new AutomationException("DISCARD_SAVE_PROOF_PRESENT", "Editor shows Bỏ ghi (voucher posted); verify Saved instead of discarding.");
                            await input.RequireCommandEnabledAsync(await input.CaptureAsync(token), profile.SaveVoucher, true, token);
                        }
                        if (await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                        {
                            // A voucher left open by Dừng ngay may have been replaced by the user meanwhile.
                            if (context.Snapshot.Status == WorkflowStatus.Paused
                                && context.NativeValues?.GetValueOrDefault("voucher_number") is { Length: > 0 })
                                await VerifyVoucherIdentity(context, await input.CaptureAsync(token), token);
                            await input.InvokeWorkflowAsync(await input.CaptureAsync(token), profile.DiscardVoucher, token,
                                leavesScope: true, discardChanges: true); sent = true;
                            await WaitEditor(context, false, token);
                        }
                        break;
                    default: throw new AutomationException("TRANSITION_INVALID", "Checkpoint không thuộc profile R20.");
                }
            }
            var finalFrame = Frame(await input.CaptureAsync(token));
            sent |= input.WorkflowInputSequence != inputSequence;
            return new(true, transition.To, finalFrame,
                transition.To == WorkflowStatus.AwaitingSave ? "Đã đối chiếu; chứng từ chưa Cất." : "Đã xác minh checkpoint " + transition.Checkpoint,
                sent ? InputDisposition.Verified : InputDisposition.NotSent, sent, Comparison: comparison);
        }
        catch (AutomationException error) when (error.Code == WorkflowRunner.StopRequestedCode)
        {
            sent |= input.WorkflowInputSequence != inputSequence;
            SetActivity(null, "stopped");
            return new(false, WorkflowStatus.Paused, "", error.Message, sent ? InputDisposition.Verified : InputDisposition.NotSent,
                sent, WorkflowRunner.StopRequestedCode, ValueInputMayHaveBeenSent: input.ValueInputSequence != valueSequence);
        }
        catch (Exception error)
        {
            checkpointFailed = true;
            sent |= input.WorkflowInputSequence != inputSequence || error is AutomationException fault && fault.InputMayHaveBeenSent;
            var valueSent = saveSent || input.ValueInputSequence != valueSequence
                || error is AutomationException { InputMayHaveBeenSent: true };
            var automation = error as AutomationException;
            var failedStep = activity?.Field ?? transition.Action;
            SetActivity(null, "failed");
            if (error is not OperationCanceledException) await NotifyFailureAsync(context, failedStep);
            return new(false, saveSent ? WorkflowStatus.SaveUncertain : transition.To == WorkflowStatus.Saved ? WorkflowStatus.Blocked
                    : sent ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked,
                "", error.Message, sent ? InputDisposition.Uncertain : InputDisposition.NotSent, sent,
                automation?.Code ?? "WORKFLOW_ERROR", error.ToString(), comparison, valueSent);
        }
        finally
        {
            stopPointsEnabled = false;
            SetActivity(null, checkpointFailed ? "failed" : "ok");
            AddTiming(context.Snapshot.RunId, "checkpoint", transition.Checkpoint + " -> " + transition.To, checkpointStartedAt,
                Elapsed(checkpointStarted), checkpointFailed ? "failed" : "ok");
            RecordCheckpoint(transition.To.ToString(), checkpointStarted);
        }
    }

    /// <summary>
    /// "Thêm Khách hàng" from the voucher's customer field (F9 = the + button): organisation or individual layout, code,
    /// name, address and MST / personal ID from the invoice, read back, then the dialog's own "Cất". The code is kept in
    /// the checkpoint before the click; MISA refuses a second customer with the same code, so a lost outcome never
    /// creates a duplicate.
    /// </summary>
    private async Task<string> CreateCustomerAsync(WorkflowContext context, Func<WorkflowProgress, CancellationToken, Task> progress,
        CancellationToken token)
    {
        if (!AllowPartyCreation) throw MissingCustomer(context);
        var dialog = profile.NewCustomer;
        var catalog = PartyCatalog;
        var buyer = context.Invoice.Buyer;
        if (dialog is null || catalog is null || !BuyerCodes.CanCreate(buyer, catalog)) throw MissingCustomer(context);
        var individual = BuyerCodes.IsIndividual(buyer);
        var code = catalog.NewCustomerCode(buyer);
        var name = buyer.Name.Trim();
        var address = buyer.Address.Trim();
        var tax = buyer.TaxCode.Trim();
        // A 12-digit CCCD on a person's invoice goes to "Số CMND"; MISA's MST field is for business tax codes.
        var personalId = individual && BuyerCodes.IsPersonalId(tax) ? tax : "";
        var mst = personalId.Length > 0 ? "" : tax;
        var kind = individual ? dialog.Individual : dialog.Organization;
        var nameField = individual ? dialog.FullName : dialog.Name;
        Entering("customer:new");
        await input.OpenNewCustomerAsync(await input.CaptureAsync(token), profile.Fields["customer"], dialog.OpenKey, token);
        var frame = await input.BindNewCustomerAsync(dialog, token);
        try
        {
            await FillNewCustomerAsync(dialog, frame, kind, nameField, code, name, address, mst, personalId, progress, token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Nothing was saved yet: close the dialog so the runner can discard the voucher underneath.
            try { await input.CancelNewCustomerAsync(dialog.Cancel, CancellationToken.None); }
            catch (Exception cancel) { Trace.TraceWarning("Cancel new-customer dialog failed: " + cancel.Message); }
            throw;
        }
        var handle = await input.SaveNewCustomerAsync(await input.CaptureAsync(token), dialog.Save, token);
        await WaitNewCustomerSavedAsync(handle, code, name, token);
        catalog.AddCreated(new(code, name, mst, address));
        await WaitEditor(context, visible: true, token);
        await Tab(profile.ReceiptTab, token);
        return code;
    }

    // Layout, fields and readback in the bound dialog; no "Cất" yet.
    private async Task FillNewCustomerAsync(CustomerDialogProfile dialog, NativeFrame frame, NativeSelector kind, NativeSelector nameField,
        string code, string name, string address, string mst, string personalId,
        Func<WorkflowProgress, CancellationToken, Task> progress, CancellationToken token)
    {
        if (await input.EnsureNewCustomerKindAsync(frame, kind, nameField, token))
            await NativeWait.UntilAsync(async () => await input.ReadFieldAsync(await input.CaptureAsync(token), nameField, token), _ => true,
                "CUSTOMER_KIND_NOT_SELECTED", $"Hộp Thêm Khách hàng chưa chuyển sang '{kind.Name}' (không thấy ô '{nameField.Label}').", token,
                timeoutMilliseconds: 5000);
        var fields = new List<(string Key, NativeSelector Selector, string Value, NativeValueKind Kind)>
        {
            ("code", dialog.Code, code, NativeValueKind.Exact),
            ("name", nameField, name, NativeValueKind.Text)
        };
        if (address.Length > 0) fields.Add(("address", dialog.Address, address, NativeValueKind.Text));
        if (mst.Length > 0) fields.Add(("tax_code", dialog.TaxCode, mst, NativeValueKind.Exact));
        if (personalId.Length > 0) fields.Add(("id_number", dialog.IdNumber, personalId, NativeValueKind.Exact));
        foreach (var field in fields)
            await input.WriteFieldAsync(await input.CaptureAsync(token), field.Selector, field.Value, field.Value, field.Kind,
                profile.Culture, "Tab", token, verifyAfterWrite: false);
        var selectors = fields.ToDictionary(field => field.Key, field => field.Selector, StringComparer.Ordinal);
        string[] Mismatches(NativeFields values) => fields
            .Where(field => !Match(field.Value, values.Values[field.Key].Value, field.Kind)).Select(field => field.Key).ToArray();
        var readback = await NativeWait.UntilAsync(async () => await input.ReadFieldsAsync(await input.CaptureAsync(token), selectors, token,
                preferDisplayText: true),
            values => Mismatches(values).Length == 0,
            "CUSTOMER_CREATE_READBACK_FAILED", $"Thông tin khách hàng mới '{name}' chưa khớp trước khi Cất.", token, returnLastMismatch: true);
        if (Mismatches(readback) is { Length: > 0 } wrong)
            throw new AutomationException("CUSTOMER_CREATE_READBACK_FAILED",
                $"Hộp Thêm Khách hàng: ô '{wrong[0]}' đang là '{readback.Values[wrong[0]].Value}', khác '{fields.First(field => field.Key == wrong[0]).Value}'.", true);
        await progress(new(CreatedCustomerKey, code, Frame(readback.Frame)), token);
    }

    private async Task WaitNewCustomerSavedAsync(nint dialog, string code, string name, CancellationToken token)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
        while (true)
        {
            var state = await input.NewItemSaveStateAsync(dialog, token);
            if (state.Message is { } message)
            {
                if (message.Contains("đã tồn tại", StringComparison.OrdinalIgnoreCase) || message.Contains("da ton tai", StringComparison.OrdinalIgnoreCase))
                    throw new AutomationException("CUSTOMER_CODE_TAKEN",
                        $"MISA báo mã khách hàng '{code}' đã tồn tại (có thể đã được tạo trước đó). Đóng thông báo, bấm Hủy bỏ trên hộp Thêm Khách hàng, "
                        + "xuất lại danh sách khách hàng từ MISA và nạp lại ở menu ••• rồi chạy lại. Chi tiết: " + message, true);
                throw new AutomationException("CUSTOMER_SAVE_BLOCKED",
                    $"MISA hiện cảnh báo khi Cất khách hàng '{code}' ('{name}'): {message}. Xử lý cảnh báo trong MISA rồi chạy lại; "
                    + "nếu khách hàng đã được tạo, MISA sẽ báo trùng mã thay vì tạo thêm.", true);
            }
            if (!state.DialogOpen) return;
            if (Stopwatch.GetTimestamp() > deadline)
                throw new AutomationException("CUSTOMER_CREATE_UNCERTAIN",
                    $"Đã bấm Cất khách hàng '{code}' nhưng hộp Thêm Khách hàng chưa đóng sau 15 giây. Kiểm tra MISA; "
                    + "lần chạy sau MISA sẽ báo trùng mã nếu khách hàng đã được tạo, không tạo thêm.", true);
            await Task.Delay(100, token);
        }
    }

    // Read-only: MISA fills the customer field asynchronously after the dialog closes.
    private async Task<bool> CustomerFilledAsync(CancellationToken token)
    {
        try
        {
            await NativeWait.UntilAsync(() => Field("customer", token), read => !string.IsNullOrWhiteSpace(read.Value),
                "CUSTOMER_NOT_FILLED", "MISA chưa điền khách hàng mới vào chứng từ.", token, timeoutMilliseconds: 3000);
            return true;
        }
        catch (AutomationException error) when (error.Code == "CUSTOMER_NOT_FILLED" && !error.InputMayHaveBeenSent) { return false; }
    }

    private ItemCatalogStore RequireCatalog()
        => ItemCatalog is { Count: > 0 } catalog ? catalog
            : throw new AutomationException("ITEM_CATALOG_MISSING", "Chưa nạp database mã hàng. Bấm 'Mã hàng' và chọn file danh mục vật tư, hàng hóa.");

    /// <summary>With item creation switched off, every line must already resolve to a code; checked before any input.</summary>
    private void RequireItemCodes(WorkflowContext context)
    {
        if (AllowItemCreation) return;
        var catalog = RequireCatalog();
        var missing = context.Invoice.Lines
            .Where(line => line.ItemCode.Trim().Length == 0
                && string.IsNullOrWhiteSpace(context.NativeValues?.GetValueOrDefault("line:" + line.LineId))
                && catalog.FindPending(line.Name, line.Unit) is null
                && ResolveCatalogName(catalog, line, context.AllowKgToTon) is null)
            .Select(line => $"'{line.Name}' ({line.Unit})").ToArray();
        if (missing.Length > 0)
            throw new AutomationException(ItemCatalogStore.MissingCode, $"Hóa đơn {context.Invoice.Identity.Number}: "
                + $"{string.Join(", ", missing)} chưa có trong danh mục mã hàng và \"Tự thêm mã hàng mới\" đang tắt. "
                + "Thêm mã trong MISA rồi nạp lại danh mục, hoặc bật lại ở menu •••, rồi chạy lại.");
    }

    private async Task<string> CreateMissingItemAsync(WorkflowContext context, InvoiceLine line, int row,
        NativeCell codeCell, CancellationToken token)
    {
        // Preflight already stops such invoices; never create when the user switched it off.
        if (!AllowItemCreation)
            throw new AutomationException(ItemCatalogStore.MissingCode, $"Dòng {row + 1}: '{line.Name}' chưa có mã và \"Tự thêm mã hàng mới\" đang tắt.");
        if (profile.ItemNewCode is null || profile.ItemNewName is null || profile.ItemNewUnit is null
            || profile.ItemNewStock is null || profile.ItemNewTaxRate is null || profile.ItemNewSave is null)
            throw new AutomationException("ITEM_MAPPING_REQUIRED", "Profile chưa khai báo native create-item capability.");
        var catalog = RequireCatalog();
        // MISA's unit list uses its own spelling ("kg", "Chai 90 ml"); reuse it when the catalog knows it.
        var unit = catalog.CanonicalUnit(line.Unit)
            ?? (string.Equals(line.Unit.Trim(), "Kg", StringComparison.OrdinalIgnoreCase) ? "kg" : line.Unit.Trim());
        var taxKey = NativeComparison.TaxRateKey(line.TaxRate, CultureInfo.InvariantCulture)
            ?? throw new AutomationException("ITEM_TAX_NOT_SET", $"Dòng {row + 1}: thiếu thuế suất để tạo mã hàng '{line.Name}'.");
        // "5", "5%" and "5,0000" all select MISA's "5%" option; KCT/KKKNT stay textual.
        var tax = taxKey.All(character => char.IsDigit(character) || character == '.') ? taxKey + "%" : line.TaxRate.Trim();
        var stock = profile.DefaultItemStock;
        Entering("line:" + line.LineId + ":new_item");
        await input.OpenNewItemAsync(await input.CaptureAsync(token), codeCell, token);
        var newItem = await input.BindNewItemAsync(profile.ItemNewCode, profile.ItemNewName, profile.ItemNewSave, token);
        // MISA pre-fills its next code from the live database, which also covers codes
        // created by other users or sessions since the local catalog was exported.
        var proposed = (await input.ReadFieldAsync(newItem, profile.ItemNewCode, token)).Value?.Trim();
        var code = catalog.NextCode(ItemGroups?.CodePrefix(line.Name, line.Unit) ?? "HH_", proposed);
        // Unit, warehouse and tax are ComboBoxes: focus must be verified before any key is sent,
        // otherwise a Tab could commit text into another field of the dialog.
        if (!string.Equals(proposed, code, StringComparison.Ordinal))
            await input.WriteFieldAsync(await input.CaptureAsync(token), profile.ItemNewCode, code, code, NativeValueKind.Exact,
                profile.Culture, "Tab", token, verifyAfterWrite: false);
        await input.WriteFieldAsync(await input.CaptureAsync(token), profile.ItemNewName,
            line.Name.Trim(), line.Name, NativeValueKind.Text, profile.Culture, "Tab", token, verifyAfterWrite: false);
        await input.WriteFieldAsync(await input.CaptureAsync(token), profile.ItemNewUnit,
            unit, line.Unit, NativeValueKind.Text, profile.Culture, "Tab", token, verifyAfterWrite: false);
        await input.WriteFieldAsync(await input.CaptureAsync(token), profile.ItemNewStock,
            stock, stock, NativeValueKind.Exact, profile.Culture, "Tab", token, verifyAfterWrite: false);
        await input.WriteFieldAsync(await input.CaptureAsync(token), profile.ItemNewTaxRate,
            tax, tax, NativeValueKind.TaxRate, profile.Culture, "Tab", token, verifyAfterWrite: false);
        var required = new Dictionary<string, NativeSelector>(StringComparer.Ordinal)
        {
            ["code"] = profile.ItemNewCode,
            ["name"] = profile.ItemNewName,
            ["unit"] = profile.ItemNewUnit,
            ["stock"] = profile.ItemNewStock,
            ["tax"] = profile.ItemNewTaxRate
        };
        string[] Mismatches(NativeFields values)
        {
            var failed = new List<string>();
            if (!Match(code, values.Values["code"].Value, NativeValueKind.Exact)) failed.Add("code");
            if (!CatalogNameMatch(line.Name, values.Values["name"].Value)) failed.Add("name");
            if (!Match(line.Unit, values.Values["unit"].Value, NativeValueKind.Text)) failed.Add("unit");
            if (!Match(stock, values.Values["stock"].Value, NativeValueKind.Exact)
                && !Match(stock, values.Values["stock"].Value, NativeValueKind.Text)) failed.Add("stock");
            if (!Match(tax, values.Values["tax"].Value, NativeValueKind.TaxRate)) failed.Add("tax");
            return [.. failed];
        }
        var readback = await NativeWait.UntilAsync(async () => await input.ReadFieldsAsync(await input.CaptureAsync(token), required, token,
                preferDisplayText: true),
            values => Mismatches(values).Length == 0,
            "ITEM_CREATE_READBACK_FAILED", $"Dòng {row + 1}: thông tin mã hàng mới chưa khớp trước khi Cất.", token,
            returnLastMismatch: true);
        if (Mismatches(readback) is { Length: > 0 } wrong)
            throw NewItemFieldError(row, wrong[0], code, line, unit, stock, tax, readback.Values[wrong[0]].Value);
        // Durable before the click: if the outcome is lost, a rerun looks this code up instead of creating another.
        catalog.BeginPending(new PendingItemCreation(code, line.Name.Trim(), line.Unit.Trim(), DateTimeOffset.UtcNow,
            context.Snapshot.RunId));
        var dialog = await input.SaveNewItemAsync(await input.CaptureAsync(token), profile.ItemNewSave, token);
        await WaitNewItemSavedAsync(catalog, dialog, code, line, row, token);
        await WaitEditor(context, visible: true, token);
        await Tab(profile.ItemTab, token);
        return code;
    }

    private async Task WaitNewItemSavedAsync(ItemCatalogStore catalog, nint dialog, string code, InvoiceLine line, int row,
        CancellationToken token)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
        while (true)
        {
            var state = await input.NewItemSaveStateAsync(dialog, token);
            if (state.Message is { } message)
            {
                if (message.Contains("đã tồn tại", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("da ton tai", StringComparison.OrdinalIgnoreCase))
                {
                    // MISA refused the save, so nothing was created under this code.
                    catalog.CancelPending(code);
                    throw new AutomationException("ITEM_CODE_TAKEN",
                        $"Dòng {row + 1}: MISA báo mã '{code}' đã tồn tại (có thể vừa được người/phiên khác tạo). "
                        + "Đóng thông báo, bấm Hủy bỏ trên hộp Thêm vật tư rồi chạy lại; tool sẽ cấp mã kế tiếp. Chi tiết: " + message, true);
                }
                throw new AutomationException("ITEM_SAVE_BLOCKED",
                    $"Dòng {row + 1}: MISA hiện cảnh báo khi Cất mã '{code}' ('{line.Name}'): {message}. "
                    + "Xử lý cảnh báo trong MISA. Mã này được ghi nhận là 'đang tạo'; lần chạy sau tool sẽ tìm lại mã này trước, không tạo trùng.", true);
            }
            if (!state.DialogOpen) return;
            if (Stopwatch.GetTimestamp() > deadline)
                throw new AutomationException("ITEM_CREATE_UNCERTAIN",
                    $"Dòng {row + 1}: đã bấm Cất mã '{code}' nhưng hộp Thêm vật tư chưa đóng sau 15 giây. "
                    + "Kiểm tra MISA; lần chạy sau tool sẽ tìm lại mã này trước, không tạo trùng.", true);
            await Task.Delay(100, token);
        }
    }

    private static AutomationException NewItemFieldError(int row, string field, string code, InvoiceLine line,
        string unit, string stock, string tax, string? actual)
        => field switch
        {
            "unit" => new("ITEM_UNIT_NOT_REGISTERED",
                $"Dòng {row + 1}: ĐVT '{unit}' chưa có trong danh mục Đơn vị tính của MISA (ô ĐVT đang là '{actual}'). "
                + "Thêm ĐVT này trong MISA (nút + cạnh ĐVT chính) hoặc sửa ĐVT trong file Excel, rồi bấm Hủy bỏ trên hộp Thêm vật tư và chạy lại.", true),
            "stock" => new("ITEM_STOCK_NOT_FOUND",
                $"Dòng {row + 1}: kho ngầm định '{stock}' không có hoặc đã đổi tên trong MISA (ô Kho đang là '{actual}'). "
                + "Kiểm tra danh mục Kho, cập nhật itemNewStockCode trong profile nếu cần, rồi bấm Hủy bỏ trên hộp Thêm vật tư và chạy lại.", true),
            "tax" => new("ITEM_TAX_NOT_SET",
                $"Dòng {row + 1}: không chọn được thuế suất GTGT '{tax}' (ô đang là '{actual}'). "
                + "Kiểm tra thuế suất trong file Excel, bấm Hủy bỏ trên hộp Thêm vật tư rồi chạy lại.", true),
            "name" => new("ITEM_CREATE_READBACK_FAILED",
                $"Dòng {row + 1}: tên hàng trong hộp Thêm vật tư là '{actual}', khác '{line.Name}'. Bấm Hủy bỏ rồi chạy lại.", true),
            _ => new("ITEM_CREATE_READBACK_FAILED",
                $"Dòng {row + 1}: mã hàng trong hộp Thêm vật tư là '{actual}', khác mã cấp '{code}'. Bấm Hủy bỏ rồi chạy lại.", true)
        };

    /// <summary>
    /// Types the exact code into the empty "Mã hàng" cell and presses Enter, which picks the first
    /// dropdown row. The readback inside WriteCellAsync proves that row is this exact code.
    /// </summary>
    private async Task SelectItemCodeAsync(int row, string code, bool afterCreate, CancellationToken token)
    {
        var cell = new NativeCell(profile.ItemGridId, row, profile.Columns["code"], profile.Columns["code"], "");
        var current = afterCreate ? (await input.ReadCellAsync(await input.CaptureAsync(token), cell, token)).Value?.Trim() ?? "" : "";
        if (current.Length > 0)
        {
            // MISA can fill the row itself after creating an item from the cell.
            if (Match(code, current, NativeValueKind.Exact)) return;
            throw new AutomationException("GRID_ROW_CHANGED",
                $"Dòng {row + 1}: ô Mã hàng đang là '{current}', khác mã '{code}'; không ghi đè.", true);
        }
        await Steady(async () => await input.WriteCellAsync(await input.CaptureAsync(token), cell, code, code, NativeValueKind.Exact,
            profile.Culture, token, commitKey: "Enter"), token);
    }

    private static AutomationException PendingUnresolved(int row, PendingItemCreation pending, Exception error)
        => new("ITEM_CREATE_UNCERTAIN",
            $"Dòng {row + 1}: lần chạy trước đã bấm Cất mã '{pending.Code}' cho '{pending.Name}' lúc {pending.StartedAt.ToLocalTime():dd/MM HH:mm} "
            + "nhưng MISA chưa xác nhận mã này. Kiểm tra danh mục Vật tư trong MISA rồi xuất và nạp lại file mã hàng; "
            + "tool không tự cấp mã mới để tránh tạo trùng. Chi tiết: " + error.Message, true, error);

    private (string Unit, decimal Quantity, decimal Price) ExpectedLine(WorkflowContext context, InvoiceLine line)
    {
        var conversion = context.NativeValues?.GetValueOrDefault("conversion:" + line.LineId) ?? "";
        if (conversion.Length == 0) return (line.Unit, line.Quantity!.Value, line.UnitPrice.GetValueOrDefault());
        if (!context.AllowKgToTon || conversion != "kg_to_ton:1000"
            || !Match(line.Unit, "Kg", NativeValueKind.Text)
            || !Match(context.NativeValues?.GetValueOrDefault("unit:" + line.LineId) ?? "", "T\u1ea5n", NativeValueKind.Text)
            || !IsUnitAbsenceEvidence(context.NativeValues?.GetValueOrDefault("unit_absence:" + line.LineId))
            || string.IsNullOrWhiteSpace(context.NativeValues?.GetValueOrDefault("line:" + line.LineId)))
            throw new AutomationException("UNIT_CONVERSION_PROVENANCE_INVALID", "Converted line has no valid authorization and registered-unit absence evidence.");
        return ("T\u1ea5n", line.Quantity!.Value / 1000m, line.UnitPrice.GetValueOrDefault() * 1000m);
    }

    private static bool IsUnitAbsenceEvidence(string? evidence)
        => evidence is not null && (evidence.StartsWith("UIA3.CompleteRegisteredUnitList:", StringComparison.Ordinal)
            || evidence.StartsWith("ItemCatalog:", StringComparison.Ordinal));

    private static string? ResolveCatalogName(ItemCatalogStore catalog, InvoiceLine line, bool allowKgToTon)
        => catalog.Find(line.Name, line.Unit, allowKgToTon)?.Code;

    private async Task EnterLines(WorkflowContext context, Func<WorkflowProgress, CancellationToken, Task> progress, CancellationToken token)
    {
        var codes = new List<string>();
        await Tab(profile.ItemTab, token);
        for (var row = 0; row < context.Invoice.Lines.Length; row++)
        {
            var line = context.Invoice.Lines[row];
            StopPoint();
            LastItemResolution = new(context.Snapshot.RunId, row, line.LineId, line.ItemCode, line.Name, line.Unit,
                line.ItemCode.Trim().Length > 0 ? "invoice_excel" : "unresolved");
            Entering("line:" + line.LineId + ":code");
            var code = line.ItemCode.Trim();
            if (code.Length == 0)
            {
                code = context.NativeValues?.GetValueOrDefault("line:" + line.LineId)?.Trim() ?? "";
                if (code.Length > 0) LastItemResolution = LastItemResolution with { CodeSource = "checkpoint_readback" };
            }
            var codeCell = new NativeCell(profile.ItemGridId, row, profile.Columns["code"], profile.Columns["code"], "");
            var existingCode = (await NativeWait.UntilAsync(() => Steady(async () => await input.ReadCellAsync(
                await input.CaptureAsync(token), codeCell, token, waitForCode: false), token),
                read => read.Value is not null, "GRID_ROW_NOT_READY", $"Row {row + 1} is not ready.", token)).Value!.Trim();
            PendingItemCreation? pending = null;
            var resolvedFromCatalog = false;
            if (code.Length == 0)
            {
                var catalog = RequireCatalog();
                // A previous run may have clicked "Cất" without learning the result: reuse that code, never create another.
                if (catalog.FindPending(line.Name, line.Unit) is { } found)
                {
                    (pending, code) = (found, found.Code);
                    LastItemResolution = LastItemResolution with { CodeSource = "pending_creation" };
                }
                // Use the imported mapping; F3 columns vary across MISA builds.
                if (pending is null)
                {
                    code = ResolveCatalogName(catalog, line, context.AllowKgToTon) ?? "";
                    LastItemResolution = LastItemResolution with
                    {
                        CodeSource = code.Length > 0 ? catalog.CodeOrigin(code) : "not_found_create_F9"
                    };
                }
                resolvedFromCatalog = pending is null && code.Length > 0 && existingCode.Length == 0;
            }
            LastItemResolution = LastItemResolution with { ResolvedCode = code.Length > 0 ? code : null, NativeCode = existingCode };
            if (existingCode.Length > 0)
            {
                if (!Match(code, existingCode, NativeValueKind.Exact))
                    throw new AutomationException("GRID_ROW_CHANGED",
                        $"Row {row + 1}: native code '{existingCode}' differs from source code '{code}'; refusing to overwrite.", true);
            }
            else
            {
                var created = code.Length == 0;
                if (created)
                {
                    code = await CreateMissingItemAsync(context, line, row, codeCell, token);
                    LastItemResolution = LastItemResolution with { ResolvedCode = code };
                }
                Entering("line:" + line.LineId + ":code");
                try { await SelectItemCodeAsync(row, code, created, token); }
                catch (Exception error) when (pending is not null && error is not OperationCanceledException)
                {
                    throw PendingUnresolved(row, pending, error);
                }
            }
            LastItemResolution = LastItemResolution with { NativeCode = code };
            var boundRow = new NativeCell(profile.ItemGridId, row, profile.Columns["code"], profile.Columns["code"], code);
            NativeFields metadata;
            try
            {
                metadata = await NativeWait.UntilAsync(async () =>
                    {
                        var read = await Steady(async () => await input.ReadRowAsync(await input.CaptureAsync(token), boundRow,
                            MetadataColumns(), token), token);
                        LastItemResolution = LastItemResolution with
                        {
                            NativeName = read.Values[profile.Columns["name"]].Value,
                            NativeUnit = read.Values[profile.Columns["unit"]].Value
                        };
                        return read;
                    },
                    actual => CatalogNameMatch(line.Name, actual.Values[profile.Columns["name"]].Value),
                    "READBACK_MISMATCH", $"Dòng {row + 1}: tên hàng MISA khác tên trong file.", token,
                    returnLastMismatch: resolvedFromCatalog);
            }
            catch (Exception error) when (pending is not null && error is not OperationCanceledException)
            {
                throw PendingUnresolved(row, pending, error);
            }
            if (resolvedFromCatalog && !CatalogNameMatch(line.Name, metadata.Values[profile.Columns["name"]].Value))
            {
                throw new AutomationException("ITEM_CATALOG_STALE",
                    $"Dòng {row + 1}: mã '{code}' được tra từ danh mục cục bộ cho '{line.Name}', "
                    + $"nhưng MISA đọc lại tên '{metadata.Values[profile.Columns["name"]].Value}'. "
                    + "Hãy xuất lại danh mục vật tư từ MISA và nạp lại file mã hàng; tool không mở Tìm nhanh/kính lúp và không tự đổi mã.", true);
            }
            // Purchases: MISA fills Kho/TK Kho/TK công nợ from the item itself; they are neither typed nor read.
            var expectedAccount = profile.IsPurchase ? null : metadata.Values[profile.Columns["debit_account"]].Value;
            if (!profile.IsPurchase && (string.IsNullOrWhiteSpace(expectedAccount) || !expectedAccount.All(char.IsDigit)))
                throw new AutomationException("ACCOUNT_READBACK_INVALID", $"Row {row + 1}: native account is invalid.");
            var converted = !string.IsNullOrWhiteSpace(context.NativeValues?.GetValueOrDefault("conversion:" + line.LineId));
            var expected = ExpectedLine(context, line);
            var nativeUnit = metadata.Values[profile.Columns["unit"]].Value;
            if (!Match(expected.Unit, nativeUnit, NativeValueKind.Text))
            {
                if (converted)
                    throw new AutomationException("ITEM_UNIT_MISMATCH", $"Row {row + 1}: native unit differs from the recorded conversion.");
                var kgLine = context.AllowKgToTon && Match(line.Unit, "Kg", NativeValueKind.Text);
                // The MISA export lists every registered unit of the item. When it proves Kg is absent, opening the
                // unit list only to see that (2–3 s per line) is skipped and the line goes straight to the Tấn unit.
                var catalogProof = kgLine && Match("T\u1ea5n", nativeUnit, NativeValueKind.Text)
                    ? ItemCatalog?.UnitAbsenceProof(code, line.Unit) : null;
                if (catalogProof is null) StopPoint();
                var selected = catalogProof is not null ? null : await Steady(async () => await input.SelectCellOptionAsync(
                    await input.CaptureAsync(token), CellTarget(row, code, "unit"), line.Unit, profile.Culture, token,
                    allowMissing: kgLine), token);
                if (catalogProof is not null || selected!.Status == "option_absent")
                {
                    if (!kgLine || !Match(selected?.Value ?? nativeUnit ?? "", "T\u1ea5n", NativeValueKind.Text))
                        throw new AutomationException("ITEM_UNIT_MISMATCH", $"Row {row + 1}: no authorized conversion for the native unit.");
                    expected = ("T\u1ea5n", line.Quantity!.Value / 1000m, line.UnitPrice.GetValueOrDefault() * 1000m);
                    // Commit absence evidence and exact code before any numeric
                    // write so partial-row recovery cannot apply the factor twice.
                    await progress(new("line:" + line.LineId, code, Frame(selected?.Frame ?? metadata.Frame), row,
                        new Dictionary<string, string>
                        {
                            ["conversion:" + line.LineId] = "kg_to_ton:1000",
                            ["unit:" + line.LineId] = expected.Unit,
                            ["unit_absence:" + line.LineId] = catalogProof ?? selected!.Source + ":" + selected.Reason
                        }), token);
                }
            }
            // Only quantity and amount are typed; MISA derives the unit price, so
            // rounded source prices (e.g. 13333.33 × 150 ≠ 2000000) cannot mismatch.
            var writes = new[]
            {
                new NativeCellWrite(profile.Columns["quantity"], expected.Quantity.ToString(Culture)),
                new NativeCellWrite(profile.Columns["amount"], line.AmountBeforeTax!.Value.ToString(Culture))
            };
            StopPoint();
            var verifiedRow = await Steady(async () => await input.WriteRowAsync(await input.CaptureAsync(token),
                new NativeCell(profile.ItemGridId, row, profile.Columns["code"], profile.Columns["code"], code),
                writes, [profile.Columns["quantity"], profile.Columns["amount"]],
                values => Match(Decimal(expected.Quantity), values.Values[profile.Columns["quantity"]].Value, NativeValueKind.Decimal)
                    && Match(Decimal(line.AmountBeforeTax), values.Values[profile.Columns["amount"]].Value, NativeValueKind.Decimal),
                token, column => Entering("line:" + line.LineId + ":"
                    + profile.Columns.Single(pair => pair.Value == column).Key)), token);
            if (pending is not null) RequireCatalog().ConfirmPending(pending.Code);
            else if (ItemCatalog?.FindPending(line.Name, line.Unit) is { } createdPending
                && Match(code, createdPending.Code, NativeValueKind.Exact))
                RequireCatalog().ConfirmPending(code);
            codes.Add(code);
            await progress(new("line:" + line.LineId, code, Frame(verifiedRow.Frame), row,
                expectedAccount is null ? null : new Dictionary<string, string> { ["account:" + line.LineId] = expectedAccount }), token);
        }
        StopPoint();
        await Tab(profile.TaxTab, token);
        // Items usually carry their VAT rate already; one grid read avoids a write per line.
        var taxGrid = await Steady(async () => await input.ReadTableAsync(await input.CaptureAsync(token), profile.ItemGridId,
            [profile.Columns["code"], profile.Columns["tax_rate"]], token), token);
        for (var row = 0; row < context.Invoice.Lines.Length; row++)
        {
            var line = context.Invoice.Lines[row];
            var taxKey = NativeComparison.TaxRateKey(line.TaxRate, CultureInfo.InvariantCulture);
            var tax = taxKey is not null && decimal.TryParse(taxKey, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate)
                ? rate.ToString(Culture) : line.TaxRate.TrimEnd('%').Trim();
            var nativeRate = row < taxGrid.Rows.Count ? taxGrid.Rows[row].GetValueOrDefault(profile.Columns["tax_rate"]) : null;
            if (row < taxGrid.Rows.Count && Match(codes[row], taxGrid.Rows[row].GetValueOrDefault(profile.Columns["code"]), NativeValueKind.Exact)
                && Match(tax, nativeRate, NativeValueKind.TaxRate))
                continue;
            StopPoint();
            Entering("line:" + line.LineId + ":tax_rate");
            await Steady(async () => await input.WriteCellAsync(await input.CaptureAsync(token), CellTarget(row, codes[row], "tax_rate"),
                tax, tax, NativeValueKind.TaxRate, profile.Culture, token), token);
        }
        if (profile.IsPurchase) await EnterPurchaseTaxAmountsAsync(context, codes, token);
    }

    private string[] MetadataColumns() => profile.IsPurchase
        ? [profile.Columns["name"], profile.Columns["unit"]]
        : [profile.Columns["name"], profile.Columns["unit"], profile.Columns["debit_account"]];

    // "Mẫu số HĐ" of the source, typed on both vouchers when the profile declares the field (MISA leaves it empty).
    private string? InvoiceTemplate(WorkflowContext context)
        => profile.Fields.ContainsKey("template") && context.Invoice.Template.Trim() is { Length: > 0 } template ? template : null;

    /// <summary>
    /// The portal states tax per rate; the importer spreads its rounding over the lines. MISA computes amount × rate per
    /// line, so a line whose tax differs gets the source tax typed on the "2. Thuế" tab (Cất's tax prompt keeps it).
    /// </summary>
    private async Task EnterPurchaseTaxAmountsAsync(WorkflowContext context, IReadOnlyList<string> codes, CancellationToken token)
    {
        var grid = await Steady(async () => await input.ReadTableAsync(await input.CaptureAsync(token), profile.ItemGridId,
            [profile.Columns["code"], profile.Columns["tax_amount"]], token), token);
        for (var row = 0; row < context.Invoice.Lines.Length; row++)
        {
            var line = context.Invoice.Lines[row];
            if (line.TaxAmount is not { } wanted) continue;
            var native = row < grid.Rows.Count ? grid.Rows[row].GetValueOrDefault(profile.Columns["tax_amount"]) : null;
            if (row < grid.Rows.Count && Match(codes[row], grid.Rows[row].GetValueOrDefault(profile.Columns["code"]), NativeValueKind.Exact)
                && Match(Decimal(wanted), native, NativeValueKind.Decimal))
                continue;
            StopPoint();
            Entering("line:" + line.LineId + ":tax_amount");
            await Steady(async () => { await WriteCell(row, codes[row], "tax_amount", wanted, token); return true; }, token);
        }
    }

    public async Task<ReconciliationResult> ReconcileAsync(WorkflowContext context, CancellationToken token)
    {
        SetActivity(new(context.Snapshot.RunId, "reconcile", DateTimeOffset.UtcNow), "ok");
        var failed = true;
        try
        {
            Use(context.Invoice);
            await BindEditor(context, token);
            // In a live run this follows TotalsVerified directly; reuse that read when nothing was typed since.
            var result = await ReadReconciliationAsync(context, token,
                reuseMatched: context.Snapshot.Status == WorkflowStatus.AwaitingSave);
            failed = false;
            return result;
        }
        finally { SetActivity(null, failed ? "failed" : "ok"); }
    }

    private async Task<ReconciliationResult> ReadReconciliationAsync(WorkflowContext context, CancellationToken token,
        bool reuseMatched = false)
    {
        // A full read switches 4 tabs and walks both grids. A matched read is reused only for
        // the same run/invoice, within 30 s, and when no value input was sent since.
        if (reuseMatched && lastReconciliation is { } last && last.Result.Matched
            && last.RunId == context.Snapshot.RunId && last.Fingerprint == context.Invoice.Fingerprint
            && last.ValueInputs == input.ValueInputSequence && DateTimeOffset.UtcNow - last.At < TimeSpan.FromSeconds(30))
            return last.Result;
        Entering("reconcile");
        // One grid read may itself wait 10 s for the rows to settle; a pass that ends in a read failure (HĐ 82: element
        // gone after the Thuế tab) gets fresh passes for longer, while a real mismatch is still reported after 10 s.
        var result = await NativeWait.UntilAsync(() => ReadReconciliationPassAsync(context, token), result => result.Matched,
            "RECONCILIATION_PENDING", "MISA values have not settled.", token, returnLastMismatch: true,
            readFailureTimeoutMilliseconds: 30000);
        lastReconciliation = (context.Snapshot.RunId, context.Invoice.Fingerprint, input.ValueInputSequence, DateTimeOffset.UtcNow, result);
        return result;
    }

    private async Task<ReconciliationResult> ReadReconciliationPassAsync(WorkflowContext context, CancellationToken token)
    {
        // A click elsewhere (MISA's list, this tool) leaves the voucher inactive and its grid unreadable on R20: each pass,
        // including the retries of a failed one, reads from an active voucher window. Best effort: the read decides.
        try { await input.ActivateWorkflowScopeAsync(await input.CaptureAsync(token), token); }
        catch (AutomationException error) when (error.Code is "FOCUS_FAILED" or "KEY_TARGET_INVALID")
        {
            Trace.TraceInformation("Không đưa được chứng từ lên trước khi đối chiếu: " + error.Message);
        }
        var values = new List<ValueComparison>();
        async Task<NativeFrame> CompareFields(string tab, params (string Key, string Expected, NativeValueKind Kind)[] fields)
        {
            StopPoint();
            await Tab(tab, token);
            var actual = await NativeWait.UntilAsync(() => Steady(async () => await input.ReadFieldsAsync(
                await input.CaptureAsync(token),
                fields.ToDictionary(field => field.Key, field => profile.Fields[field.Key]), token), token),
                _ => true, "TARGET_NOT_FOUND", "Các trường chưa sẵn sàng sau khi chuyển tab.", token);
            foreach (var field in fields)
            {
                var read = actual.Values[field.Key];
                values.Add(new(field.Key, field.Expected, read.Value, Match(field.Expected, read.Value, field.Kind)
                    || profile.IsPurchase && field.Key == "customer_name" && SupplierMatches(context, field.Key, field.Expected, read.Value, false), read.Source));
            }
            return actual.Frame;
        }
        // Tab order: tax grid (selected after entry) → item grid + totals → receipt → invoice.
        foreach (var taxTab in new[] { true, false })
        {
            StopPoint();
            await Tab(taxTab ? profile.TaxTab : profile.ItemTab, token);
            var keys = taxTab ? new[] { "code", "name", "tax_rate", "tax_amount" }
                : profile.IsPurchase ? new[] { "code", "name", "unit", "quantity", "amount", "unit_price" }
                : new[] { "code", "name", "unit", "debit_account", "quantity", "amount", "unit_price" };
            NativeTable table;
            try
            {
                table = await Steady(async () => await input.ReadCompleteTableAsync(await input.CaptureAsync(token), profile.ItemGridId,
                    keys.Select(key => profile.Columns[key]).ToArray(), token), token);
            }
            catch (AutomationException error) when (!error.InputMayHaveBeenSent)
            {
                throw new AutomationException(error.Code, $"Tab '{(taxTab ? profile.TaxTab : profile.ItemTab)}': {error.Message}", false, error);
            }
            var expectedLines = context.Invoice.Lines
                .Select(line => (Line: line, Code: line.ItemCode.Trim().Length > 0
                    ? line.ItemCode.Trim() : Saved(context, "line:" + line.LineId)))
                .ToArray();
            var nativeRows = table.Rows.Select((row, index) =>
                (Row: row, Index: index, Code: row.GetValueOrDefault(profile.Columns["code"])?.Trim() ?? ""))
                .Where(item => item.Code.Length > 0).ToArray();
            var nativeByCode = nativeRows.GroupBy(item => item.Code, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var expectedOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (line, code) in expectedLines)
            {
                var expectedLine = ExpectedLine(context, line);
                var occurrence = expectedOccurrences.GetValueOrDefault(code, 0);
                expectedOccurrences[code] = occurrence + 1;
                var candidates = nativeByCode.GetValueOrDefault(code);
                var native = candidates is not null && occurrence < candidates.Length
                    ? candidates[occurrence] : default;
                var expected = taxTab
                    ? new[] { ("code", code, NativeValueKind.Exact), ("name", line.Name, NativeValueKind.Text),
                        ("tax_rate", line.TaxRate, NativeValueKind.TaxRate), ("tax_amount", Decimal(line.TaxAmount), NativeValueKind.Decimal) }
                    : new[] { ("code", code, NativeValueKind.Exact), ("name", line.Name, NativeValueKind.Text), ("unit", expectedLine.Unit, NativeValueKind.Text),
                        ("debit_account", context.NativeValues?.GetValueOrDefault("account:" + line.LineId) ?? "<missing verified account>", NativeValueKind.Exact),
                        ("quantity", Decimal(expectedLine.Quantity), NativeValueKind.Decimal), ("amount", Decimal(line.AmountBeforeTax), NativeValueKind.Decimal),
                        ("unit_price", Decimal(decimal.Round(line.AmountBeforeTax!.Value / expectedLine.Quantity, 2, MidpointRounding.AwayFromZero)), NativeValueKind.Decimal) };
                // Purchases leave Kho/TK Kho/TK công nợ to MISA and do not compare them.
                if (!taxTab && profile.IsPurchase) expected = [.. expected.Where(item => item.Item1 != "debit_account")];
                foreach (var (key, wanted, kind) in expected)
                {
                    var actual = native.Row?.GetValueOrDefault(profile.Columns[key]);
                    var source = native.Row is not null && native.Index < table.Sources.Count
                        ? table.Sources[native.Index].GetValueOrDefault(profile.Columns[key]) ?? "UIA3.GridPattern"
                        : "UIA3.GridPattern:missing_row";
                    var matched = key switch
                    {
                        "name" => CatalogNameMatch(wanted, actual),
                        // MISA derives the price from amount / quantity at its own display scale.
                        "unit_price" => NativeComparison.MatchesDerivedUnitPrice(expectedLine.Quantity, line.AmountBeforeTax!.Value, actual, Culture),
                        // MISA rounds VAT per line itself; up to 2 đồng off the e-invoice is accepted (Cất's prompt is answered Có).
                        "tax_amount" => NativeComparison.MatchesTaxRounding(wanted, actual, Culture),
                        _ => Match(wanted, actual, kind)
                    };
                    values.Add(new($"line:{line.LineId}:{key}", wanted, actual, matched, source));
                }
            }
            var nativeOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var native in nativeRows)
            {
                var expectedCount = expectedOccurrences.GetValueOrDefault(native.Code);
                var nativeOccurrence = nativeOccurrences.GetValueOrDefault(native.Code) + 1;
                nativeOccurrences[native.Code] = nativeOccurrence;
                if (nativeOccurrence > expectedCount)
                    values.Add(new($"line:unexpected:{native.Index}:code", "<no extra native row>", native.Code, false,
                        native.Index < table.Sources.Count
                            ? table.Sources[native.Index].GetValueOrDefault(profile.Columns["code"]) ?? "UIA3.GridPattern"
                            : "UIA3.GridPattern"));
            }
            var count = table.Rows.Count(row => !string.IsNullOrWhiteSpace(row[profile.Columns["code"]])
                || !string.IsNullOrWhiteSpace(row[profile.Columns["name"]]));
            values.Add(new(taxTab ? "tax_row_count" : "item_row_count", context.Invoice.Lines.Length.ToString(CultureInfo.InvariantCulture),
                count.ToString(CultureInfo.InvariantCulture), count == context.Invoice.Lines.Length, "UIA3.GridPattern"));
            if (!taxTab)
            {
                // Totals sit below the grid; read them while the item tab is still selected.
                var totals = await input.ReadFieldsAsync(await input.CaptureAsync(token),
                    new[] { "subtotal", "tax_total", "grand_total" }.ToDictionary(key => key, key => profile.Fields[key]), token);
                foreach (var (key, wanted) in new[] { ("subtotal", context.Invoice.Subtotal), ("tax_total", context.Invoice.TaxTotal),
                    ("grand_total", context.Invoice.GrandTotal) })
                {
                    var read = totals.Values[key];
                    // VAT and the total that includes it may be up to 2 đồng off; the subtotal must be exact.
                    var matched = key == "subtotal" ? Match(Decimal(wanted), read.Value, NativeValueKind.Decimal)
                        : NativeComparison.MatchesTaxRounding(Decimal(wanted), read.Value, Culture);
                    values.Add(new(key, Decimal(wanted), read.Value, matched, read.Source));
                }
            }
        }
        List<(string Key, string Expected, NativeValueKind Kind)> receiptFields =
        [
            ("voucher_number", Saved(context, "voucher_number"), NativeValueKind.Exact),
            .. CustomerFields(context),
            ("voucher_date", context.Invoice.VoucherDate!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date),
            ("posted_date", context.Invoice.PostedDate!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date)
        ];
        var taxField = CustomerTaxField(context);
        var taxOnReceipt = taxField is not null && CustomerTaxTab == profile.ReceiptTab;
        var invoiceFields = new List<(string Key, string Expected, NativeValueKind Kind)>
        {
            ("number", context.Invoice.Identity.Number, NativeValueKind.Exact),
            ("issued_date", context.Invoice.Issued!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date)
        };
        // Purchases are always "Chưa thanh toán" (the editor the Thêm button opens) and keep MISA's series.
        if (!profile.IsPurchase) invoiceFields.Add(("payment", context.Invoice.PaymentMethod, NativeValueKind.Text));
        // An empty source series keeps MISA's default series; there is nothing to compare.
        if (context.Invoice.Identity.Series.Length > 0)
            invoiceFields.Add(("series", context.Invoice.Identity.Series, NativeValueKind.Exact));
        if (InvoiceTemplate(context) is { } template) invoiceFields.Add(("template", template, NativeValueKind.Exact));
        // Invoice tab first: entry ends there, and Cất needs the receipt tab, so each header tab is selected once.
        await CompareFields(profile.InvoiceTab, [.. invoiceFields]);
        var individual = !profile.IsPurchase && BuyerCodes.IsIndividual(context.Invoice.Buyer);
        if (taxField is { } tax && !taxOnReceipt) values.Add(await CompareCustomerTaxAsync(tax.Expected, BuyerCode(context), individual, token));
        await CompareFields(profile.ReceiptTab, [.. receiptFields]);
        if (taxField is { } receiptTax && taxOnReceipt) values.Add(await CompareCustomerTaxAsync(receiptTax.Expected, BuyerCode(context), individual, token));
        // BindEditor/ValidateDocument require the exact domestic cash editor.
        // R20 has no currency control on this VND-only surface.
        values.Add(new("currency", context.Invoice.Currency, "VND",
            context.Invoice.Currency == "VND", "R20.DomesticCashEditor:implicit_currency"));
        return new(context.Invoice.InvoiceId, context.Invoice.Revision, DateTimeOffset.UtcNow, values, context.Invoice.Source.Sha256);
    }

    private async Task VerifyFinished(WorkflowContext context, bool save, CancellationToken token)
    {
        // Saving R20 can leave the voucher editor open in a read-only,
        // saved-invoice state with the Save command disabled. This recovery-only
        // path reads the sales register as save proof; discard needs only the
        // editor to disappear.
        if (!save)
        {
            await WaitEditor(context, false, token);
            return;
        }
        var frame = await BindSales(context, token, activate: false);
        bool MatchesResult(NativeTable table)
        {
            frame = table.Frame;
            var vouchers = table.Rows.Where(row => Match(Saved(context, "voucher_number"),
                row[profile.SalesColumns["voucher_number"]], NativeValueKind.Exact)).ToArray();
            if (vouchers.Length > 1) throw new AutomationException("SAVE_AMBIGUOUS", "Có nhiều dòng cùng số chứng từ; không Cất lại.");
            return vouchers.Length == 1
                && Match(context.Invoice.Identity.Number, vouchers[0][profile.SalesColumns["number"]], NativeValueKind.Exact)
                && Match(ExpectedCustomerName(context), vouchers[0][profile.SalesColumns["customer"]], NativeValueKind.Text)
                && Match(context.Invoice.VoucherDate!.Value.ToString("yyyy-MM-dd"), vouchers[0][profile.SalesColumns["voucher_date"]], NativeValueKind.Date);
        }
        var proof = await NativeWait.UntilAsync(async () => await input.ReadTableAsync(
            await input.CaptureAsync(token), profile.SalesGridId,
            SalesFinishedColumns(), token), MatchesResult,
            "SAVE_UNCERTAIN", "Danh sách MISA chưa xác nhận kết quả Cất; không gửi lại thao tác.", token,
            timeoutMilliseconds: 15000, returnLastMismatch: true);
        if (!MatchesResult(proof))
        {
            var candidates = proof.Rows.Where(row =>
                Match(Saved(context, "voucher_number"), row[profile.SalesColumns["voucher_number"]], NativeValueKind.Exact)
                || Match(context.Invoice.Identity.Number, row[profile.SalesColumns["number"]], NativeValueKind.Exact)).Take(3);
            throw new AutomationException("SAVE_UNCERTAIN",
                $"Native saved proof missing or mismatched at {proof.Frame.ObservedAt:O}; "
                + $"expected voucher={Saved(context, "voucher_number")}, invoice={context.Invoice.Identity.Number}, "
                + $"date={context.Invoice.VoucherDate:yyyy-MM-dd}; observed rows={proof.Rows.Count}, "
                + "candidates=" + JsonSerializer.Serialize(candidates, ContractJson.Compact) + ". Save is not replayed.");
        }
    }

    /// <summary>
    /// Cất can raise MISA's MST-status warning ("Có", keep saving), its tax-difference prompt ("Có" for up to 2 đồng, else
    /// "Không") and then its duplicate-number prompt (answered "No"). Polls until <paramref name="waitMilliseconds"/> passes
    /// without a prompt.
    /// </summary>
    private async Task<(bool TaxAnswered, string? Duplicate, bool AnyAnswered)> AnswerSavePromptsAsync(nint editorHandle,
        int waitMilliseconds, CancellationToken token)
    {
        var taxAnswered = false;
        var statusAnswered = false;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * waitMilliseconds / 1000;
        while (true)
        {
            if (await input.AnswerTaxpayerStatusPromptAsync(editorHandle, token) is { } status)
            {
                statusAnswered = true;
                Trace.TraceInformation("Taxpayer status prompt answered Có: " + status.Text);
                deadline = Math.Max(deadline, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 800 / 1000);
            }
            if (await input.AnswerTaxRoundingPromptAsync(editorHandle, token) is { } tax)
            {
                taxAnswered = true;
                Trace.TraceInformation($"Tax difference prompt answered {(tax.Accepted ? "Có" : "Không")}: " + tax.Text);
                // The duplicate-number prompt, if any, follows the tax prompt.
                deadline = Math.Max(deadline, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 800 / 1000);
            }
            if (await input.AnswerDuplicateInvoicePromptAsync(editorHandle, 0, token) is { } duplicate)
                return (taxAnswered, duplicate, true);
            if (Stopwatch.GetTimestamp() >= deadline) return (taxAnswered, null, taxAnswered || statusAnswered);
            await Task.Delay(50, token);
        }
    }

    private async Task VerifySavedAndCloseEditor(WorkflowContext context, CancellationToken token, bool afterSave,
        bool taxPromptAnswered = false, nint editorHandle = 0)
    {
        var window = BindTarget(context, activate: false);
        if (afterSave) await input.AnswerTaxpayerStatusPromptAsync(editorHandle, token);
        if (afterSave && await input.AnswerTaxRoundingPromptAsync(editorHandle, token) is not null) taxPromptAnswered = true;
        // R20 closes the editor itself only after a successful Save; an unsaved editor never closes silently.
        if (!await EditorExistsAsync(window, token))
        {
            await BindSales(context, token, activate: false);
            return;
        }
        var savedEditor = await BindEditor(context, token);
        if (!afterSave) await VerifyVoucherIdentity(context, savedEditor, token);
        // Saved proof without reading the sales list: "Bỏ ghi" appears, or the voucher turns read-only (Cất disabled).
        await NativeWait.UntilAsync(async () =>
            {
                // A late tax-difference or MST-status prompt keeps Cất enabled until it is answered.
                if (afterSave && await input.AnswerTaxpayerStatusPromptAsync(editorHandle, token) is not null) return false;
                if (afterSave && await input.AnswerTaxRoundingPromptAsync(editorHandle, token) is not null)
                {
                    taxPromptAnswered = true;
                    return false;
                }
                return await ShowsSavedAsync(token);
            }, saved => saved, "SAVE_UNCERTAIN",
            "Chưa thấy nút Bỏ ghi và nút Cất chưa chuyển sang trạng thái đã ghi; không Cất lại.", token, timeoutMilliseconds: 15000);
        if (taxPromptAnswered)
        {
            // "Có" moves a difference of up to 2 đồng into line 1: VAT and grand total stay within 2 đồng, the subtotal is exact.
            await Tab(profile.ItemTab, token);
            var totals = new (string Key, string Expected)[] { ("subtotal", Decimal(context.Invoice.Subtotal)),
                ("tax_total", Decimal(context.Invoice.TaxTotal)), ("grand_total", Decimal(context.Invoice.GrandTotal)) };
            await NativeWait.UntilAsync(() => Steady(async () => await input.ReadFieldsAsync(await input.CaptureAsync(token),
                    totals.ToDictionary(total => total.Key, total => profile.Fields[total.Key]), token), token),
                actual => totals.All(total => total.Key == "subtotal"
                    ? Match(total.Expected, actual.Values[total.Key].Value, NativeValueKind.Decimal)
                    : NativeComparison.MatchesTaxRounding(total.Expected, actual.Values[total.Key].Value, Culture)),
                "READBACK_MISMATCH", "Tổng tiền sau khi trả lời hộp chênh lệch thuế lệch nguồn quá 2 đồng.", token);
        }
        for (var attempt = 0; ; attempt++)
        {
            await input.InvokeWorkflowAsync(await input.CaptureAsync(token), profile.DiscardVoucher, token, leavesScope: true);
            try
            {
                await WaitEditor(context, false, token);
                return;
            }
            catch (AutomationException error) when (error.Code == "EDITOR_STILL_OPEN" && attempt < 2)
            {
                // A stray user click can swallow Đóng. Closing again is not a data input, but only
                // while the same voucher still proves saved (Bỏ ghi / Cất disabled); Cất itself is never resent.
                var editor = await BindEditor(context, token);
                await VerifyVoucherIdentity(context, editor, token);
                if (!await ShowsSavedAsync(token))
                    throw new AutomationException("COMMAND_STATE_MISMATCH", "Chứng từ chưa thể hiện đã Cất; không bấm Đóng lại.");
                Trace.TraceInformation("Đóng chưa đóng editor đã Cất; bấm lại Đóng lần " + (attempt + 2));
            }
        }
    }

    private const int SaveReadRetries = 5;
    private static readonly string[] TransientReadCodes = ["NATIVE_READ_TIMEOUT", "NATIVE_ELEMENT_UNAVAILABLE"];

    private static bool IsTransientRead(Exception error)
        => error is AutomationException { InputMayHaveBeenSent: false } fault && TransientReadCodes.Contains(fault.Code);

    // A completed UIA read that timed out says nothing about the editor; poll until MISA answers.
    private Task<bool> EditorExistsAsync(WindowIdentity window, CancellationToken token)
        => NativeWait.UntilAsync(() => input.WorkflowScopeExistsAsync(window, profile.EditorId, token), _ => true,
            "NATIVE_READ_TIMEOUT", "MISA chưa trả lời khi kiểm tra chứng từ sau Cất.", token, timeoutMilliseconds: 30000);

    private async Task<bool> ShowsUnpostAsync(CancellationToken token)
    {
        if (profile.UnpostVoucher is not { } unpost) return false;
        try { await input.RequireCommandEnabledAsync(await input.CaptureAsync(token), unpost, true, token); return true; }
        catch (AutomationException error) when (error.Code == "COMMAND_STATE_MISMATCH") { return false; }
    }

    private async Task<bool> ShowsSavedAsync(CancellationToken token)
    {
        if (await ShowsUnpostAsync(token)) return true;
        try { await input.RequireDisabledCommandAsync(await input.CaptureAsync(token), profile.SaveVoucher, token); return true; }
        catch (AutomationException error) when (error.Code == "COMMAND_STATE_MISMATCH") { return false; }
    }

    private (string Key, string Expected, NativeValueKind Kind)[] CustomerFields(WorkflowContext context) =>
    [
        ("customer", context.NativeValues?.GetValueOrDefault("customer_reference") ?? BuyerCode(context), NativeValueKind.Exact),
        ("customer_name", ExpectedCustomerName(context), NativeValueKind.Text)
    ];

    private static (string Key, string Expected, NativeValueKind Kind)? CustomerTaxField(WorkflowContext context)
        => string.IsNullOrWhiteSpace(context.Invoice.Partner().TaxCode) ? null
            : ("customer_tax", context.Invoice.Partner().TaxCode, NativeValueKind.Exact);

    // R20 cash sales show the buyer tax code on the "Hóa đơn" tab; "Phiếu thu" has no MST control.
    private string CustomerTaxTab => profile.FieldTabs.GetValueOrDefault("customer_tax", profile.InvoiceTab);
    // Once the MST control proved absent in this MISA session, later reads try once instead of waiting.
    private volatile bool customerTaxAbsent;

    /// <summary>
    /// Reads the buyer MST on the already selected tab. MISA fills it from its customer list once the code is chosen;
    /// when this R20 build shows no control matching the profile selector, the customer list (exported from MISA) is
    /// the proof: the verified customer code must carry the source MST there. A readable MISA value always decides.
    /// </summary>
    private async Task<ValueComparison> CompareCustomerTaxAsync(string expected, string? customerCode, bool individual, CancellationToken token)
        => CustomerTaxComparison(expected, customerCode, await ReadCustomerTaxAsync(token), individual);

    private async Task<Readback?> ReadCustomerTaxAsync(CancellationToken token)
    {
        Readback? read = null;
        try
        {
            read = await NativeWait.UntilAsync(async () => await input.ReadFieldAsync(await input.CaptureAsync(token),
                profile.Fields["customer_tax"], token), _ => true, "TARGET_NOT_FOUND", "Không thấy ô Mã số thuế.", token,
                timeoutMilliseconds: customerTaxAbsent ? 1 : 1500);
        }
        catch (AutomationException error) when (error.Code == "TARGET_NOT_FOUND" && !error.InputMayHaveBeenSent) { }
        customerTaxAbsent = read is null;
        return read;
    }

    /// <param name="individual">
    /// The buyer is a person: MISA customers for individuals are often kept without an MST (the invoice carries a personal
    /// ID), so an empty MISA MST is accepted for them. A different, non-empty MST still mismatches.
    /// </param>
    private ValueComparison CustomerTaxComparison(string expected, string? customerCode, Readback? read, bool individual)
    {
        var entry = PartyCatalog?.FindByCode(customerCode);
        if (read is not null)
        {
            if (PartyCatalogStore.TaxKey(expected) == PartyCatalogStore.TaxKey(read.Value))
                return new("customer_tax", expected, read.Value, true, read.Source);
            // An MST in the customer list means MISA has one that is not filled in yet; only an MST-less customer passes.
            var withoutMst = individual && PartyCatalogStore.TaxKey(read.Value).Length == 0
                && PartyCatalogStore.TaxKey(entry?.TaxCode).Length == 0;
            return new("customer_tax", expected, read.Value, withoutMst,
                withoutMst ? read.Source + " (cá nhân: MISA không lưu MST)" : read.Source);
        }
        var listed = entry?.TaxCode ?? "";
        if (individual && entry is not null && PartyCatalogStore.TaxKey(listed).Length == 0)
            return new("customer_tax", expected, "", true, $"PartyCatalog:{customerCode} (cá nhân: MISA không lưu MST)");
        return new("customer_tax", expected, listed.Length == 0 ? null : listed,
            listed.Length > 0 && PartyCatalogStore.TaxKey(expected) == PartyCatalogStore.TaxKey(listed),
            $"PartyCatalog:{customerCode} (MISA không hiện ô MST theo profile)");
    }

    // The run ended in doubt and the user has since closed its voucher. Nothing is typed or clicked:
    // a complete register read decides whether that voucher was saved.
    private async Task<WorkflowObservation> ResolveStaleAsync(WorkflowContext context, WindowIdentity window, CancellationToken token)
    {
        var number = context.Invoice.Identity.Number;
        try
        {
            if (await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                return new(false, context.Snapshot.Status, "",
                    $"Chứng từ {(profile.IsPurchase ? "mua" : "bán")} hàng vẫn đang mở trong MISA; chưa xác định được hóa đơn {number} đã Cất hay chưa.",
                    ErrorCode: "EXISTING_EDITOR");
            // Without a voucher number the voucher was never identified; MISA's duplicate-number prompt still guards it.
            if (context.NativeValues?.GetValueOrDefault("voucher_number") is not { Length: > 0 } voucher)
                return new(true, WorkflowStatus.Discarded, Frame(await input.CaptureAsync(token)),
                    $"Lần chạy trước của hóa đơn {number} dừng trước khi có chứng từ; chứng từ đã đóng.");
            // Reuse the window bound without the profile-revision guard; BindSales would re-apply it.
            var sales = await input.TryBindWorkflowScopeAsync(window, profile.SalesScopeId, null, token)
                ?? throw SurfaceMissing();
            var register = await input.ReadCompleteTableAsync(sales, profile.SalesGridId, SalesFinishedColumns(), token);
            var saved = register.Rows.Any(row =>
                Match(voucher, row[profile.SalesColumns["voucher_number"]], NativeValueKind.Exact)
                && Match(number, row[profile.SalesColumns["number"]], NativeValueKind.Exact));
            return new(true, saved ? WorkflowStatus.Saved : WorkflowStatus.Discarded, Frame(register.Frame), saved
                ? $"Danh sách MISA có chứng từ {voucher} (hóa đơn {number}): lần chạy trước đã Cất."
                : $"Danh sách MISA không có chứng từ {voucher}: hóa đơn {number} chưa được Cất, chứng từ đã đóng.");
        }
        catch (AutomationException error)
        {
            return new(false, context.Snapshot.Status, "", error.Message, ErrorCode: error.Code);
        }
    }

    private async Task<NativeFields> VerifyCustomer(WorkflowContext context, CancellationToken token, bool selecting = false)
    {
        await Tab(profile.ReceiptTab, token);
        // R20 exposes the selected customer's internal GUID through the combo
        // ValuePattern. Persist that reference only after the source-code commit
        // and business identity readback; recovery must match the same reference.
        var expected = CustomerFields(context);
        var fields = expected.ToDictionary(field => field.Key, field => profile.Fields[field.Key]);
        var customer = await NativeWait.UntilAsync(() => Steady(async () => await input.ReadFieldsAsync(
            await input.CaptureAsync(token), fields, token), token),
            actual => expected.All(field => Match(field.Expected, actual.Values[field.Key].Value, field.Kind)
                || selecting && field.Key == "customer" && Guid.TryParse(actual.Values[field.Key].Value, out _)
                || profile.IsPurchase && SupplierMatches(context, field.Key, field.Expected, actual.Values[field.Key].Value, selecting)),
            "READBACK_MISMATCH", profile.IsPurchase ? "Nhà cung cấp MISA chưa khớp tên/mã sau khi gõ mã nhà cung cấp (hoặc MST khi danh mục chưa có)." : "Khách hàng MISA chưa khớp mã/tên sau khi chọn mã nguồn.",
            token, timeoutMilliseconds: 15000);
        // The buyer MST is compared by the reconciliation before Cất, on the tab it is already reading.
        return customer;
    }
    private async Task VerifyDates(WorkflowContext context, CancellationToken token)
    {
        await Tab(profile.ReceiptTab, token);
        await RequireFields(token,
            ("voucher_date", context.Invoice.VoucherDate!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date),
            ("posted_date", context.Invoice.PostedDate!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date));
        await Tab(profile.InvoiceTab, token);
        await Require("issued_date", context.Invoice.Issued!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date, token);
    }
    private async Task VerifyInvoice(WorkflowContext context, CancellationToken token)
    {
        await Tab(profile.InvoiceTab, token);
        await Require("number", context.Invoice.Identity.Number, NativeValueKind.Exact, token);
        if (context.Invoice.Identity.Series.Length > 0) await Require("series", context.Invoice.Identity.Series, NativeValueKind.Exact, token);
        if (InvoiceTemplate(context) is { } template) await Require("template", template, NativeValueKind.Exact, token);
        if (profile.IsPurchase) return;
        // R20's domestic VND editor has no currency control; VND is implicit in this surface.
        if (!string.Equals(context.Invoice.Currency, "VND", StringComparison.Ordinal))
            await Require("currency", context.Invoice.Currency, NativeValueKind.Exact, token);
        await Require("payment", context.Invoice.PaymentMethod, NativeValueKind.Text, token);
    }
    private async Task VerifyVoucherIdentity(WorkflowContext context, NativeFrame frame, CancellationToken token)
    {
        Remember(profile.ReceiptTab);
        await input.SelectTabAsync(frame, profile.ReceiptTab, token);
        await Require("voucher_number", Saved(context, "voucher_number"), NativeValueKind.Exact, token);
    }
    private async Task<NativeFrame> BindEditor(WorkflowContext context, CancellationToken token)
    {
        ValidateDocument(context);
        var frame = await input.TryBindWorkflowScopeAsync(BindTarget(context, editor: true), profile.EditorId, null, token)
            ?? throw new AutomationException("EDITOR_NOT_FOUND", "Không có editor chứng từ đã chọn.");
        return await input.ActivateWorkflowScopeAsync(frame, token);
    }
    private async Task<NativeFrame> BindSales(WorkflowContext context, CancellationToken token, bool activate = true)
        => await input.TryBindWorkflowScopeAsync(BindTarget(context, activate), profile.SalesScopeId, null, token)
            ?? throw SurfaceMissing();

    // Same code for both lists so recovery and the UI treat them alike.
    private AutomationException SurfaceMissing() => new("SALES_SURFACE_MISSING", profile.IsPurchase
        ? "Mở màn hình Mua hàng hóa, dịch vụ (phân hệ Mua hàng) của dữ liệu đã chọn. Nếu đã mở mà vẫn báo lỗi, bấm Copy lỗi gửi cho AI để cập nhật profile."
        : "Mở màn hình Bán hàng của dữ liệu đã chọn.");

    private static void ValidateDocument(WorkflowContext context)
    {
        if (context.Invoice.IsPurchase())
        {
            if (context.Invoice.Currency != "VND")
                throw new AutomationException("DOCUMENT_TYPE_UNSUPPORTED", "Profile chỉ hỗ trợ mua hàng trong nước VND.");
        }
        else if (context.Invoice.Currency != "VND" || context.Invoice.PaymentMethod != "Tiền mặt"
            || context.Invoice.InvoiceType != InvoiceKinds.Sales)
            throw new AutomationException("DOCUMENT_TYPE_UNSUPPORTED", "Profile chỉ hỗ trợ bán hàng trong nước VND/tiền mặt.");
        if (context.Invoice.Lines.Length > 5000 || context.Invoice.Lines.Select(line => line.LineId).Distinct().Count() != context.Invoice.Lines.Length
            || context.Invoice.Lines.Any(line => string.IsNullOrWhiteSpace(line.LineId)))
            throw new AutomationException("LINE_IDENTITY_INVALID", "Dòng hàng vượt giới hạn hoặc thiếu identity duy nhất.");
        if (context.Invoice.Lines.Any(line => string.IsNullOrWhiteSpace(line.Name)
                || string.IsNullOrWhiteSpace(line.Unit)))
            throw new AutomationException("ITEM_IDENTITY_MISSING", "Each item requires a name and unit; the item code is optional.");
        var texts = new[] { context.Invoice.Partner().Code, context.Invoice.Partner().TaxCode, context.Invoice.Identity.Number, context.Invoice.Identity.Series }
            .Concat(context.Invoice.Lines.SelectMany(line => new[] { line.ItemCode, line.Name, line.Unit }));
        if (texts.Any(text => text.Length > 32768 || text.Any(char.IsControl)))
            throw new AutomationException("INVALID_TEXT", "Dữ liệu chứa ký tự điều khiển hoặc vượt giới hạn nhập native.");
    }
    private async Task<NativeFrame?> WaitEditor(WorkflowContext context, bool visible, CancellationToken token)
    {
        if (visible)
        {
            var editor = await NativeWait.UntilAsync(() => input.TryBindWorkflowScopeAsync(BindTarget(context, editor: true), profile.EditorId, null, token, waitForEnabled: true),
                frame => frame is not null, "EDITOR_NOT_FOUND", "Editor chưa sẵn sàng.", token, timeoutMilliseconds: 60000);
            return await input.ActivateWorkflowScopeAsync(editor!, token);
        }
        await NativeWait.UntilAsync(() => input.WorkflowScopeExistsAsync(BindTarget(context), profile.EditorId, token),
            exists => !exists, "EDITOR_STILL_OPEN", "Editor chưa đóng; cần kiểm tra Cất/Bỏ.", token, timeoutMilliseconds: 15000);
        return null;
    }
    // resolve_stale only reads the sales register with the current profile, so a checkpoint left by an older
    // profile revision can still be settled; any step that types or reads the editor keeps the revision guard.
    private WindowIdentity BindTarget(WorkflowContext context, bool activate = true, bool anyProfileRevision = false, bool editor = false)
    {
        if (!anyProfileRevision && context.ProfileRevision != profile.Revision)
            throw new AutomationException("PROFILE_REVISION_CHANGED", "Checkpoint thuộc profile khác; cần đối chiếu và migration explicit trước khi nhập.");
        var parts = context.WindowIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2).ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
        if (!long.TryParse(parts.GetValueOrDefault("hwnd"), out var handle) || !int.TryParse(parts.GetValueOrDefault("pid"), out var pid)
            || !DateTimeOffset.TryParse(parts.GetValueOrDefault("start"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start))
            throw new AutomationException("WINDOW_IDENTITY_INVALID", "Thiếu HWND/PID/process birth.");
        // Restore and foreground MISA before binding UIA scopes; minimized windows expose no workflow surface.
        // Read-only observations do not need to change the foreground window.
        // Verify the bound session before activating it, so a login/"Mở dữ liệu" window is never brought forward.
        var window = windows.Bind((nint)handle, pid);
        if (window.ProcessStartUtc != start.UtcDateTime || !string.Equals(window.ExecutablePath, parts.GetValueOrDefault("exe"), StringComparison.OrdinalIgnoreCase)
            || !window.Title.StartsWith(profile.VersionTitle + " - ", StringComparison.Ordinal))
            throw new AutomationException("WINDOW_IDENTITY_CHANGED",
                "Phiên MISA đã thay đổi (MISA mở lại, đổi dữ liệu hoặc đang ở màn hình đăng nhập). Đăng nhập/mở đúng dữ liệu rồi bấm Kết nối lại.");
        if (!activate) return window;
        if (!editor) return windows.Focus((nint)handle, pid);
        // Activating the main window deactivates R20's owned voucher window (focus moved to the Bán hàng list), whose grid
        // then exposed rows without cells (VPS 0.2.166, HĐ 81). Editor steps only restore MISA and activate the voucher itself.
        MisaWindowDiscovery.RestoreIfMinimized((nint)handle);
        return windows.Bind((nint)handle, pid);
    }
    private Task<Readback> Field(string key, CancellationToken token)
        => Steady(async () => await input.ReadFieldAsync(await input.CaptureAsync(token), profile.Fields[key], token), token);
    private async Task<NativeFrame> Tab(string tab, CancellationToken token)
    {
        Remember(tab);
        return (await input.SelectTabAsync(await input.CaptureAsync(token), tab, token)).Frame;
    }

    // Tabs the workflow selected, most recent last. Replaying them in order leaves every tab
    // group (Phiếu thu/Hóa đơn, Hàng tiền/Thuế) on the tab the workflow chose last.
    private readonly List<string> workflowTabs = [];
    private void Remember(string tab)
    {
        workflowTabs.Remove(tab);
        workflowTabs.Add(tab);
    }

    // Failures that only mean the user moved focus or switched a tab under the workflow.
    private static readonly HashSet<string> DisplacedCodes = new(StringComparer.Ordinal)
    {
        "TARGET_NOT_FOUND", "FOCUS_LOST", "FOCUS_FAILED", "GRID_COLUMN_AMBIGUOUS", "GRID_COLUMN_MISSING", "GRID_OFFSCREEN"
    };

    private static bool Displaced(Exception error)
    {
        if (error is not AutomationException { InputMayHaveBeenSent: false }) return false;
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is AutomationException { InputMayHaveBeenSent: true }) return false;
            if (current is AutomationException automation && DisplacedCodes.Contains(automation.Code)) return true;
        }
        return false;
    }

    /// <summary>
    /// Runs a read or a write again after restoring the workflow's tabs, but only when it failed
    /// before any input was sent (user clicked another tab or window). Uncertain input is never replayed.
    /// </summary>
    private async Task<T> Steady<T>(Func<Task<T>> operation, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await operation(); }
            catch (Exception error) when (attempt < 2 && Displaced(error))
            {
                Trace.TraceInformation("Khôi phục tab/focus MISA sau thao tác của người dùng: " + error.Message);
                foreach (var tab in workflowTabs.ToArray())
                    await input.SelectTabAsync(await input.CaptureAsync(token), tab, token);
            }
        }
    }
    private async Task Require(string field, string expected, NativeValueKind kind, CancellationToken token)
        => await RequireFields(token, (field, expected, kind));
    private async Task RequireFields(CancellationToken token, params (string Key, string Expected, NativeValueKind Kind)[] fields)
    {
        await NativeWait.UntilAsync(() => Steady(async () => await input.ReadFieldsAsync(
            await input.CaptureAsync(token),
            fields.ToDictionary(field => field.Key, field => profile.Fields[field.Key]), token), token),
            actual => fields.All(field => Match(field.Expected, actual.Values[field.Key].Value, field.Kind)),
            "READBACK_MISMATCH", "Các trường chưa khớp nguồn: " + string.Join(", ", fields.Select(field => field.Key)), token);
    }
    private async Task Write(string field, string text, string expected, NativeValueKind kind, string commit, CancellationToken token)
    {
        StopPoint();
        Entering(field);
        await Steady(async () => await input.WriteFieldAsync(await input.CaptureAsync(token), profile.Fields[field], text, expected, kind,
            profile.Culture, commit, token, verifyAfterWrite: false), token);
    }
    private Task WriteDate(string field, DateOnly day, CancellationToken token)
        => Write(field, day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            NativeValueKind.Date, "Tab", token);
    private NativeCell CellTarget(int row, string code, string key) => new(profile.ItemGridId, row, profile.Columns[key], profile.Columns["code"], code);
    private async Task<Readback> Cell(int row, string code, string key, CancellationToken token)
        => await input.ReadCellAsync(await input.CaptureAsync(token), CellTarget(row, code, key), token);
    private async Task WriteCell(int row, string code, string key, decimal value, CancellationToken token)
        => await input.WriteCellAsync(await input.CaptureAsync(token), CellTarget(row, code, key), value.ToString(Culture), value.ToString(CultureInfo.InvariantCulture),
            NativeValueKind.Decimal, profile.Culture, token);
    private async Task RequireCell(int row, string code, string key, string expected, NativeValueKind kind, CancellationToken token)
    {
        await NativeWait.UntilAsync(async () => await input.ReadCellAsync(
            await input.CaptureAsync(token), CellTarget(row, code, key), token, waitForCode: key == "code"),
            actual => Match(expected, actual.Value, kind), "READBACK_MISMATCH", $"Dòng {row + 1}, {key} khác nguồn.", token);
    }
    // Texts also compare by TextKey, so tone placement, look-alike letters and invisible characters never cause a mismatch.
    private bool Match(string expected, string? actual, NativeValueKind kind) => NativeComparison.Matches(expected, actual, kind, Culture)
        || kind == NativeValueKind.Text && actual is not null && TextKey.Canonical(expected) == TextKey.Canonical(actual);
    // Names compare ignoring case, spacing, punctuation and the e-invoice's extra wording; the item code is the
    // authoritative identity.
    private static bool CatalogNameMatch(string expected, string? actual)
        => actual is not null && ItemCatalogStore.SameItemName(expected, actual);
    private static string Decimal(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? throw new InvalidDataException("Thiếu số nguồn.");
    private static string Saved(WorkflowContext context, string key) => context.NativeValues?.GetValueOrDefault(key)
        ?? throw new AutomationException("CHECKPOINT_PROOF_MISSING", "Checkpoint thiếu native proof " + key);
    private static string Frame(NativeFrame frame) => JsonSerializer.Serialize(frame, ContractJson.Compact);
    private IReadOnlyList<string> SalesFinishedColumns() => [profile.SalesColumns["voucher_number"], profile.SalesColumns["number"],
        profile.SalesColumns["customer"], profile.SalesColumns["voucher_date"]];
    private static string TableHash(NativeTable table) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(table.Rows, ContractJson.Compact))));
}

