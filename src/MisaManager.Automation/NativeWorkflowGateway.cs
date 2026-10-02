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

public sealed class NativeWorkflowGateway(MisaWindowDiscovery windows, NativeInputService input,
    WorkflowProfile profile) : IWorkflowNativeGateway
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> checkpointSamples = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<long> workflowSamples = new();
    private volatile WorkflowActivity? activity;
    public WorkflowActivity? Activity => activity;
    private void Entering(string field) => activity = activity is { } current
        ? current with { Field = field, StartedAt = DateTimeOffset.UtcNow }
        : null;

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
                if (status == WorkflowStatus.InvoiceVerified)
                {
                    await input.BindAsync(BindTarget(context, activate: false), token);
                    if (await input.TryCloseItemCatalogAsync(token))
                        await WaitEditor(context, visible: true, token);
                }
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
        activity = new(context.Snapshot.RunId, transition.Action, DateTimeOffset.UtcNow);
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
                    if (transition.To == WorkflowStatus.Saved) await VerifySavedAndCloseEditor(context, token);
                    else await VerifyFinished(context, false, token);
                    var finishedFrame = Frame(await input.CaptureAsync(token));
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
                var sales = await BindSales(context, token);
                // R20 virtualizes horizontally hidden columns (including the
                // subtotal) with zero-sized headers. Duplicate detection only
                // needs the visible invoice number, so do not require hidden
                // columns at this checkpoint.
                var table = await input.ReadTableAsync(sales, profile.SalesGridId,
                    SalesBaselineColumns(), token);
                if (table.Rows.Any(row => Match(context.Invoice.Identity.Number, row[profile.SalesColumns["number"]], NativeValueKind.Exact)))
                    throw new AutomationException("POSSIBLE_DUPLICATE", "Số hóa đơn đã có trong bảng MISA; cần đối chiếu trước khi nhập.");
                await progress(new("sales_baseline", TableHash(table), Frame(table.Frame)), token);
                await input.InvokeWorkflowAsync(await input.CaptureAsync(token), profile.OpenVoucher, token);
                sent = true;
                await WaitEditor(context, true, token);
                await Tab(profile.ReceiptTab, token);
                var voucher = await Field("voucher_number", await input.CaptureAsync(token), token);
                if (string.IsNullOrWhiteSpace(voucher.Value)) throw new AutomationException("VOUCHER_ID_MISSING", "Chưa đọc được số chứng từ mới.", true);
                await progress(new("voucher_number", voucher.Value!, Frame(voucher.Frame)), token);
                await Tab(profile.ItemTab, token);
                var grid = await input.ReadTableAsync(await input.CaptureAsync(token), profile.ItemGridId,
                    [profile.Columns["code"], profile.Columns["name"]], token);
                if (grid.Rows.Any(row => row.Values.Any(value => !string.IsNullOrWhiteSpace(value))))
                    throw new AutomationException("VOUCHER_NOT_EMPTY", "Editor mới có dòng hàng; giữ nguyên để kiểm tra.", true);
            }
            else
            {
                var frame = await BindEditor(context, token);
                // Full reconciliation reads voucher_number on the receipt tab
                // itself. Avoid a duplicate identity traversal immediately
                // before those expensive table reads.
                if (transition.To is not (WorkflowStatus.TotalsVerified or WorkflowStatus.Saved))
                    await VerifyVoucherIdentity(context, frame, token);
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
                        await RequireFields(token,
                            ("voucher_date", context.Invoice.VoucherDate!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date),
                            ("posted_date", context.Invoice.PostedDate!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date));
                        await Tab(profile.InvoiceTab, token);
                        await WriteDate("issued_date", context.Invoice.Issued!.Value, token);
                        await Require("issued_date", context.Invoice.Issued!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date, token);
                        break;
                    case WorkflowStatus.InvoiceVerified:
                        await Tab(profile.InvoiceTab, token);
                        await Write("number", context.Invoice.Identity.Number, context.Invoice.Identity.Number, NativeValueKind.Exact, "Tab", token); sent = true;
                        if (context.Invoice.Identity.Series.Length > 0)
                            await Write("series", context.Invoice.Identity.Series, context.Invoice.Identity.Series, NativeValueKind.Exact, "Tab", token);
                        else
                        {
                            var series = await Field("series", await input.CaptureAsync(token), token);
                            await progress(new("series", series.Value ?? "", Frame(series.Frame)), token);
                        }
                        await VerifyInvoice(context, token);
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
                        // TotalsVerified already reconciled every row. Save performs a new full reconciliation.
                        await VerifyTotals(context, token);
                        break;
                    case WorkflowStatus.Saved:
                        var beforeSave = await ReadReconciliationAsync(context, token);
                        comparison = beforeSave;
                        if (!beforeSave.Matched) throw new AutomationException("RECONCILIATION_FAILED", "Chứng từ đã đổi; không Cất.");
                        Entering("save");
                        var saveFrame = await input.CaptureAsync(token);
                        var beforeSaveInput = input.WorkflowInputSequence;
                        try { await input.InvokeWorkflowAsync(saveFrame, profile.SaveVoucher, token, leavesScope: true); }
                        catch (AutomationException error) { saveSent = error.InputMayHaveBeenSent; throw; }
                        finally { saveSent |= input.WorkflowInputSequence != beforeSaveInput; }
                        sent = true;
                        await VerifySavedAndCloseEditor(context, token);
                        break;
                    case WorkflowStatus.Discarded:
                        if (context.Snapshot.Status == WorkflowStatus.SaveUncertain)
                        {
                            // Absence requires a fresh complete table, never saved
                            // error text or the register's currently visible page.
                            var salesFrame = await BindSales(context, token);
                            var register = await input.ReadCompleteTableAsync(salesFrame, profile.SalesGridId,
                                SalesFinishedColumns(), token);
                            if (register.Rows.Any(row =>
                                Match(Saved(context, "voucher_number"), row[profile.SalesColumns["voucher_number"]], NativeValueKind.Exact)
                                || Match(context.Invoice.Identity.Number, row[profile.SalesColumns["number"]], NativeValueKind.Exact)))
                                throw new AutomationException("DISCARD_SAVE_PROOF_PRESENT", "Register contains the voucher or invoice; verify Saved instead of discarding.");
                            await BindEditor(context, token);
                            await VerifyVoucherIdentity(context, await input.CaptureAsync(token), token);
                            await input.RequireCommandEnabledAsync(await input.CaptureAsync(token), profile.SaveVoucher, true, token);
                        }
                        if (context.NativeValues?.ContainsKey("sales_baseline") != true)
                            throw new AutomationException("DISCARD_BASELINE_MISSING", "Thiếu baseline trước khi mở chứng từ.");
                        await input.InvokeWorkflowAsync(await input.CaptureAsync(token), profile.DiscardVoucher, token,
                            leavesScope: true, discardChanges: true); sent = true;
                        await VerifyFinished(context, false, token);
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
            sent |= input.WorkflowInputSequence != inputSequence || error is AutomationException fault && fault.InputMayHaveBeenSent;
            var automation = error as AutomationException;
            return new(false, saveSent ? WorkflowStatus.SaveUncertain : transition.To == WorkflowStatus.Saved ? WorkflowStatus.Blocked
                    : sent ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked,
                "", error.Message, sent ? InputDisposition.Uncertain : InputDisposition.NotSent, sent,
                automation?.Code ?? "WORKFLOW_ERROR", error.ToString(), comparison);
        }
        finally { activity = null; RecordCheckpoint(transition.To.ToString(), checkpointStarted); }
    }

    private async Task<string> CreateMissingItemAndSelectAsync(WorkflowContext context, InvoiceLine line, int row,
        NativeCell codeCell, CancellationToken token)
    {
        if (profile.ItemNewCode is null || profile.ItemNewName is null || profile.ItemNewUnit is null
            || profile.ItemNewStock is null || profile.ItemNewTaxRate is null || profile.ItemNewSave is null)
            throw new AutomationException("ITEM_MAPPING_REQUIRED", "Profile chưa khai báo native create-item capability.");

        await input.CloseItemCatalogAsync(await input.CaptureAsync(token), token);
        await WaitEditor(context, visible: true, token);
        await Tab(profile.ItemTab, token);
        await input.OpenNewItemAsync(await input.CaptureAsync(token), codeCell, token);
        var newItem = await input.BindNewItemAsync(token);
        var code = CreateGeneratedItemCode(line);
        await input.WriteFieldAsync(newItem, profile.ItemNewCode, code, code, NativeValueKind.Exact,
            profile.Culture, "Tab", token, verifyAfterWrite: false);
        await input.WriteFieldAsync(await input.CaptureAsync(token), profile.ItemNewName,
            line.Name, line.Name, NativeValueKind.Text, profile.Culture, "Tab", token,
            verifyAfterWrite: false);
        await input.WriteFieldAsync(await input.CaptureAsync(token), profile.ItemNewUnit,
            line.Unit, line.Unit, NativeValueKind.Text, profile.Culture, "Tab", token,
            allowUnverifiedFocus: true, verifyAfterWrite: false);
        await input.WriteFieldAsync(await input.CaptureAsync(token), profile.ItemNewStock,
            "KHH", "KHH", NativeValueKind.Exact, profile.Culture, "Tab", token,
            allowUnverifiedFocus: true, verifyAfterWrite: false);
        await input.WriteFieldAsync(await input.CaptureAsync(token), profile.ItemNewTaxRate,
            line.TaxRate, line.TaxRate, NativeValueKind.TaxRate, profile.Culture, "Tab", token,
            allowUnverifiedFocus: true, verifyAfterWrite: false);
        var required = new Dictionary<string, NativeSelector>(StringComparer.Ordinal)
        {
            ["code"] = profile.ItemNewCode,
            ["name"] = profile.ItemNewName,
            ["unit"] = profile.ItemNewUnit,
            ["stock"] = profile.ItemNewStock,
            ["tax"] = profile.ItemNewTaxRate
        };
        await NativeWait.UntilAsync(async () => await input.ReadFieldsAsync(await input.CaptureAsync(token), required, token),
            values => Match(code, values.Values["code"].Value, NativeValueKind.Exact)
                && CatalogNameMatch(line.Name, values.Values["name"].Value)
                && Match(line.Unit, values.Values["unit"].Value, NativeValueKind.Text)
                && Match("KHH", values.Values["stock"].Value, NativeValueKind.Exact)
                && Match(line.TaxRate, values.Values["tax"].Value, NativeValueKind.TaxRate),
            "ITEM_CREATE_READBACK_FAILED", $"Row {row + 1}: new-item fields do not match before native save.", token);
        await input.InvokeWorkflowAsync(await input.CaptureAsync(token), profile.ItemNewSave, token,
            leavesScope: true);
        await WaitEditor(context, visible: true, token);
        await Tab(profile.ItemTab, token);

        // Reopen the catalog after creation. The newly created row must still
        // pass the same exact-name/exact-unit/unique-candidate/readback gates.
        await input.OpenItemCatalogAsync(await input.CaptureAsync(token), codeCell, token);
        var catalog = await input.BindItemCatalogAsync(token);
        await input.WriteFieldAsync(catalog, new("4", ControlType: "ComboBox"),
            line.Name, line.Name, NativeValueKind.Text, profile.Culture, "Enter", token,
            allowUnverifiedFocus: true, verifyAfterWrite: false);
        var columns = new[] { profile.Columns["code"], profile.Columns["name"], profile.Columns["unit"] };
        string? previous = null;
        var candidates = await NativeWait.UntilAsync(async () =>
                await input.ReadCatalogTableAsync(await input.CaptureAsync(token), "grdData", columns, token),
            table =>
            {
                var fingerprint = TableHash(table);
                var settled = previous == fingerprint;
                previous = fingerprint;
                return settled;
            }, "ITEM_CREATE_READBACK_FAILED", $"Row {row + 1}: created item did not appear in native catalog.", token, timeoutMilliseconds: 30000);
        var matches = candidates.Rows.Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.GetValueOrDefault(profile.Columns["code"]))
                && CatalogNameMatch(line.Name, candidate.GetValueOrDefault(profile.Columns["name"]) ?? "")
                && Match(line.Unit, candidate.GetValueOrDefault(profile.Columns["unit"]) ?? "", NativeValueKind.Text))
            .ToArray();
        if (matches.Length != 1)
            throw new AutomationException(matches.Length == 0 ? "ITEM_CREATE_READBACK_FAILED" : "ITEM_AMBIGUOUS",
                $"Row {row + 1}: native catalog returned {matches.Length} created-item matches for '{line.Name}' / '{line.Unit}'.");
        var nativeCode = matches[0][profile.Columns["code"]].Trim();
        if (!Match(code, nativeCode, NativeValueKind.Exact))
            throw new AutomationException("ITEM_CREATE_READBACK_FAILED",
                $"Row {row + 1}: native catalog code '{nativeCode}' differs from generated code '{code}'.");
        await input.SelectItemCatalogRowAsync(await input.CaptureAsync(token),
            Array.FindIndex(candidates.Rows.ToArray(), candidate =>
                string.Equals(candidate.GetValueOrDefault(profile.Columns["code"])?.Trim(), nativeCode,
                    StringComparison.Ordinal)), nativeCode, token);
        await input.InvokeAsync(await input.CaptureAsync(token), "btnOK", "", token);
        await WaitEditor(context, visible: true, token);
        await Tab(profile.ItemTab, token);
        await RequireCell(row, nativeCode, "code", nativeCode, NativeValueKind.Exact, token);
        return nativeCode;
    }

    private static string CreateGeneratedItemCode(InvoiceLine line)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line.Name + "|" + line.Unit)))[..10];
        return "FU_" + digest;
    }

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
            var existingCode = await NativeWait.UntilAsync(async () => await input.ReadCellAsync(
                await input.CaptureAsync(token), codeCell, token, waitForCode: false),
                read => read.Value is not null, "GRID_ROW_NOT_READY", $"Row {row + 1} is not ready.", token);
            var catalogSelected = false;
            if (string.IsNullOrWhiteSpace(line.ItemCode) && code.Length == 0
                && !string.IsNullOrWhiteSpace(existingCode.Value))
            {
                var existingName = await Cell(row, existingCode.Value.Trim(), "name", token);
                if (CatalogNameMatch(line.Name, existingName.Value))
                    code = existingCode.Value.Trim();
            }
            if (!string.IsNullOrWhiteSpace(existingCode.Value)
                && !Match(code, existingCode.Value, NativeValueKind.Exact))
                throw new AutomationException("GRID_ROW_CHANGED",
                    $"Row {row + 1}: native code '{existingCode.Value}' differs from source code '{code}'; refusing to overwrite.", true);
            if (string.IsNullOrWhiteSpace(existingCode.Value))
            {
                if (code.Length == 0)
                {
                    await input.OpenItemCatalogAsync(await input.CaptureAsync(token), codeCell, token);
                    var catalog = await input.BindItemCatalogAsync(token);
                    await input.WriteFieldAsync(catalog, new("4", ControlType: "ComboBox"),
                        line.Name, line.Name, NativeValueKind.Text, profile.Culture, "Enter", token,
                        allowUnverifiedFocus: true, verifyAfterWrite: false);
                    var columns = new[] { profile.Columns["code"], profile.Columns["name"], profile.Columns["unit"] };
                    string? previous = null;
                    var candidates = await NativeWait.UntilAsync(async () =>
                        await input.ReadCatalogTableAsync(await input.CaptureAsync(token), "grdData", columns, token),
                        table =>
                        {
                            var fingerprint = TableHash(table);
                            var settled = previous == fingerprint;
                            previous = fingerprint;
                            return settled;
                        }, "ITEM_NOT_FOUND", $"Row {row + 1}: MISA has no exact item name '{line.Name}'.", token, timeoutMilliseconds: 30000);
                    var names = candidates.Rows.Where(candidate =>
                        !string.IsNullOrWhiteSpace(candidate[profile.Columns["code"]])
                        && CatalogNameMatch(line.Name, candidate[profile.Columns["name"]])).ToArray();
                    if (names.Length == 0)
                    {
                        // Some R20 catalogs normalize spacing in product-size
                        // suffixes (for example "10kg" versus "10 kg").
                        // Search a stable prefix, then require a unique
                        // normalized full name before accepting a candidate.
                        var prefix = line.Name.StartsWith("NPK \"Bông lúa\"", StringComparison.OrdinalIgnoreCase)
                            ? "NPK Bông lúa"
                            : line.Name.Contains('(', StringComparison.Ordinal)
                            ? line.Name[..line.Name.IndexOf('(')].TrimEnd()
                            : line.Name.Contains(" - ", StringComparison.Ordinal)
                                ? line.Name.Split(" - ", 2, StringSplitOptions.None)[0].Trim()
                                : line.Name.Length > 24 ? line.Name[..24].TrimEnd() : line.Name;
                        await input.WriteFieldAsync(await input.CaptureAsync(token), new("4", ControlType: "ComboBox"),
                            prefix, prefix, NativeValueKind.Text, profile.Culture, "Enter", token,
                            allowUnverifiedFocus: true, verifyAfterWrite: false);
                        previous = null;
                        candidates = await NativeWait.UntilAsync(async () =>
                            await input.ReadCatalogTableAsync(await input.CaptureAsync(token), "grdData", columns, token),
                            table =>
                            {
                                var fingerprint = TableHash(table);
                                var settled = previous == fingerprint;
                                previous = fingerprint;
                                return settled;
                            }, "ITEM_NOT_FOUND", $"Row {row + 1}: MISA has no catalog prefix '{prefix}'.", token, timeoutMilliseconds: 30000);
                        names = candidates.Rows.Where(candidate =>
                            !string.IsNullOrWhiteSpace(candidate[profile.Columns["code"]])
                            && CatalogNameMatch(line.Name, candidate[profile.Columns["name"]])).ToArray();
                        if (names.Length == 0)
                        {
                            // A catalog may tokenize punctuation differently
                            // (for example, a dash before the package size).
                            // Retry once with the first two words, then keep the
                            // same exact normalized-name and unit gates below.
                            var words = line.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            var broadPrefix = words.Length >= 2 ? string.Join(' ', words.Take(2)) : prefix;
                            if (!string.Equals(broadPrefix, prefix, StringComparison.Ordinal))
                            {
                                await input.WriteFieldAsync(await input.CaptureAsync(token), new("4", ControlType: "ComboBox"),
                                    broadPrefix, broadPrefix, NativeValueKind.Text, profile.Culture, "Enter", token,
                                    allowUnverifiedFocus: true, verifyAfterWrite: false);
                                previous = null;
                                candidates = await NativeWait.UntilAsync(async () =>
                                    await input.ReadCatalogTableAsync(await input.CaptureAsync(token), "grdData", columns, token),
                                    table =>
                                    {
                                        var fingerprint = TableHash(table);
                                        var settled = previous == fingerprint;
                                        previous = fingerprint;
                                        return settled;
                                    }, "ITEM_NOT_FOUND", $"Row {row + 1}: MISA has no broad catalog prefix '{broadPrefix}'.", token, timeoutMilliseconds: 30000);
                                names = candidates.Rows.Where(candidate =>
                                    !string.IsNullOrWhiteSpace(candidate[profile.Columns["code"]])
                                    && CatalogNameMatch(line.Name, candidate[profile.Columns["name"]])).ToArray();
                                if (names.Length == 0 && words.Length >= 2)
                                {
                                    var shortestPrefix = words[0];
                                    await input.WriteFieldAsync(await input.CaptureAsync(token), new("4", ControlType: "ComboBox"),
                                        shortestPrefix, shortestPrefix, NativeValueKind.Text, profile.Culture, "Enter", token,
                                        allowUnverifiedFocus: true, verifyAfterWrite: false);
                                    previous = null;
                                    candidates = await NativeWait.UntilAsync(async () =>
                                        await input.ReadCatalogTableAsync(await input.CaptureAsync(token), "grdData", columns, token),
                                        table =>
                                        {
                                            var fingerprint = TableHash(table);
                                            var settled = previous == fingerprint;
                                            previous = fingerprint;
                                            return settled;
                                        }, "ITEM_NOT_FOUND", $"Row {row + 1}: MISA has no short catalog prefix '{shortestPrefix}'.", token, timeoutMilliseconds: 30000);
                                    names = candidates.Rows.Where(candidate =>
                                        !string.IsNullOrWhiteSpace(candidate[profile.Columns["code"]])
                                        && CatalogNameMatch(line.Name, candidate[profile.Columns["name"]])).ToArray();
                                }
                            }
                        }
                    }
                    if (names.Length == 0)
                    {
                        // Some catalogs index a product by its concentration or
                        // package token rather than the leading brand token.
                        // Try a bounded set of stable tokens, then apply the
                        // same full-name and unit filters below.
                        var alternateTerms = line.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Select(term => term.Trim('(', ')', '"', '\''))
                            .Where(term => term.Length >= 3)
                            .Reverse()
                            .Take(4);
                        foreach (var term in alternateTerms)
                        {
                            await input.WriteFieldAsync(await input.CaptureAsync(token), new("4", ControlType: "ComboBox"),
                                term, term, NativeValueKind.Text, profile.Culture, "Enter", token,
                                allowUnverifiedFocus: true, verifyAfterWrite: false);
                            previous = null;
                            candidates = await NativeWait.UntilAsync(async () =>
                                await input.ReadCatalogTableAsync(await input.CaptureAsync(token), "grdData", columns, token),
                                table =>
                                {
                                    var fingerprint = TableHash(table);
                                    var settled = previous == fingerprint;
                                    previous = fingerprint;
                                    return settled;
                                }, "ITEM_NOT_FOUND", $"Row {row + 1}: MISA has no token catalog search '{term}'.", token, timeoutMilliseconds: 30000);
                            names = candidates.Rows.Where(candidate =>
                                !string.IsNullOrWhiteSpace(candidate[profile.Columns["code"]])
                                && CatalogNameMatch(line.Name, candidate[profile.Columns["name"]])).ToArray();
                            if (names.Length > 0) break;
                        }
                    }
                    if (names.Length == 0)
                    {
                        // A missing exact row is the native trigger for the
                        // supported create-item flow. Creation is performed in
                        // MISA's own dialog, then the catalog is reopened and
                        // the new row is selected/read back before line input.
                        code = await CreateMissingItemAndSelectAsync(context, line, row, codeCell, token);
                        catalogSelected = true;
                    }
                    else
                    {
                        var exactUnits = names.Where(candidate =>
                            Match(line.Unit, candidate[profile.Columns["unit"]], NativeValueKind.Text)).ToArray();
                        var matches = exactUnits.Length > 0 ? exactUnits : names;
                        if (matches.Length != 1)
                            throw new AutomationException(matches.Length == 0 ? "ITEM_UNIT_MISMATCH" : "ITEM_AMBIGUOUS",
                                $"Row {row + 1}: MISA returned {matches.Length} exact matches for '{line.Name}' / '{line.Unit}'. "
                                + (matches.Length == 0
                                    ? "Catalog readback: " + string.Join(" | ", candidates.Rows
                                        .Where(candidate => !string.IsNullOrWhiteSpace(candidate[profile.Columns["name"]]))
                                        .Take(12)
                                        .Select(candidate => $"{candidate[profile.Columns["code"]]}:{candidate[profile.Columns["name"]]}[{candidate[profile.Columns["unit"]]}]"))
                                    : ""));
                        code = matches[0][profile.Columns["code"]].Trim();
                        await input.SelectItemCatalogRowAsync(await input.CaptureAsync(token),
                            Array.FindIndex(candidates.Rows.ToArray(), candidate =>
                                string.Equals(candidate.GetValueOrDefault(profile.Columns["code"])?.Trim(), code,
                                    StringComparison.Ordinal)), code, token);
                        // Use the same host-root click path as direct native
                        // invoke; the catalog's child HWND can ignore a
                        // targeted click after a checkbox selection.
                        await input.InvokeAsync(await input.CaptureAsync(token), "btnOK", "", token);
                        await WaitEditor(context, visible: true, token);
                        await Tab(profile.ItemTab, token);
                        await RequireCell(row, code, "code", code, NativeValueKind.Exact, token);
                        catalogSelected = true;
                    }
                }
                if (!catalogSelected)
                    await input.WriteCellAsync(await input.CaptureAsync(token), codeCell, code, code,
                        NativeValueKind.Exact, profile.Culture, token);
            }
            var boundRow = new NativeCell(profile.ItemGridId, row, profile.Columns["code"], profile.Columns["code"], code);
            var metadata = await NativeWait.UntilAsync(async () => await input.ReadRowAsync(
                await input.CaptureAsync(token), boundRow,
                [profile.Columns["name"], profile.Columns["unit"], profile.Columns["debit_account"]], token),
                actual => CatalogNameMatch(line.Name, actual.Values[profile.Columns["name"]].Value),
                "READBACK_MISMATCH", $"Row {row + 1}: native item name differs from source.", token);
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
            var derivedPrice = context.Invoice.ImportConfirmation?.CalculateUnitPriceFromAmount == true;
            var writes = derivedPrice
                ? new[]
                {
                    new NativeCellWrite(profile.Columns["quantity"], expected.Quantity.ToString(Culture)),
                    new NativeCellWrite(profile.Columns["amount"], line.AmountBeforeTax!.Value.ToString(Culture))
                }
                : new[]
                {
                    new NativeCellWrite(profile.Columns["quantity"], expected.Quantity.ToString(Culture)),
                    new NativeCellWrite(profile.Columns["unit_price"], expected.Price.ToString(Culture)),
                    new NativeCellWrite(profile.Columns["amount"], line.AmountBeforeTax!.Value.ToString(Culture))
                };
            var rowValues = await input.WriteRowAsync(await input.CaptureAsync(token),
                new NativeCell(profile.ItemGridId, row, profile.Columns["code"], profile.Columns["code"], code),
                writes,
                [profile.Columns["name"], profile.Columns["debit_account"], profile.Columns["unit"],
                    profile.Columns["quantity"], profile.Columns["amount"], profile.Columns["unit_price"]],
                actual =>
                {
                    var values = actual.Values;
                    var account = values.GetValueOrDefault(profile.Columns["debit_account"])?.Value;
                    var unit = values.GetValueOrDefault(profile.Columns["unit"])?.Value;
                    var unitPrice = values.GetValueOrDefault(profile.Columns["unit_price"])?.Value;
                    return values.TryGetValue(profile.Columns["code"], out var nativeCode)
                        && Match(code, nativeCode.Value, NativeValueKind.Exact)
                        && CatalogNameMatch(line.Name, values.GetValueOrDefault(profile.Columns["name"])?.Value)
                        && Match(expectedAccount, account, NativeValueKind.Exact)
                        && Match(expected.Unit, unit, NativeValueKind.Text)
                        && Match(Decimal(expected.Quantity), values.GetValueOrDefault(profile.Columns["quantity"])?.Value, NativeValueKind.Decimal)
                        && Match(Decimal(line.AmountBeforeTax), values.GetValueOrDefault(profile.Columns["amount"])?.Value, NativeValueKind.Decimal)
                        && (derivedPrice
                            ? NativeComparison.MatchesDerivedUnitPrice(expected.Quantity, line.AmountBeforeTax!.Value, unitPrice, Culture)
                            : Match(Decimal(expected.Price), unitPrice, NativeValueKind.Decimal));
                }, token, column => Entering("line:" + line.LineId + ":"
                    + profile.Columns.Single(pair => pair.Value == column).Key));
            var unitPrice = rowValues.Values[profile.Columns["unit_price"]];
            var accountValue = rowValues.Values[profile.Columns["debit_account"]].Value;
            var unitValue = rowValues.Values[profile.Columns["unit"]].Value;
            if (string.IsNullOrWhiteSpace(accountValue) || !accountValue.All(char.IsDigit))
                throw new AutomationException("ACCOUNT_READBACK_INVALID", $"Dòng {row + 1}, TK tiền/chi phí không phải mã tài khoản: {accountValue}");
            if (!Match(expected.Unit, unitValue, NativeValueKind.Text))
                throw new AutomationException("ITEM_UNIT_MISMATCH",
                    $"Dòng {row + 1}: mã hàng '{code}' có ĐVT native '{unitValue}', khác ĐVT nguồn '{line.Unit}'. "
                    + "Cần sửa mã/ĐVT nguồn hoặc xác nhận ánh xạ; không tự đổi ĐVT.", true);
            var derived = new Dictionary<string, string> { ["account:" + line.LineId] = expectedAccount };
            if (derivedPrice)
            {
                derived["price:" + line.LineId] = unitPrice.Value!;
            }
            codes.Add(code);
            await progress(new("line:" + line.LineId, code, Frame(rowValues.Frame), row, derived), token);
        }
        await Tab(profile.TaxTab, token);
        for (var row = 0; row < context.Invoice.Lines.Length; row++)
        {
            var line = context.Invoice.Lines[row];
            var code = codes[row];
            var tax = line.TaxRate.TrimEnd('%').Trim();
            Entering("line:" + line.LineId + ":tax_rate");
            await input.WriteCellAsync(await input.CaptureAsync(token), CellTarget(row, code, "tax_rate"), tax, tax,
                NativeValueKind.TaxRate, profile.Culture, token);
            await RequireCell(row, code, "tax_amount", Decimal(line.TaxAmount), NativeValueKind.Decimal, token);
        }
    }

    public async Task<ReconciliationResult> ReconcileAsync(WorkflowContext context, CancellationToken token)
    {
        activity = new(context.Snapshot.RunId, "reconcile", DateTimeOffset.UtcNow);
        try
        {
            await BindEditor(context, token);
            return await ReadReconciliationAsync(context, token);
        }
        finally { activity = null; }
    }

    private async Task<ReconciliationResult> ReadReconciliationAsync(WorkflowContext context, CancellationToken token)
    {
        Entering("reconcile");
        return await NativeWait.UntilAsync(() => ReadReconciliationPassAsync(context, token), result => result.Matched,
            "RECONCILIATION_PENDING", "MISA values have not settled.", token, returnLastMismatch: true);
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
        await CompareFields(profile.ReceiptTab,
            [("voucher_number", Saved(context, "voucher_number"), NativeValueKind.Exact),
            .. CustomerFields(context),
            ("voucher_date", context.Invoice.VoucherDate!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date),
            ("posted_date", context.Invoice.PostedDate!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date)]);
        await CompareFields(profile.InvoiceTab,
            ("number", context.Invoice.Identity.Number, NativeValueKind.Exact),
            ("series", context.Invoice.Identity.Series.Length > 0 ? context.Invoice.Identity.Series : Saved(context, "series"), NativeValueKind.Exact),
            ("issued_date", context.Invoice.Issued!.Value.ToString("yyyy-MM-dd"), NativeValueKind.Date),
            ("payment", context.Invoice.PaymentMethod, NativeValueKind.Text));
        // BindEditor/ValidateDocument require the exact domestic cash editor.
        // R20 has no currency control on this VND-only surface.
        values.Add(new("currency", context.Invoice.Currency, "VND",
            context.Invoice.Currency == "VND", "R20.DomesticCashEditor:implicit_currency"));
        foreach (var taxTab in new[] { false, true })
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
                        ("unit_price", context.Invoice.ImportConfirmation?.CalculateUnitPriceFromAmount == true
                            ? Saved(context, "price:" + line.LineId) : Decimal(expectedLine.Price),
                            context.Invoice.ImportConfirmation?.CalculateUnitPriceFromAmount == true ? NativeValueKind.Exact : NativeValueKind.Decimal) };
                foreach (var (key, wanted, kind) in expected)
                {
                    var actual = native.Row?.GetValueOrDefault(profile.Columns[key]);
                    var source = native.Row is not null && native.Index < table.Sources.Count
                        ? table.Sources[native.Index].GetValueOrDefault(profile.Columns[key]) ?? "UIA3.GridPattern"
                        : "UIA3.GridPattern:missing_row";
                    var matched = key == "name" ? CatalogNameMatch(wanted, actual) : Match(wanted, actual, kind);
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
        }
        await CompareFields(profile.ItemTab,
            ("subtotal", Decimal(context.Invoice.Subtotal), NativeValueKind.Decimal),
            ("tax_total", Decimal(context.Invoice.TaxTotal), NativeValueKind.Decimal),
            ("grand_total", Decimal(context.Invoice.GrandTotal), NativeValueKind.Decimal));
        return new(context.Invoice.InvoiceId, context.Invoice.Revision, DateTimeOffset.UtcNow, values, context.Invoice.Source.Sha256);
    }

    private async Task VerifyFinished(WorkflowContext context, bool save, CancellationToken token)
    {
        // Saving R20 can leave the voucher editor open in a read-only,
        // saved-invoice state with the Save command disabled. The sales
        // register readback below is authoritative for the save result, so do
        // not require the editor to close before checking it. Discard still
        // requires the editor to disappear.
        if (!save)
            await WaitEditor(context, false, token);
        var frame = await BindSales(context, token);
        bool MatchesResult(NativeTable table)
        {
            frame = table.Frame;
            if (!save) return TableHash(table) == Saved(context, "sales_baseline");
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
            save ? SalesFinishedColumns() : SalesBaselineColumns(), token), MatchesResult,
            save ? "SAVE_UNCERTAIN" : "DISCARD_UNCERTAIN", "Danh sách MISA chưa xác nhận kết quả Cất/Bỏ; không gửi lại thao tác.", token,
            timeoutMilliseconds: save ? 15000 : 10000, returnLastMismatch: true);
        if (!MatchesResult(proof))
        {
            if (!save) throw new AutomationException("DISCARD_UNCERTAIN", "Native sales baseline has not been restored.");
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

    private async Task VerifySavedAndCloseEditor(WorkflowContext context, CancellationToken token)
    {
        var preservedTax = await input.TryPreserveSourceTaxAsync(BindTarget(context, activate: false), profile.EditorId, null,
            profile.Fields["voucher_number"], Saved(context, "voucher_number"), context.Invoice.TaxTotal!.Value,
            context.Invoice.Lines.Length, token);
        await VerifyFinished(context, true, token);
        var window = BindTarget(context, activate: false);
        if (!await input.WorkflowScopeExistsAsync(window, profile.EditorId, token)) return;
        var savedEditor = await BindEditor(context, token);
        await VerifyVoucherIdentity(context, savedEditor, token);
        if (preservedTax)
        {
            await Tab(profile.ItemTab, token);
            await RequireFields(token,
                ("subtotal", Decimal(context.Invoice.Subtotal), NativeValueKind.Decimal),
                ("tax_total", Decimal(context.Invoice.TaxTotal), NativeValueKind.Decimal),
                ("grand_total", Decimal(context.Invoice.GrandTotal), NativeValueKind.Decimal));
        }
        await input.RequireDisabledCommandAsync(await input.CaptureAsync(token), profile.SaveVoucher, token);
        await input.InvokeWorkflowAsync(await input.CaptureAsync(token), profile.DiscardVoucher, token, leavesScope: true);
        await WaitEditor(context, false, token);
        await VerifyFinished(context, true, token);
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
    private async Task VerifyTotals(WorkflowContext context, CancellationToken token)
    {
        await Tab(profile.ItemTab, token);
        await RequireFields(token,
            ("subtotal", Decimal(context.Invoice.Subtotal), NativeValueKind.Decimal),
            ("tax_total", Decimal(context.Invoice.TaxTotal), NativeValueKind.Decimal),
            ("grand_total", Decimal(context.Invoice.GrandTotal), NativeValueKind.Decimal));
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
        var window = activate ? windows.Focus((nint)handle, pid) : windows.Bind((nint)handle, pid);
        if (window.ProcessStartUtc != start.UtcDateTime || !string.Equals(window.ExecutablePath, parts.GetValueOrDefault("exe"), StringComparison.OrdinalIgnoreCase)
            || !window.Title.StartsWith(profile.VersionTitle + " - ", StringComparison.Ordinal))
            throw new AutomationException("WINDOW_IDENTITY_CHANGED", "Phiên hoặc phiên bản MISA thay đổi; cần rebind.");
        return window;
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
        => Write(field, day.ToString("dd/MM/yyyy"), day.ToString("yyyy-MM-dd"), NativeValueKind.Date, "Tab", token);
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
    private static bool CatalogNameMatch(string expected, string? actual)
    {
        if (actual is null) return false;
        static string Key(string value) => new string(value.Normalize(NormalizationForm.FormC)
            .Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var expectedKey = Key(expected);
        var actualKey = Key(actual);
        if (expectedKey == actualKey) return true;

        // Ignore only the package word "bao"; retain size, volume and supplier
        // suffixes so distinct catalog variants cannot share a shortened stem.
        static string PackageKey(string value)
        {
            value = Regex.Replace(value, @"\(\s*bao\s*\)", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            value = Regex.Replace(value, @"\bbao\s+(?=\d+(?:[.,]\d+)?\s*(?:kg|g)\b)", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return Key(value);
        }
        if (expectedKey.Length > 0 && PackageKey(expected) == PackageKey(actual))
            return true;

        // The current native catalog abbreviates only these observed supplier
        // variants. Preserve the size and marker to exclude other catalog rows.
        return (expectedKey == Key("Chani 300EC - 100ml (\u0110N Nicotex)")
                && actualKey == Key("Chani 300EC - 100ml (\u0110N)"))
            || (expectedKey == Key("Phân NPK 16-16-8+13S HCQN (BM-BV)")
                && actualKey == Key("Phân NPK 16-16-8+13S HCQN (BM-BV) (50Kg)"))
            || (expectedKey == Key("NPK \"Bông lúa\" 5.10.3 L25")
                && actualKey.StartsWith(Key("NPK \"Bông lúa\" 5.10.3 L2"), StringComparison.Ordinal));
    }
    private static string Decimal(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? throw new InvalidDataException("Thiếu số nguồn.");
    private static string Saved(WorkflowContext context, string key) => context.NativeValues?.GetValueOrDefault(key)
        ?? throw new AutomationException("CHECKPOINT_PROOF_MISSING", "Checkpoint thiếu native proof " + key);
    private static string Frame(NativeFrame frame) => JsonSerializer.Serialize(frame, ContractJson.Compact);
    private IReadOnlyList<string> SalesBaselineColumns() => [profile.SalesColumns["number"]];
    private IReadOnlyList<string> SalesFinishedColumns() => [profile.SalesColumns["voucher_number"], profile.SalesColumns["number"],
        profile.SalesColumns["customer"], profile.SalesColumns["voucher_date"]];
    private static string TableHash(NativeTable table) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(table.Rows, ContractJson.Compact))));
}

