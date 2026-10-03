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

public sealed class NativeWorkflowGateway(MisaWindowDiscovery windows, NativeInputService input,
    WorkflowProfile profile) : IWorkflowNativeGateway
{
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
            BindTarget(context, activate: false);
            if (status is WorkflowStatus.Ready or WorkflowStatus.PreflightPassed or WorkflowStatus.SalesSurface)
            {
                ValidateDocument(context);
                if (await input.WorkflowScopeExistsAsync(BindTarget(context, activate: false), profile.EditorId, token))
                    throw new AutomationException("EXISTING_EDITOR", "An open voucher cannot be recovered as a pre-entry checkpoint.");
                var current = await BindSales(context, token, activate: false);
                return new(true, status, Frame(current), "Đã đọc lại phiên MISA và surface Bán hàng.");
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
        var checkpointStarted = Stopwatch.GetTimestamp();
        try
        {
            var window = BindTarget(context, activate: transition.Action != "verify_only");
            ValidateDocument(context);
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
                if (await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                    throw new AutomationException("EXISTING_EDITOR", "Có editor chưa gắn với run; cần xử lý trước khi nhập mới.");
                var current = await BindSales(context, token);
                return new(true, transition.To, Frame(current), "Đã xác nhận preflight và surface Bán hàng.");
            }
            if (transition.To == WorkflowStatus.VoucherCreated)
            {
                if (await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                    throw new AutomationException("EXISTING_EDITOR", "Đang có editor chưa gắn với run; không nhận chứng từ đang mở làm chứng từ mới.");
                // The sales list is not read: already-saved invoices are skipped from history.
                var sales = await BindSales(context, token);
                await input.InvokeWorkflowAsync(sales, profile.OpenVoucher, token);
                sent = true;
                await WaitEditor(context, true, token);
                await Tab(profile.ReceiptTab, token);
                var voucher = await Field("voucher_number", await input.CaptureAsync(token), token);
                if (string.IsNullOrWhiteSpace(voucher.Value)) throw new AutomationException("VOUCHER_ID_MISSING", "Chưa đọc được số chứng từ mới.", true);
                await progress(new("voucher_number", voucher.Value!, Frame(voucher.Frame)), token);
            }
            else
            {
                // Entry steps skip readbacks the final reconciliation repeats (voucher number,
                // dates, invoice fields, line values). Recovery still verifies via ObserveAsync.
                // A confirmed discard may be retried after the editor has already closed;
                // let its fresh register readback decide whether an editor is needed.
                if (transition.To != WorkflowStatus.Discarded
                    || await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                    await BindEditor(context, token);
                switch (transition.To)
                {
                    case WorkflowStatus.CustomerVerified:
                        await Tab(profile.ReceiptTab, token);
                        Entering("customer");
                        await input.WriteFieldAsync(await input.CaptureAsync(token), profile.Fields["customer"],
                            context.Invoice.Buyer.Code, context.Invoice.Buyer.Code, NativeValueKind.Exact,
                            profile.Culture, "Enter", token, selectedCustomer: true, verifyAfterWrite: false);
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
                        await Write("number", context.Invoice.Identity.Number, context.Invoice.Identity.Number, NativeValueKind.Exact, "Tab", token); sent = true;
                        if (context.Invoice.Identity.Series.Length > 0)
                            await Write("series", context.Invoice.Identity.Series, context.Invoice.Identity.Series, NativeValueKind.Exact, "Tab", token);
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
                        string? duplicate;
                        try
                        {
                            duplicate = await input.AnswerDuplicateInvoicePromptAsync(editorHandle, 800, token);
                            if (duplicate is null) await VerifySavedAndCloseEditor(context, token, afterSave: true);
                        }
                        catch (AutomationException) when (!token.IsCancellationRequested)
                        {
                            // A slow prompt makes the editor modal, so the saved-proof reads fail first.
                            duplicate = await input.AnswerDuplicateInvoicePromptAsync(editorHandle, 1500, token);
                            if (duplicate is null) throw;
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
                            await input.RequireCommandEnabledAsync(await input.CaptureAsync(token), profile.SaveVoucher, true, token);
                        }
                        if (await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                        {
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
        catch (Exception error)
        {
            checkpointFailed = true;
            sent |= input.WorkflowInputSequence != inputSequence || error is AutomationException fault && fault.InputMayHaveBeenSent;
            var automation = error as AutomationException;
            var failedStep = activity?.Field ?? transition.Action;
            SetActivity(null, "failed");
            if (error is not OperationCanceledException) await NotifyFailureAsync(context, failedStep);
            return new(false, saveSent ? WorkflowStatus.SaveUncertain : transition.To == WorkflowStatus.Saved ? WorkflowStatus.Blocked
                    : sent ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked,
                "", error.Message, sent ? InputDisposition.Uncertain : InputDisposition.NotSent, sent,
                automation?.Code ?? "WORKFLOW_ERROR", error.ToString(), comparison);
        }
        finally
        {
            SetActivity(null, checkpointFailed ? "failed" : "ok");
            AddTiming(context.Snapshot.RunId, "checkpoint", transition.Checkpoint + " -> " + transition.To, checkpointStartedAt,
                Elapsed(checkpointStarted), checkpointFailed ? "failed" : "ok");
            RecordCheckpoint(transition.To.ToString(), checkpointStarted);
        }
    }

    private ItemCatalogStore RequireCatalog()
        => ItemCatalog is { Count: > 0 } catalog ? catalog
            : throw new AutomationException("ITEM_CATALOG_MISSING", "Chưa nạp database mã hàng. Bấm 'Mã hàng' và chọn file danh mục vật tư, hàng hóa.");

    private async Task<string> CreateMissingItemAsync(WorkflowContext context, InvoiceLine line, int row,
        NativeCell codeCell, CancellationToken token)
    {
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
        var code = catalog.NextCode(proposed: proposed);
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
        catalog.ConfirmPending(code);
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
        await input.WriteCellAsync(await input.CaptureAsync(token), cell, code, code, NativeValueKind.Exact,
            profile.Culture, token, commitKey: "Enter");
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
            || !(context.NativeValues?.GetValueOrDefault("unit_absence:" + line.LineId) ?? "").StartsWith("UIA3.CompleteRegisteredUnitList:", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(context.NativeValues?.GetValueOrDefault("line:" + line.LineId)))
            throw new AutomationException("UNIT_CONVERSION_PROVENANCE_INVALID", "Converted line has no valid authorization and registered-unit absence evidence.");
        return ("T\u1ea5n", line.Quantity!.Value / 1000m, line.UnitPrice.GetValueOrDefault() * 1000m);
    }

    private async Task EnterLines(WorkflowContext context, Func<WorkflowProgress, CancellationToken, Task> progress, CancellationToken token)
    {
        var codes = new List<string>();
        await Tab(profile.ItemTab, token);
        for (var row = 0; row < context.Invoice.Lines.Length; row++)
        {
            var line = context.Invoice.Lines[row];
            Entering("line:" + line.LineId + ":code");
            var code = line.ItemCode.Trim();
            if (code.Length == 0)
                code = context.NativeValues?.GetValueOrDefault("line:" + line.LineId)?.Trim() ?? "";
            var codeCell = new NativeCell(profile.ItemGridId, row, profile.Columns["code"], profile.Columns["code"], "");
            var existingCode = (await NativeWait.UntilAsync(async () => await input.ReadCellAsync(
                await input.CaptureAsync(token), codeCell, token, waitForCode: false),
                read => read.Value is not null, "GRID_ROW_NOT_READY", $"Row {row + 1} is not ready.", token)).Value!.Trim();
            PendingItemCreation? pending = null;
            if (code.Length == 0)
            {
                var catalog = RequireCatalog();
                code = catalog.Find(line.Name, line.Unit, context.AllowKgToTon)?.Code ?? "";
                // A previous run may have clicked "Cất" without learning the result: reuse that code, never create another.
                if (code.Length == 0 && catalog.FindPending(line.Name, line.Unit) is { } found)
                    (pending, code) = (found, found.Code);
            }
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
                    code = await CreateMissingItemAsync(context, line, row, codeCell, token);
                Entering("line:" + line.LineId + ":code");
                try { await SelectItemCodeAsync(row, code, created, token); }
                catch (Exception error) when (pending is not null && error is not OperationCanceledException)
                {
                    throw PendingUnresolved(row, pending, error);
                }
            }
            var boundRow = new NativeCell(profile.ItemGridId, row, profile.Columns["code"], profile.Columns["code"], code);
            NativeFields metadata;
            try
            {
                metadata = await NativeWait.UntilAsync(async () => await input.ReadRowAsync(
                    await input.CaptureAsync(token), boundRow,
                    [profile.Columns["name"], profile.Columns["unit"], profile.Columns["debit_account"]], token),
                    actual => CatalogNameMatch(line.Name, actual.Values[profile.Columns["name"]].Value),
                    "READBACK_MISMATCH", $"Dòng {row + 1}: tên hàng MISA khác tên trong file.", token);
            }
            catch (Exception error) when (pending is not null && error is not OperationCanceledException)
            {
                throw PendingUnresolved(row, pending, error);
            }
            if (pending is not null) RequireCatalog().ConfirmPending(pending.Code);
            var expectedAccount = metadata.Values[profile.Columns["debit_account"]].Value;
            if (string.IsNullOrWhiteSpace(expectedAccount) || !expectedAccount.All(char.IsDigit))
                throw new AutomationException("ACCOUNT_READBACK_INVALID", $"Row {row + 1}: native account is invalid.");
            var converted = !string.IsNullOrWhiteSpace(context.NativeValues?.GetValueOrDefault("conversion:" + line.LineId));
            var expected = ExpectedLine(context, line);
            if (!Match(expected.Unit, metadata.Values[profile.Columns["unit"]].Value, NativeValueKind.Text))
            {
                if (converted)
                    throw new AutomationException("ITEM_UNIT_MISMATCH", $"Row {row + 1}: native unit differs from the recorded conversion.");
                var selected = await input.SelectCellOptionAsync(await input.CaptureAsync(token),
                    CellTarget(row, code, "unit"), line.Unit, profile.Culture, token,
                    allowMissing: context.AllowKgToTon && Match(line.Unit, "Kg", NativeValueKind.Text));
                if (selected.Status == "option_absent")
                {
                    if (!context.AllowKgToTon || !Match(line.Unit, "Kg", NativeValueKind.Text)
                        || !Match(selected.Value ?? "", "T\u1ea5n", NativeValueKind.Text))
                        throw new AutomationException("ITEM_UNIT_MISMATCH", $"Row {row + 1}: no authorized conversion for the native unit.");
                    expected = ("T\u1ea5n", line.Quantity!.Value / 1000m, line.UnitPrice.GetValueOrDefault() * 1000m);
                    // Commit absence evidence and exact code before any numeric
                    // write so partial-row recovery cannot apply the factor twice.
                    await progress(new("line:" + line.LineId, code, Frame(selected.Frame), row,
                        new Dictionary<string, string>
                        {
                            ["conversion:" + line.LineId] = "kg_to_ton:1000",
                            ["unit:" + line.LineId] = expected.Unit,
                            ["unit_absence:" + line.LineId] = selected.Source + ":" + selected.Reason
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
            var sentFrame = await input.SendRowValuesAsync(await input.CaptureAsync(token),
                new NativeCell(profile.ItemGridId, row, profile.Columns["code"], profile.Columns["code"], code),
                writes, token, column => Entering("line:" + line.LineId + ":"
                    + profile.Columns.Single(pair => pair.Value == column).Key));
            codes.Add(code);
            await progress(new("line:" + line.LineId, code, Frame(sentFrame), row,
                new Dictionary<string, string> { ["account:" + line.LineId] = expectedAccount }), token);
        }
        var taxFrame = await Tab(profile.TaxTab, token);
        // Items usually carry their VAT rate already; one grid read avoids a write per line.
        var taxGrid = await input.ReadTableAsync(taxFrame, profile.ItemGridId,
            [profile.Columns["code"], profile.Columns["tax_rate"]], token);
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
            Entering("line:" + line.LineId + ":tax_rate");
            await input.WriteCellAsync(await input.CaptureAsync(token), CellTarget(row, codes[row], "tax_rate"), tax, tax,
                NativeValueKind.TaxRate, profile.Culture, token);
        }
    }

    public async Task<ReconciliationResult> ReconcileAsync(WorkflowContext context, CancellationToken token)
    {
        SetActivity(new(context.Snapshot.RunId, "reconcile", DateTimeOffset.UtcNow), "ok");
        var failed = true;
        try
        {
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
        var result = await NativeWait.UntilAsync(() => ReadReconciliationPassAsync(context, token), result => result.Matched,
            "RECONCILIATION_PENDING", "MISA values have not settled.", token, returnLastMismatch: true);
        lastReconciliation = (context.Snapshot.RunId, context.Invoice.Fingerprint, input.ValueInputSequence, DateTimeOffset.UtcNow, result);
        return result;
    }

    private async Task<ReconciliationResult> ReadReconciliationPassAsync(WorkflowContext context, CancellationToken token)
    {
        var values = new List<ValueComparison>();
        async Task<NativeFrame> CompareFields(string tab, params (string Key, string Expected, NativeValueKind Kind)[] fields)
        {
            await Tab(tab, token);
            var actual = await NativeWait.UntilAsync(async () => await input.ReadFieldsAsync(
                await input.CaptureAsync(token),
                fields.ToDictionary(field => field.Key, field => profile.Fields[field.Key]), token),
                _ => true, "TARGET_NOT_FOUND", "Các trường chưa sẵn sàng sau khi chuyển tab.", token);
            foreach (var field in fields)
            {
                var read = actual.Values[field.Key];
                values.Add(new(field.Key, field.Expected, read.Value, Match(field.Expected, read.Value, field.Kind), read.Source));
            }
            return actual.Frame;
        }
        // Tab order: tax grid (selected after entry) → item grid + totals → receipt → invoice.
        foreach (var taxTab in new[] { true, false })
        {
            var tabFrame = await Tab(taxTab ? profile.TaxTab : profile.ItemTab, token);
            var keys = taxTab ? new[] { "code", "name", "tax_rate", "tax_amount" }
                : new[] { "code", "name", "unit", "debit_account", "quantity", "amount", "unit_price" };
            var table = await input.ReadCompleteTableAsync(tabFrame, profile.ItemGridId,
                keys.Select(key => profile.Columns[key]).ToArray(), token);
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
                    values.Add(new(key, Decimal(wanted), read.Value, Match(Decimal(wanted), read.Value, NativeValueKind.Decimal), read.Source));
                }
            }
        }
        await CompareFields(profile.ReceiptTab,
            [("voucher_number", Saved(context, "voucher_number"), NativeValueKind.Exact),
            .. CustomerFields(context),
            ("voucher_date", context.Invoice.VoucherDate!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date),
            ("posted_date", context.Invoice.PostedDate!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date)]);
        var invoiceFields = new List<(string Key, string Expected, NativeValueKind Kind)>
        {
            ("number", context.Invoice.Identity.Number, NativeValueKind.Exact),
            ("issued_date", context.Invoice.Issued!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date),
            ("payment", context.Invoice.PaymentMethod, NativeValueKind.Text)
        };
        // An empty source series keeps MISA's default series; there is nothing to compare.
        if (context.Invoice.Identity.Series.Length > 0)
            invoiceFields.Add(("series", context.Invoice.Identity.Series, NativeValueKind.Exact));
        await CompareFields(profile.InvoiceTab, [.. invoiceFields]);
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
                && Match(context.Invoice.Buyer.Name, vouchers[0][profile.SalesColumns["customer"]], NativeValueKind.Text)
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

    private async Task VerifySavedAndCloseEditor(WorkflowContext context, CancellationToken token, bool afterSave)
    {
        var window = BindTarget(context, activate: false);
        var preservedTax = afterSave && await input.TryPreserveSourceTaxAsync(window, profile.EditorId, null,
            profile.Fields["voucher_number"], Saved(context, "voucher_number"), context.Invoice.TaxTotal!.Value,
            context.Invoice.Lines.Length, token);
        // R20 closes the editor itself only after a successful Save; an unsaved editor never closes silently.
        if (!await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
        {
            await BindSales(context, token, activate: false);
            return;
        }
        var savedEditor = await BindEditor(context, token);
        if (!afterSave) await VerifyVoucherIdentity(context, savedEditor, token);
        if (preservedTax)
        {
            await Tab(profile.ItemTab, token);
            await RequireFields(token,
                ("subtotal", Decimal(context.Invoice.Subtotal), NativeValueKind.Decimal),
                ("tax_total", Decimal(context.Invoice.TaxTotal), NativeValueKind.Decimal),
                ("grand_total", Decimal(context.Invoice.GrandTotal), NativeValueKind.Decimal));
        }
        // Saved proof without reading the sales list: the voucher turns read-only (Cất disabled).
        await NativeWait.UntilAsync(async () =>
            {
                try { await input.RequireDisabledCommandAsync(await input.CaptureAsync(token), profile.SaveVoucher, token); return true; }
                catch (AutomationException error) when (error.Code == "COMMAND_STATE_MISMATCH") { return false; }
            }, disabled => disabled, "SAVE_UNCERTAIN",
            "Nút Cất chưa chuyển sang trạng thái đã ghi; không Cất lại.", token, timeoutMilliseconds: 15000);
        await input.InvokeWorkflowAsync(await input.CaptureAsync(token), profile.DiscardVoucher, token, leavesScope: true);
        await WaitEditor(context, false, token);
    }

    private (string Key, string Expected, NativeValueKind Kind)[] CustomerFields(WorkflowContext context)
    {
        var fields = new List<(string Key, string Expected, NativeValueKind Kind)>
        {
            ("customer", context.NativeValues?.GetValueOrDefault("customer_reference") ?? context.Invoice.Buyer.Code, NativeValueKind.Exact),
            ("customer_name", context.Invoice.Buyer.Name, NativeValueKind.Text)
        };
        if (!string.IsNullOrWhiteSpace(context.Invoice.Buyer.TaxCode))
            fields.Add(("customer_tax", context.Invoice.Buyer.TaxCode, NativeValueKind.Exact));
        return fields.ToArray();
    }

    private async Task<NativeFields> VerifyCustomer(WorkflowContext context, CancellationToken token, bool selecting = false)
    {
        await Tab(profile.ReceiptTab, token);
        // R20 exposes the selected customer's internal GUID through the combo
        // ValuePattern. Persist that reference only after the source-code commit
        // and business identity readback; recovery must match the same reference.
        var expected = CustomerFields(context);
        var fields = expected.ToDictionary(field => field.Key, field => profile.Fields[field.Key]);
        return await NativeWait.UntilAsync(async () => await input.ReadFieldsAsync(
            await input.CaptureAsync(token), fields, token),
            actual => expected.All(field => Match(field.Expected, actual.Values[field.Key].Value, field.Kind)
                || selecting && field.Key == "customer" && Guid.TryParse(actual.Values[field.Key].Value, out _)),
            "READBACK_MISMATCH", "Khách hàng MISA chưa khớp tên/MST sau khi chọn mã nguồn.", token);
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
        // R20's domestic VND editor has no currency control; VND is implicit in this surface.
        if (!string.Equals(context.Invoice.Currency, "VND", StringComparison.Ordinal))
            await Require("currency", context.Invoice.Currency, NativeValueKind.Exact, token);
        await Require("payment", context.Invoice.PaymentMethod, NativeValueKind.Text, token);
    }
    private async Task VerifyVoucherIdentity(WorkflowContext context, NativeFrame frame, CancellationToken token)
    {
        await input.SelectTabAsync(frame, profile.ReceiptTab, token);
        await Require("voucher_number", Saved(context, "voucher_number"), NativeValueKind.Exact, token);
    }
    private async Task<NativeFrame> BindEditor(WorkflowContext context, CancellationToken token)
    {
        ValidateDocument(context);
        return await input.TryBindWorkflowScopeAsync(BindTarget(context), profile.EditorId, null, token)
            ?? throw new AutomationException("EDITOR_NOT_FOUND", "Không có editor chứng từ đã chọn.");
    }
    private async Task<NativeFrame> BindSales(WorkflowContext context, CancellationToken token, bool activate = true)
        => await input.TryBindWorkflowScopeAsync(BindTarget(context, activate), profile.SalesScopeId, null, token)
            ?? throw new AutomationException("SALES_SURFACE_MISSING", "Mở màn hình Bán hàng của dữ liệu đã chọn.");

    private static void ValidateDocument(WorkflowContext context)
    {
        if (context.Invoice.Currency != "VND" || context.Invoice.PaymentMethod != "Tiền mặt"
            || context.Invoice.InvoiceType != "Hóa đơn bán hàng")
            throw new AutomationException("DOCUMENT_TYPE_UNSUPPORTED", "Profile chỉ hỗ trợ bán hàng trong nước VND/tiền mặt.");
        if (context.Invoice.Lines.Length > 5000 || context.Invoice.Lines.Select(line => line.LineId).Distinct().Count() != context.Invoice.Lines.Length
            || context.Invoice.Lines.Any(line => string.IsNullOrWhiteSpace(line.LineId)))
            throw new AutomationException("LINE_IDENTITY_INVALID", "Dòng hàng vượt giới hạn hoặc thiếu identity duy nhất.");
        if (context.Invoice.Lines.Any(line => string.IsNullOrWhiteSpace(line.Name)
                || string.IsNullOrWhiteSpace(line.Unit)))
            throw new AutomationException("ITEM_IDENTITY_MISSING", "Each item requires a name and unit; the item code is optional.");
        var texts = new[] { context.Invoice.Buyer.Code, context.Invoice.Identity.Number, context.Invoice.Identity.Series }
            .Concat(context.Invoice.Lines.SelectMany(line => new[] { line.ItemCode, line.Name, line.Unit }));
        if (texts.Any(text => text.Length > 32768 || text.Any(char.IsControl)))
            throw new AutomationException("INVALID_TEXT", "Dữ liệu chứa ký tự điều khiển hoặc vượt giới hạn nhập native.");
    }
    private async Task<NativeFrame?> WaitEditor(WorkflowContext context, bool visible, CancellationToken token)
    {
        if (visible)
            return await NativeWait.UntilAsync(() => input.TryBindWorkflowScopeAsync(BindTarget(context), profile.EditorId, null, token, waitForEnabled: true),
                frame => frame is not null, "EDITOR_NOT_FOUND", "Editor chưa sẵn sàng.", token, timeoutMilliseconds: 60000);
        await NativeWait.UntilAsync(() => input.WorkflowScopeExistsAsync(BindTarget(context), profile.EditorId, token),
            exists => !exists, "EDITOR_STILL_OPEN", "Editor chưa đóng; cần kiểm tra Cất/Bỏ.", token, timeoutMilliseconds: 15000);
        return null;
    }
    private WindowIdentity BindTarget(WorkflowContext context, bool activate = true)
    {
        if (context.ProfileRevision != profile.Revision)
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
        return activate ? windows.Focus((nint)handle, pid) : window;
    }
    private Task<Readback> Field(string key, NativeFrame frame, CancellationToken token) => input.ReadFieldAsync(frame, profile.Fields[key], token);
    private async Task<NativeFrame> Tab(string tab, CancellationToken token)
        => (await input.SelectTabAsync(await input.CaptureAsync(token), tab, token)).Frame;
    private async Task Require(string field, string expected, NativeValueKind kind, CancellationToken token)
        => await RequireFields(token, (field, expected, kind));
    private async Task RequireFields(CancellationToken token, params (string Key, string Expected, NativeValueKind Kind)[] fields)
    {
        await NativeWait.UntilAsync(async () => await input.ReadFieldsAsync(
            await input.CaptureAsync(token),
            fields.ToDictionary(field => field.Key, field => profile.Fields[field.Key]), token),
            actual => fields.All(field => Match(field.Expected, actual.Values[field.Key].Value, field.Kind)),
            "READBACK_MISMATCH", "Các trường chưa khớp nguồn: " + string.Join(", ", fields.Select(field => field.Key)), token);
    }
    private async Task Write(string field, string text, string expected, NativeValueKind kind, string commit, CancellationToken token)
    {
        Entering(field);
        await input.WriteFieldAsync(await input.CaptureAsync(token), profile.Fields[field], text, expected, kind,
            profile.Culture, commit, token, verifyAfterWrite: false);
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
    private bool Match(string expected, string? actual, NativeValueKind kind) => NativeComparison.Matches(expected, actual, kind, Culture);
    // Names compare ignoring case, spacing and punctuation; the item code is the authoritative identity.
    private static bool CatalogNameMatch(string expected, string? actual)
    {
        if (actual is null) return false;
        static string Key(string value) => new string(value.Normalize(NormalizationForm.FormC)
            .Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return Key(expected) == Key(actual);
    }
    private static string Decimal(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? throw new InvalidDataException("Thiếu số nguồn.");
    private static string Saved(WorkflowContext context, string key) => context.NativeValues?.GetValueOrDefault(key)
        ?? throw new AutomationException("CHECKPOINT_PROOF_MISSING", "Checkpoint thiếu native proof " + key);
    private static string Frame(NativeFrame frame) => JsonSerializer.Serialize(frame, ContractJson.Compact);
    private IReadOnlyList<string> SalesFinishedColumns() => [profile.SalesColumns["voucher_number"], profile.SalesColumns["number"],
        profile.SalesColumns["customer"], profile.SalesColumns["voucher_date"]];
    private static string TableHash(NativeTable table) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(table.Rows, ContractJson.Compact))));
}

