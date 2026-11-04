using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text;
using System.Windows.Input;
using MisaManager.Automation;
using MisaManager.Core;

namespace MisaManager.App;


public enum MisaConnection { Unknown, Connected, Lost }

public sealed record ScreenCheckView(bool Ok, string Summary, string Details, ScreenCheckReport? Report = null);

/// <summary>
/// Latest "Đối chiếu hóa đơn" against the voucher open in MISA (outside a run). <see cref="Invoice"/> is the Excel invoice with
/// MISA's number (null when the workbook has none); <see cref="Selection"/> is the list selection it stays shown for.
/// </summary>
public sealed record OpenVoucherCheck(InvoiceDocument? Invoice, OpenVoucherComparison? Comparison, string? Error, DateTimeOffset At,
    string? Selection);

public sealed class WorkflowViewModel : INotifyPropertyChanged
{
    private readonly AppServices services;
    private string status = "Sẵn sàng. Chọn hóa đơn để tiếp tục.";
    private bool running;
    private bool stopRequested;
    // Stopped by the user with ticked invoices left; Tiếp tục runs the rest of the batch.
    private bool batchStopped;
    private readonly HashSet<string> checkedFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> handledInBatch = new(StringComparer.Ordinal);
    private int batchIndex;
    private bool probing;
    private string? operation;
    private MisaConnection connection = MisaConnection.Unknown;
    private string connectionDetail = "";
    private (string? Code, string Message)? lastError;
    private DateTimeOffset? performanceStartedAt;
    private DateTimeOffset? performanceEndedAt;
    // Time of earlier segments of a batch that was stopped and continued.
    private TimeSpan performanceCarried;
    private string? performanceInvoice;
    private string performanceCurrent = "Chưa chạy";
    private string performanceElapsed = "0 ms";
    private string? activeNativeTraceId;
    private PerformanceTraceEntry? activeNativeTrace;
    private readonly Dictionary<string, int> performanceTraceIndexes = new(StringComparer.Ordinal);

    public WorkflowViewModel(AppServices services)
    {
        this.services = services;
        AddCommand = new ActionCommand(async () => await RunAsync());
        ResumeCommand = new ActionCommand(ResumeAsync);
    }

    public InvoiceDocument? SelectedInvoice { get; set; }
    public OpenVoucherCheck? OpenVoucherCheck { get; private set; }
    public ICommand AddCommand { get; }
    public ICommand ResumeCommand { get; }
    public bool IsRunning => running;
    public WorkflowContext? CurrentContext => services.Workflow.Current;
    public string Status
    {
        get => status;
        private set { status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? WorkflowChanged;
    public event EventHandler<WorkflowErrorEventArgs>? ErrorRaised;
    public event EventHandler? SessionReset;

    private void NotifyWorkflowChanged()
    {
        PropertyChanged?.Invoke(this, new(nameof(CurrentContext)));
        PropertyChanged?.Invoke(this, new(nameof(CanEditSource)));
        PropertyChanged?.Invoke(this, new(nameof(CanStart)));
        PropertyChanged?.Invoke(this, new(nameof(CanResume)));
        PropertyChanged?.Invoke(this, new(nameof(State)));
        WorkflowChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RestoreAsync()
    {
        try
        {
            await services.Initialization;
            await LoadHistoryAsync();
            if (services.Workflow.Current is { } current)
                Status = current.Snapshot.LastMessage ?? $"Đã khôi phục phiên {current.Invoice.Identity.Number}: {current.Snapshot.Status}.";
            NotifyWorkflowChanged();
        }
        catch (Exception error) { Status = "Không đọc được phiên trước: " + error.Message; }
    }

    /// <summary>
    /// Dừng ngay: the action in progress (typing a field, a click, a tab switch) completes, then the invoice stops
    /// there as "Tạm dừng" (not an error). Cất, Bỏ and creating an item are never interrupted. On the next start the
    /// half-entered voucher is Bỏ and that invoice is entered again from scratch.
    /// </summary>
    public void RequestStop()
    {
        if (!running || stopRequested) return;
        stopRequested = true;
        services.Workflow.StopSignal.Request();
        Status = "Đang dừng: tool dừng ngay sau thao tác đang làm (nhập ô, click, chuyển tab) và lưu hồ sơ là Tạm dừng.";
        NotifyWorkflowChanged();
    }

    public bool IsStopping => running && stopRequested;

    /// <summary>
    /// Cập nhật app: releases the old checkpoint (lỗi, chưa rõ, tạm dừng) like a new Thêm vào MISA does. History keeps
    /// the uncertainty, so that invoice still needs register proof before it is entered again. Returns an error or null.
    /// </summary>
    public async Task<string?> ReleaseCheckpointForUpdateAsync()
    {
        if (running) return "Đang chạy Thêm vào MISA. Bấm Dừng ngay, chờ dừng hẳn rồi mới cập nhật.";
        batchStopped = false;
        if (services.Workflow.Current is not { } old) { NotifyWorkflowChanged(); return null; }
        var abandoned = await services.AbandonStoppedSessionAsync();
        if (abandoned.Status != OperationStatuses.Completed) return abandoned.Message;
        History = [old with { SessionDetached = true }, .. History.Where(run => run.Snapshot.RunId != old.Snapshot.RunId)];
        lastError = null;
        SessionReset?.Invoke(this, EventArgs.Empty);
        NotifyWorkflowChanged();
        return null;
    }

    public async Task ResumeAsync()
    {
        if (running) return;
        if (batchStopped) { await RunAsync(resume: true); return; }
        if (services.Workflow.Current?.Snapshot.Status != WorkflowStatus.Paused) return;
        stopRequested = false;
        running = true;
        operation = "resume";
        NotifyWorkflowChanged();
        try { Status = (await services.ResumeAsync()).Message; }
        catch (Exception error) { Status = error.Message; }
        finally { running = false; operation = null; NotifyWorkflowChanged(); }
    }

    public IReadOnlyList<InvoiceDocument> InvoiceQueue
    {
        get => invoiceQueue;
        set { invoiceQueue = value; RefreshHolds(); }
    }
    private IReadOnlyList<InvoiceDocument> invoiceQueue = [];
    private Dictionary<string, IReadOnlyList<ImportIssue>> holds = new(StringComparer.Ordinal);

    /// <summary>
    /// Recomputes which invoices are held because an item name matches several catalog codes (see <see cref="CatalogHold"/>);
    /// held invoices lose their tick. Call after the queue or the item catalog changes.
    /// </summary>
    public void RefreshHolds()
    {
        var catalog = services.ItemCatalog;
        holds = invoiceQueue.Select(invoice => (invoice.Fingerprint, Issues: CatalogHold.Issues(invoice, catalog)))
            .Where(entry => entry.Issues.Count > 0)
            .GroupBy(entry => entry.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Issues, StringComparer.Ordinal);
        if (checkedFingerprints.RemoveWhere(holds.ContainsKey) > 0)
            CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Why the invoice is held back from entry; empty when it is not held or already entered.</summary>
    public IReadOnlyList<ImportIssue> HoldIssues(InvoiceDocument invoice)
        => holds.TryGetValue(invoice.Fingerprint, out var issues) && !IsDone(invoice) ? issues : [];

    public bool IsHeld(InvoiceDocument invoice) => HoldIssues(invoice).Count > 0;

    /// <summary>Why the latest run left the invoice unfinished (no MISA customer code yet); empty otherwise.</summary>
    public IReadOnlyList<ImportIssue> UnfinishedIssues(InvoiceDocument invoice)
    {
        var latest = History.FirstOrDefault(run => run.Invoice.Fingerprint == invoice.Fingerprint);
        if (latest is null || IsDone(invoice) || !RunOutcome.IsUnfinished(latest.Snapshot)) return Array.Empty<ImportIssue>();
        return new[]
        {
            new ImportIssue(BuyerCodes.MissingCode, IssueSeverity.Blocking, latest.Snapshot.LastMessage ?? RunOutcome.UnfinishedLabel,
                InvoiceId: invoice.InvoiceId,
                Resolution: "Nạp danh sách khách hàng xuất từ MISA ở menu ••• (người mua không có trong danh sách sẽ được tool tự tạo mới trong MISA), "
                    + "hoặc ghi mã vào cột Mã khách hàng của Excel; sau đó chạy lại.")
        };
    }

    /// <summary>Raised when the set of invoices ticked for the next run changes.</summary>
    public event EventHandler? CheckedChanged;
    public int CheckedCount => InvoiceQueue.Count(invoice => checkedFingerprints.Contains(invoice.Fingerprint));
    public bool IsChecked(string fingerprint) => checkedFingerprints.Contains(fingerprint);

    public void SetChecked(string fingerprint, bool value)
    {
        if (value && holds.ContainsKey(fingerprint)) { CheckedChanged?.Invoke(this, EventArgs.Empty); return; }
        if (value ? checkedFingerprints.Add(fingerprint) : checkedFingerprints.Remove(fingerprint))
            CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCheckedMany(IEnumerable<string> fingerprints)
    {
        checkedFingerprints.Clear();
        checkedFingerprints.UnionWith(fingerprints.Where(fingerprint => !holds.ContainsKey(fingerprint)));
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops ticks for invoices that are no longer in the list (after a re-import).</summary>
    public void KeepCheckedIn(IEnumerable<string> available)
    {
        if (checkedFingerprints.RemoveWhere(fingerprint => !available.Contains(fingerprint)) > 0)
            CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Invoices that can still be entered: not blocked or held and neither saved, already in MISA nor posted.</summary>
    public IEnumerable<InvoiceDocument> PendingInvoices => InvoiceQueue.Where(invoice => !invoice.IsBlocked && !IsDone(invoice) && !IsHeld(invoice));

    public bool IsDone(InvoiceDocument invoice) => services.IsPosted(invoice)
        || History.Any(run => run.Invoice.Fingerprint == invoice.Fingerprint
            && (run.Snapshot.Status == WorkflowStatus.Saved || RunOutcome.IsExisting(run.Snapshot))
            || WorkflowRunner.IsManualSavedFor(run, invoice));

    public bool CanMarkSaved(InvoiceDocument invoice) => !running && !IsDone(invoice);

    /// <summary>Done only because of the tool's own records (not the posted list loaded from MISA), so a reset makes it ready.</summary>
    public bool CanResetToReady(InvoiceDocument invoice) => !running && !services.IsPosted(invoice) && IsDone(invoice);

    /// <summary>
    /// The user deleted these vouchers in MISA by hand: deletes their run records so they can be entered again.
    /// Returns how many invoices were reset and an error (null when everything went through).
    /// </summary>
    public async Task<(int Reset, string? Error)> ResetToReadyAsync(IEnumerable<InvoiceDocument> targets)
    {
        if (running) return (0, "Đang chạy Thêm vào MISA. Bấm Dừng ngay, chờ dừng hẳn rồi mới chuyển hóa đơn về sẵn sàng nhập.");
        var reset = 0;
        string? error = null;
        var hadSession = services.Workflow.Current is not null;
        try
        {
            foreach (var invoice in targets.DistinctBy(invoice => invoice.Fingerprint).Where(CanResetToReady))
            {
                await services.Workflow.ResetToReadyAsync(invoice);
                reset++;
            }
        }
        catch (Exception exception) { error = exception.Message; }
        History = await services.Workflow.ReadHistoryAsync();
        if (hadSession && services.Workflow.Current is null) { lastError = null; SessionReset?.Invoke(this, EventArgs.Empty); }
        NotifyWorkflowChanged();
        return (reset, error);
    }

    /// <summary>
    /// The user entered these invoices in MISA by hand: records them as saved and deletes their old checkpoints.
    /// Returns how many were marked and an error (null when everything went through).
    /// </summary>
    public async Task<(int Marked, string? Error)> MarkSavedManuallyAsync(IEnumerable<InvoiceDocument> targets)
    {
        if (running) return (0, "Đang chạy Thêm vào MISA. Bấm Dừng ngay, chờ dừng hẳn rồi mới đánh dấu hóa đơn đã lưu.");
        var marked = 0;
        string? error = null;
        var hadSession = services.Workflow.Current is not null;
        try
        {
            foreach (var invoice in targets.DistinctBy(invoice => invoice.Fingerprint).Where(invoice => !IsDone(invoice)))
            {
                await services.Workflow.MarkSavedManuallyAsync(invoice);
                SetChecked(invoice.Fingerprint, false);
                marked++;
            }
        }
        catch (Exception exception) { error = exception.Message; }
        History = await services.Workflow.ReadHistoryAsync();
        if (hadSession && services.Workflow.Current is null) { lastError = null; SessionReset?.Invoke(this, EventArgs.Empty); }
        NotifyWorkflowChanged();
        return (marked, error);
    }

    public IReadOnlyList<WorkflowContext> History { get; private set; } = [];
    public WorkflowActivity? Activity => services.Activity;
    public ObservableCollection<PerformanceTraceEntry> PerformanceEntries { get; } = [];
    public string PerformanceCurrent
    {
        get => performanceCurrent;
        private set { if (performanceCurrent == value) return; performanceCurrent = value; PropertyChanged?.Invoke(this, new(nameof(PerformanceCurrent))); }
    }
    public string PerformanceElapsed
    {
        get => performanceElapsed;
        private set { if (performanceElapsed == value) return; performanceElapsed = value; PropertyChanged?.Invoke(this, new(nameof(PerformanceElapsed))); }
    }
    public string PerformanceInvoice => performanceInvoice ?? "Chưa có hóa đơn đang chạy";
    public bool HasPerformanceData => PerformanceEntries.Count > 0 || performanceStartedAt is not null;
    public bool CanEditSource => !running;
    public bool CanStart => !running;
    public bool CanResume => !running && (batchStopped || services.Workflow.Current?.Snapshot.Status == WorkflowStatus.Paused);
    public MisaConnection Connection => connection;

    public void RefreshPerformance()
    {
        // Native activity only counts while a run owns MISA; a stale one must not open new traces after a stop.
        var activity = running ? services.Activity : null;
        if (activity is null)
        {
            if (activeNativeTraceId is not null)
            {
                FinishTrace(activeNativeTraceId, "Hoàn tất");
                activeNativeTraceId = null;
                activeNativeTrace = null;
            }
        }
        else if (activeNativeTraceId != activity.RunId + ":" + activity.Field + ":" + activity.StartedAt.Ticks
            || activeNativeTrace is null
            || activeNativeTrace.StartedAt != activity.StartedAt)
        {
            if (activeNativeTraceId is not null) FinishTrace(activeNativeTraceId, "Chuyển bước");
            activeNativeTraceId = activity.RunId + ":" + activity.Field + ":" + activity.StartedAt.Ticks;
            activeNativeTrace = new(activity.Field, "Native/UI Automation", activity.StartedAt, null, "Đang chạy");
            performanceTraceIndexes[activeNativeTraceId] = PerformanceEntries.Count;
            PerformanceEntries.Add(activeNativeTrace);
        }

        if (performanceStartedAt is not null)
        {
            PerformanceElapsed = FormatDuration(PerformanceTotal());
            PerformanceCurrent = activity is null
                ? (running ? "Đang xử lý..." : PerformanceCurrent)
                : $"{activity.Field} · {FormatDuration(DateTimeOffset.UtcNow - activity.StartedAt)}";
            PropertyChanged?.Invoke(this, new(nameof(PerformanceInvoice)));
        }
        PropertyChanged?.Invoke(this, new(nameof(HasPerformanceData)));
    }

    public string ExportPerformance()
    {
        RefreshPerformance();
        var lines = new StringBuilder()
            .AppendLine("MISA Manager - PHÂN TÍCH HIỆU NĂNG")
            .AppendLine($"Thời điểm xuất: {DateTimeOffset.Now:O}")
            .AppendLine($"Hóa đơn: {PerformanceInvoice}")
            .AppendLine($"Trạng thái: {Status}")
            .AppendLine($"Đang chạy: {PerformanceCurrent}")
            .AppendLine($"Tổng thời gian phiên: {PerformanceElapsed}")
            .AppendLine()
            .AppendLine("CÁC BƯỚC")
            .AppendLine("Tên | Trạng thái | Thời lượng | Bắt đầu | Chi tiết")
            .AppendLine(new string('-', 100));
        foreach (var entry in PerformanceEntries)
            lines.AppendLine($"{entry.Name} | {entry.Status} | {entry.DurationDisplay} | {entry.StartedDisplay} | {entry.Details}{(string.IsNullOrWhiteSpace(entry.Error) ? "" : " | " + entry.Error)}");
        return lines.ToString();
    }

    public string ExportLastError(string message, string details, DateTimeOffset occurredAt)
    {
        RefreshPerformance();
        return ErrorPrompt.Build(message, details, occurredAt, PerformanceInvoice, PerformanceEntries);
    }

    public async Task LoadHistoryAsync()
    {
        History = await services.Workflow.ReadHistoryAsync();
        NotifyWorkflowChanged();
    }

    public async Task<int> DeleteAllProfileHistoryAsync()
    {
        var deleted = await services.Workflow.DeleteAllProfileHistoryAsync();
        await LoadHistoryAsync();
        return deleted;
    }

    public void ClearPerformance() => ResetPerformance();

    private async Task RunAsync(bool resume = false)
    {
        if (running) return;
        // Only ticked invoices run, in list order; nothing ticked means nothing runs.
        var ticked = InvoiceQueue.Where(invoice => checkedFingerprints.Contains(invoice.Fingerprint)).ToArray();
        if (ticked.Length == 0)
        { Status = "Chưa tick hóa đơn nào. Tick ô vuông trước các hóa đơn cần nhập rồi bấm Thêm vào MISA."; return; }
        resume &= batchStopped;
        batchStopped = false;
        if (!resume) { handledInBatch.Clear(); batchIndex = 0; }
        InvoiceDocument[] queue = [.. ticked.Where(invoice => !handledInBatch.Contains(invoice.Fingerprint))];
        if (queue.Length == 0) { Status = "Không còn hóa đơn nào cần chạy."; NotifyWorkflowChanged(); return; }
        var batchTotal = batchIndex + queue.Length;
        var first = queue[0];
        running = true;
        stopRequested = false;
        services.Workflow.StopSignal.Reset();
        lastError = null;
        OpenVoucherCheck = null;
        operation = "connect";
        var saved = 0;
        var existing = 0;
        var mismatched = 0;
        var skipped = 0;
        var held = 0;
        var unfinishedNumbers = new List<string>();
        var blockedNumbers = new List<string>();
        var failedNumbers = new List<string>();
        var consecutiveFailures = 0;
        string? stopNote = null;
        string? identity = null;
        services.BeginUiBatch(first.Identity.Number);
        if (resume && performanceStartedAt is { } before && performanceEndedAt is { } after) performanceCarried += after - before;
        else ResetPerformance();
        performanceStartedAt = DateTimeOffset.UtcNow;
        performanceEndedAt = null;
        performanceInvoice = first.Identity.Number;
        PerformanceCurrent = resume ? "Tiếp tục phiên" : "Chuẩn bị phiên";
        var wholeRun = BeginTrace(resume ? "Tiếp tục function Thêm vào MISA" : "Toàn bộ function Thêm vào MISA", first.Identity.Number);
        var performanceOutcome = "Hoàn tất";
        NotifyWorkflowChanged();
        try
        {
            var initialize = BeginTrace("Khởi tạo phiên", "Đọc state và lịch sử workflow");
            await services.Initialization;
            if (!resume)
            {
                // A voucher left open by Dừng ngay is Bỏ once the MISA session is known (below), not abandoned here.
                if (services.Workflow.Current is { } oldRun && WorkflowRunner.ReleasedByNewSession(oldRun))
                {
                    var abandoned = await services.AbandonStoppedSessionAsync();
                    if (abandoned.Status != OperationStatuses.Completed)
                        throw new InvalidOperationException(abandoned.Message);
                }
                SessionReset?.Invoke(this, EventArgs.Empty);
            }
            await LoadHistoryAsync();
            FinishTrace(initialize, "Hoàn tất");
            if (services.ItemCatalog.Count == 0 && queue.Any(invoice => invoice.Lines.Any(line => line.ItemCode.Trim().Length == 0)))
            {
                Status = "Chưa nạp database mã hàng. Bấm 'Mã hàng' và chọn file danh mục vật tư, hàng hóa trước khi nhập.";
                return;
            }
            // One MISA session per batch: resolving it per invoice costs a UIA window scan each time.
            var findWindow = BeginTrace("Tìm cửa sổ MISA", "Xác định đúng PID/HWND phiên MISA");
            identity = await WaitForSessionAsync();
            FinishTrace(findWindow, identity is null ? "Không hoàn tất" : "Hoàn tất", identity is null ? Status : null);
            if (identity is null) return;
            if (!await SettleStoppedVoucherAsync(identity)) return;
            var precheck = BeginTrace("Kiểm tra giao diện MISA", "Đối chiếu ID control của profile với MISA đang mở");
            var screens = await CheckScreensAsync();
            // Only a concrete selector mismatch blocks; an unreadable tree is left to the workflow's own guards.
            if (screens.Report is { Ok: false })
            {
                FinishTrace(precheck, "Không khớp", screens.Summary);
                Status = screens.Summary;
                RaiseError("Giao diện MISA không khớp profile. Có thể MISA đã cập nhật phiên bản hoặc cửa sổ quá nhỏ.", screens.Details);
                return;
            }
            FinishTrace(precheck, "Hoàn tất", screens.Report is null ? screens.Summary : null);
            operation = "enter";
            SetConnection(MisaConnection.Connected, "");
            foreach (var invoice in queue)
            {
                if (stopRequested) break;
                var fingerprint = invoice.Fingerprint;
                var position = $"Hóa đơn {++batchIndex}/{batchTotal}: {invoice.Identity.Number}";
                if (IsDone(invoice))
                {
                    handledInBatch.Add(fingerprint);
                    SetChecked(fingerprint, false);
                    skipped++;
                    continue;
                }
                if (invoice.IsBlocked)
                {
                    handledInBatch.Add(fingerprint);
                    blockedNumbers.Add(invoice.Identity.Number);
                    Status = $"{position} · còn lỗi nguồn, bỏ qua và chuyển sang hóa đơn kế tiếp.";
                    continue;
                }
                // Held by a duplicate item name: skipped quietly, the reason is shown on the invoice list.
                if (IsHeld(invoice))
                {
                    handledInBatch.Add(fingerprint);
                    SetChecked(fingerprint, false);
                    held++;
                    continue;
                }
                SelectedInvoice = invoice;
                PropertyChanged?.Invoke(this, new(nameof(SelectedInvoice)));
                performanceInvoice = invoice.Identity.Number;
                PropertyChanged?.Invoke(this, new(nameof(PerformanceInvoice)));
                Status = $"{position} · đang nhập và đối chiếu. Bấm Dừng ngay để dừng sau thao tác hiện tại.";
                PerformanceCurrent = "Nhập hóa đơn · đang chạy";
                OperationResult response;
                var windowReruns = 0;
                for (var rerun = 0; ; rerun++)
                {
                    var liveRun = BeginTrace("Nhập hóa đơn", $"Nhập và đối chiếu hóa đơn {invoice.Identity.Number}"
                        + (rerun == 0 ? "" : $" · chạy lại lần {rerun}"));
                    response = await services.RunInvoiceAsync(invoice, identity ?? "");
                    FinishTrace(liveRun, response.Status == "completed" ? "Hoàn tất" : response.Status, response.Status == "completed" ? null : response.Message);
                    // MISA was restarted, re-logged or switched data with nothing of this invoice left open:
                    // find the session again (waiting for login if needed) and enter the invoice from scratch.
                    if (response.Status != "completed" && !stopRequested && windowReruns == 0
                        && services.Workflow.LastLiveRun is { MisaClean: true } lost && lost.Context.Invoice.Fingerprint == fingerprint
                        && IsSessionLoss(lost.Context.Snapshot.LastErrorCode ?? lost.Verification.ErrorCode))
                    {
                        windowReruns++;
                        Status = $"{position} · phiên MISA đã thay đổi; đang tìm lại phiên MISA để nhập lại từ đầu…";
                        NotifyWorkflowChanged();
                        identity = await WaitForSessionAsync();
                        if (identity is null) break;
                        continue;
                    }
                    // Interference (stray click, tab switch/drag) with the half-entered voucher already discarded:
                    // enter the same invoice again from scratch instead of stopping with an error.
                    if (response.Status == "completed" || stopRequested || rerun >= InterferenceRetry.InvoiceReruns
                        || services.Workflow.LastLiveRun is not { AutoRerunSafe: true } last
                        || last.Context.Invoice.Fingerprint != fingerprint)
                        break;
                    Status = $"{position} · MISA bị gián đoạn ({last.Error ?? response.Message}). Đã Bỏ chứng từ dở, "
                        + $"tự nhập lại từ đầu (lần {rerun + 1}/{InterferenceRetry.InvoiceReruns})… Tránh bấm vào MISA trong lúc tool chạy.";
                    NotifyWorkflowChanged();
                    await Task.Delay(TimeSpan.FromSeconds(1.5));
                    if (stopRequested) break;
                }
                RestoreMisaWindow(identity);
                if (stopRequested && services.Workflow.LastLiveRun is { Outcome: "paused" } paused
                    && paused.Context.Invoice.Fingerprint == fingerprint)
                {
                    Status = response.Message;
                    History = [paused.Context, .. History.Where(run => run.Snapshot.RunId != paused.Context.Snapshot.RunId)];
                    NotifyWorkflowChanged();
                    break;
                }
                // Stopped while an interference rerun was due: the voucher was already discarded safely, so this is
                // not an error; Tiếp tục enters the invoice again from scratch.
                if (response.Status != "completed" && stopRequested
                    && services.Workflow.LastLiveRun is { AutoRerunSafe: true } interrupted && interrupted.Context.Invoice.Fingerprint == fingerprint)
                {
                    batchIndex--;
                    break;
                }
                Status = response.Message;
                NotifyWorkflowChanged();
                // No customer code yet: nothing was typed, so the invoice is recorded as unfinished, not raised as an error.
                var unfinished = response.Status != "completed" && !stopRequested
                    && services.Workflow.LastLiveRun is { MisaClean: true } skippedRun
                    && skippedRun.Context.Invoice.Fingerprint == fingerprint && RunOutcome.IsUnfinished(skippedRun.Context.Snapshot);
                if (response.Status != "completed" && !unfinished)
                {
                    performanceOutcome = response.Status;
                    if (!stopRequested) RaiseError(response.Message, Serialize(response.Data));
                }
                // Update history in memory instead of re-reading every run file from disk.
                if (services.Workflow.Current is { } finished)
                {
                    History = [finished, .. History.Where(run => run.Snapshot.RunId != finished.Snapshot.RunId)];
                    NotifyWorkflowChanged();
                }
                if (unfinished)
                {
                    handledInBatch.Add(fingerprint);
                    unfinishedNumbers.Add(invoice.Identity.Number);
                    Status = $"{position} · chưa hoàn thành (chưa có mã khách hàng), đã lưu hồ sơ; chuyển sang hóa đơn kế tiếp.";
                    NotifyWorkflowChanged();
                    continue;
                }
                // A completed run (saved, verified discard, or number already in MISA) advances the queue.
                // A failed invoice that left MISA clean is set aside so one bad invoice does not block the
                // rest; anything that may have left data in MISA, or an environment problem, stops here.
                if (response.Status != "completed")
                {
                    var failed = services.Workflow.LastLiveRun;
                    var code = failed?.Context.Snapshot.LastErrorCode ?? failed?.Verification.ErrorCode;
                    if (stopRequested || failed is not { MisaClean: true } || failed.Context.Invoice.Fingerprint != fingerprint
                        || IsEnvironmentFailure(code) || ++consecutiveFailures >= 3)
                    {
                        if (consecutiveFailures >= 3)
                            stopNote = Status = $"{consecutiveFailures} hóa đơn liên tiếp bị lỗi; dừng để kiểm tra MISA/dữ liệu. Lỗi gần nhất: {response.Message}";
                        break;
                    }
                    handledInBatch.Add(fingerprint);
                    failedNumbers.Add(invoice.Identity.Number);
                    performanceOutcome = "Hoàn tất";
                    Status = $"{position} · lỗi ({failed.Error ?? response.Message}). Chứng từ đã được Bỏ an toàn; giữ tick để xử lý sau, chuyển sang hóa đơn kế tiếp.";
                    NotifyWorkflowChanged();
                    continue;
                }
                consecutiveFailures = 0;
                var current = services.Workflow.Current;
                var final = current?.Invoice.Fingerprint == fingerprint ? current.Snapshot : null;
                if (final?.Status is WorkflowStatus.Saved or WorkflowStatus.Discarded)
                {
                    handledInBatch.Add(fingerprint);
                    if (final.Status == WorkflowStatus.Saved) { saved++; SetChecked(fingerprint, false); }
                    else if (RunOutcome.IsExisting(final))
                    {
                        existing++;
                        SetChecked(fingerprint, false);
                        Status = $"{position} · số hóa đơn đã tồn tại trong MISA: đã chọn Không, đóng tab và chuyển sang hóa đơn tiếp theo.";
                    }
                    else mismatched++;
                }
                if (stopRequested || final?.Status is not (WorkflowStatus.Saved or WorkflowStatus.Discarded)) break;
            }
            if (performanceOutcome == "Hoàn tất")
            {
                if (!stopRequested && stopNote is null)
                {
                    var parts = new List<string>();
                    if (saved > 0) parts.Add($"{saved} đã lưu");
                    if (mismatched > 0) parts.Add($"{mismatched} đã bỏ vì lệch");
                    if (existing > 0) parts.Add($"{existing} đã tồn tại trong MISA");
                    if (skipped > 0) parts.Add($"{skipped} bỏ qua (đã nhập trước đó)");
                    if (held > 0) parts.Add($"{held} tạm hoãn (mặt hàng trùng tên nhiều mã)");
                    if (unfinishedNumbers.Count > 0)
                        parts.Add($"{unfinishedNumbers.Count} chưa hoàn thành, chưa có mã khách hàng ({string.Join(", ", unfinishedNumbers.Take(5))})");
                    if (failedNumbers.Count > 0) parts.Add($"{failedNumbers.Count} lỗi, đã bỏ chứng từ, cần xem lại ({string.Join(", ", failedNumbers.Take(5))})");
                    if (blockedNumbers.Count > 0) parts.Add($"{blockedNumbers.Count} thiếu dữ liệu ({string.Join(", ", blockedNumbers.Take(5))})");
                    Status = parts.Count == 0 ? "Không còn hóa đơn nào cần chạy trong danh sách đã tick."
                        : $"Xong các hóa đơn đã tick: {string.Join(" · ", parts)}.";
                }
            }
        }
        catch (Exception error)
        {
            Status = error.Message;
            performanceOutcome = "Lỗi";
            if (!stopRequested) RaiseError(error.Message, error.ToString());
            FinishActiveTrace("Lỗi", error.Message);
        }
        finally
        {
            // A run paused with its voucher open stays active so the next start can Bỏ that voucher safely.
            var keptStopped = services.Workflow.Current is { } open && WorkflowRunner.StoppedWithOpenVoucher(open) ? open : null;
            if (stopRequested && keptStopped is null)
            {
                var stopped = services.Workflow.Current;
                var abandoned = await services.AbandonStoppedSessionAsync();
                if (abandoned.Status == OperationStatuses.Completed)
                {
                    lastError = null;
                    if (stopped is not null)
                        History = [stopped with { SessionDetached = true }, .. History.Where(run => run.Snapshot.RunId != stopped.Snapshot.RunId)];
                    SessionReset?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Status = abandoned.Message;
                    RaiseError(abandoned.Message, Serialize(abandoned.Data));
                    performanceOutcome = "Lỗi";
                }
            }
            if (stopRequested && performanceOutcome == "Hoàn tất") performanceOutcome = "Đã dừng";
            performanceEndedAt = DateTimeOffset.UtcNow;
            FinishActiveTrace(performanceOutcome);
            FinishAllRunning(performanceOutcome);
            FinishTrace(wholeRun, performanceOutcome);
            var left = InvoiceQueue.Count(invoice => checkedFingerprints.Contains(invoice.Fingerprint) && !handledInBatch.Contains(invoice.Fingerprint));
            batchStopped = stopRequested && left > 0 && performanceOutcome != "Lỗi";
            if (batchStopped && keptStopped is not null)
                Status = $"Đã tạm dừng hóa đơn {keptStopped.Invoice.Identity.Number} (chưa Cất, chứng từ vẫn mở trong MISA). Còn {left} hóa đơn đã tick; "
                    + "bấm Tiếp tục hoặc Thêm vào MISA: tool sẽ Bỏ chứng từ dở đó rồi nhập lại hóa đơn từ đầu.";
            else if (batchStopped) Status = $"Đã dừng và bỏ checkpoint cũ. Còn {left} hóa đơn đã tick; bấm Thêm vào MISA để chạy phiên mới hoặc Tiếp tục để chạy phần còn lại.";
            else if (stopRequested && performanceOutcome == "Đã dừng") Status = "Đã dừng; không còn hóa đơn đã tick nào cần chạy.";
            stopRequested = false;
            running = false;
            operation = null;
            services.EndUiBatch(performanceOutcome);
            PerformanceCurrent = performanceOutcome == "Đã dừng" ? "Đã dừng" : "Đã kết thúc";
            PerformanceElapsed = FormatDuration(PerformanceTotal());
            NotifyWorkflowChanged();
            RestoreMisaWindow(identity);
        }
    }

    /// <summary>
    /// Bỏ the voucher a Dừng ngay left half-entered in this MISA session (its identity is verified first), then
    /// releases that run. A voucher from an earlier MISA session no longer exists, so that run is only released.
    /// </summary>
    private async Task<bool> SettleStoppedVoucherAsync(string identity)
    {
        if (services.Workflow.Current is not { } stopped || !WorkflowRunner.StoppedWithOpenVoucher(stopped)) return true;
        var trace = BeginTrace("Bỏ chứng từ dở", $"Hóa đơn {stopped.Invoice.Identity.Number} còn mở từ lần Dừng ngay trước");
        if (stopped.WindowIdentity == identity)
        {
            var discarded = await services.DiscardStoppedVoucherAsync();
            if (discarded.Status != OperationStatuses.Completed)
            {
                // Still release the checkpoint so the next Thêm vào MISA starts fresh; history keeps its uncertainty.
                var detached = services.Workflow.Current ?? stopped;
                if ((await services.AbandonStoppedSessionAsync()).Status == OperationStatuses.Completed)
                    History = [detached with { SessionDetached = true }, .. History.Where(run => run.Snapshot.RunId != detached.Snapshot.RunId)];
                FinishTrace(trace, "Không hoàn tất", discarded.Message);
                var message = discarded.Message + " Đã bỏ checkpoint cũ; sau khi Bỏ chứng từ đó trong MISA, bấm Thêm vào MISA để chạy phiên mới.";
                Status = message;
                RaiseError(message, Serialize(discarded.Data));
                return false;
            }
        }
        var released = services.Workflow.Current ?? stopped;
        var abandoned = await services.AbandonStoppedSessionAsync();
        if (abandoned.Status != OperationStatuses.Completed)
        {
            FinishTrace(trace, "Không hoàn tất", abandoned.Message);
            Status = abandoned.Message;
            RaiseError(abandoned.Message, Serialize(abandoned.Data));
            return false;
        }
        History = [released with { SessionDetached = true }, .. History.Where(run => run.Snapshot.RunId != released.Snapshot.RunId)];
        FinishTrace(trace, "Hoàn tất");
        return true;
    }

    private TimeSpan PerformanceTotal()
        => performanceStartedAt is { } started ? performanceCarried + ((performanceEndedAt ?? DateTimeOffset.UtcNow) - started) : TimeSpan.Zero;

    // MISA can end up minimized after Cất closes its voucher; show it again (now and once it settles) so it stays visible
    // behind the tool, which is topmost and therefore never covered.
    private static void RestoreMisaWindow(string? identity)
    {
        if (ResolveWindow(identity) is not { } window) return;
        MisaWindowDiscovery.RestoreIfMinimized(window.WindowHandle);
        _ = Task.Run(async () =>
        {
            foreach (var delay in new[] { 400, 1200 })
            {
                await Task.Delay(delay);
                MisaWindowDiscovery.RestoreIfMinimized(window.WindowHandle);
            }
        });
    }

    private void ResetPerformance()
    {
        PerformanceEntries.Clear();
        performanceTraceIndexes.Clear();
        activeNativeTraceId = null;
        activeNativeTrace = null;
        performanceStartedAt = null;
        performanceEndedAt = null;
        performanceCarried = TimeSpan.Zero;
        performanceInvoice = null;
        PerformanceElapsed = "0 ms";
        PerformanceCurrent = "Chưa chạy";
        PropertyChanged?.Invoke(this, new(nameof(PerformanceInvoice)));
        PropertyChanged?.Invoke(this, new(nameof(HasPerformanceData)));
    }

    private string BeginTrace(string name, string details)
    {
        var id = Guid.NewGuid().ToString("N");
        performanceTraceIndexes[id] = PerformanceEntries.Count;
        PerformanceEntries.Add(new(name, details, DateTimeOffset.UtcNow, null, "Đang chạy"));
        return id;
    }

    private void FinishTrace(string id, string status, string? error = null)
    {
        if (!performanceTraceIndexes.TryGetValue(id, out var index) || index >= PerformanceEntries.Count) return;
        var entry = PerformanceEntries[index];
        if (entry.CompletedAt is not null) return;
        PerformanceEntries[index] = entry with { CompletedAt = DateTimeOffset.UtcNow, Status = status, Error = error };
        performanceTraceIndexes.Remove(id);
    }

    private void FinishActiveTrace(string status, string? error = null)
    {
        if (activeNativeTraceId is not null) FinishTrace(activeNativeTraceId, status, error);
        activeNativeTraceId = null;
        activeNativeTrace = null;
        if (performanceStartedAt is not null) PerformanceElapsed = FormatDuration(PerformanceTotal());
    }

    private void FinishAllRunning(string status, string? error = null)
    {
        foreach (var id in performanceTraceIndexes.Keys.ToArray()) FinishTrace(id, status, error);
    }

    private void RaiseError(string message, string details)
    {
        services.RecordUiError(message, details);
        var code = ErrorHints.FindCode(services.Workflow.Current?.Snapshot.ErrorChain, message, details, services.Workflow.Current?.Snapshot.LastErrorCode);
        lastError = (code, message);
        ErrorRaised?.Invoke(this, new(message, details, DateTimeOffset.Now));
    }

    private static string Serialize(object? value)
        => value is null ? "Không có thêm dữ liệu." : JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalSeconds < 1 ? $"{duration.TotalMilliseconds:N0} ms" : duration.ToString(@"mm\:ss\.fff");

    // Codes that mean the MISA session itself changed (restart, re-login, other data), not the invoice.
    private static bool IsSessionLoss(string? code)
        => code is "WINDOW_IDENTITY_CHANGED" or "WINDOW_NOT_FOUND" or "WINDOW_IDENTITY_INVALID" or "NOT_MISA" or "NOT_CONNECTED";

    // Problems every following invoice would hit too; continuing would only repeat the same failure.
    private static bool IsEnvironmentFailure(string? code)
        => IsSessionLoss(code) || code is "SALES_SURFACE_MISSING" or "EXISTING_EDITOR" or "DIALOG_REQUIRES_ATTENTION"
            or "NATIVE_OWNER_UNAVAILABLE" or "PROFILE_REVISION_CHANGED" or "ITEM_CATALOG_MISSING" or "ITEM_CREATE_UNCERTAIN";

    // Waits (up to 2 minutes, cancellable with Dừng ngay) for exactly one logged-in MISA R20 session,
    // so starting while MISA is still on its login screen or restarting does not fail the run.
    private async Task<string?> WaitForSessionAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (true)
        {
            var windows = await services.Native.FindCandidatesAsync();
            var sessions = Sessions(windows);
            if (sessions.Count == 1 || stopRequested || DateTimeOffset.UtcNow >= deadline
                || sessions.Count > 1 || windows.Any(window => MisaWindowDiscovery.IsSessionTitle(window.Title)))
                return await ResolveWindowIdentityAsync();
            var left = (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds);
            Status = NoSessionDetail(windows) + $" Đang chờ phiên MISA sẵn sàng ({left}s), bấm Dừng ngay để hủy.";
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private async Task<string?> ResolveWindowIdentityAsync()
    {
        var windows = await services.Native.FindCandidatesAsync();
        var sessions = Sessions(windows);
        SetConnection(sessions.Count > 0 ? MisaConnection.Connected : MisaConnection.Lost, NoSessionDetail(windows));
        if (sessions.Count != 1)
        {
            Status = sessions.Count == 0 ? NoSessionDetail(windows)
                : "Đang mở nhiều phiên MISA đã đăng nhập; hãy đóng bớt phiên thừa rồi thử lại.";
            return null;
        }
        if (stopRequested) return null;
        var window = sessions[0];
        return $"pid={window.ProcessId};start={window.ProcessStartUtc:O};hwnd={window.WindowHandle};exe={window.ExecutablePath}";
    }

    /// <summary>
    /// Compares the selected invoice with MISA: through the run's checkpoint while that run still has its voucher open,
    /// otherwise by reading whichever sales voucher is open in MISA right now.
    /// </summary>
    public async Task<OperationResult> ReconcileAsync()
    {
        if (SelectedInvoice is not { } invoice || services.Workflow.Current is not { } current
            || current.Invoice.Fingerprint != invoice.Fingerprint || WorkflowRunner.IsTerminal(current.Snapshot.Status))
            return await CompareOpenVoucherAsync();
        OpenVoucherCheck = null;
        var owned = !running;
        if (owned) { running = true; operation = "reconcile"; NotifyWorkflowChanged(); }
        try
        {
            var result = await services.ReconcileAsync(invoice.Fingerprint);
            Status = result.Message;
            NotifyWorkflowChanged();
            return result;
        }
        finally { if (owned) { running = false; operation = null; NotifyWorkflowChanged(); } }
    }

    /// <summary>
    /// Reads the voucher open in MISA and pairs it with the loaded Excel invoice carrying the same invoice number
    /// (not the one selected in the list); without such an invoice only the MISA values are shown.
    /// </summary>
    private async Task<OperationResult> CompareOpenVoucherAsync()
    {
        if (running) return new("ui", "blocked", "Tool đang chạy; chờ xong rồi bấm Đối chiếu hóa đơn.");
        running = true;
        operation = "reconcile";
        NotifyWorkflowChanged();
        Status = "Đang đọc chứng từ đang mở trong MISA để đối chiếu…";
        try
        {
            var windows = await services.Native.FindCandidatesAsync();
            var sessions = Sessions(windows);
            if (sessions.Count != 1)
                throw new AutomationException("NOT_CONNECTED", sessions.Count == 0 ? NoSessionDetail(windows)
                    : "Đang mở nhiều phiên MISA đã đăng nhập; hãy đóng bớt phiên thừa rồi thử lại.");
            var comparison = await services.CompareOpenVoucherAsync(InvoiceQueue, sessions[0]);
            if (comparison.Invoice is { } paired && paired.Fingerprint != SelectedInvoice?.Fingerprint)
            {
                SelectedInvoice = paired;
                PropertyChanged?.Invoke(this, new(nameof(SelectedInvoice)));
            }
            OpenVoucherCheck = new(comparison.Invoice, comparison, null, DateTimeOffset.Now, SelectedInvoice?.Fingerprint);
            var voucher = comparison.VoucherNumber.Length == 0 ? "đang mở" : comparison.VoucherNumber;
            if (comparison.Invoice is not { } invoice)
            {
                Status = $"Đã đọc chứng từ {voucher} (hóa đơn {(comparison.InvoiceNumber.Length == 0 ? "?" : comparison.InvoiceNumber)}) trong MISA; "
                    + "Excel đang nạp không có hóa đơn này nên cột Excel để trống.";
                return new("ui", OperationStatuses.Completed, Status, Data: comparison.Result);
            }
            var mismatched = comparison.Result.Values.Count(value => !value.Matched);
            Status = mismatched == 0 ? $"Chứng từ {voucher} trong MISA khớp hóa đơn {invoice.Identity.Number}."
                : $"Chứng từ {voucher} lệch {mismatched} trường so với hóa đơn {invoice.Identity.Number}; xem cửa sổ đối chiếu.";
            return new("ui", mismatched == 0 ? OperationStatuses.Completed : OperationStatuses.Blocked, Status, Data: comparison.Result);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            OpenVoucherCheck = new(null, null, error.Message, DateTimeOffset.Now, SelectedInvoice?.Fingerprint);
            Status = "Không đối chiếu được: " + error.Message;
            return new("ui", OperationStatuses.Blocked, Status);
        }
        finally
        {
            running = false;
            operation = null;
            PropertyChanged?.Invoke(this, new(nameof(OpenVoucherCheck)));
            NotifyWorkflowChanged();
        }
    }

    /// <summary>Matches the profile selectors against the open MISA screens (read-only).</summary>
    public async Task<ScreenCheckView> CheckScreensAsync(CancellationToken token = default)
    {
        try
        {
            var windows = await services.Native.FindCandidatesAsync(token);
            var sessions = Sessions(windows);
            if (sessions.Count != 1)
                return new(false, sessions.Count == 0 ? NoSessionDetail(windows) : "Đang mở nhiều phiên MISA; hãy đóng bớt rồi kiểm tra lại.", "");
            var report = await services.CheckScreensAsync(sessions[0], token);
            return new(report.Ok, report.Summary, report.Details, report);
        }
        catch (Exception error) when (error is AutomationException or InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            return new(false, "Không kiểm tra được giao diện MISA: " + error.Message, error.ToString());
        }
    }

    private void SetConnection(MisaConnection value, string detail)
    {
        if (connection == value && connectionDetail == detail) return;
        connection = value;
        connectionDetail = detail;
        PropertyChanged?.Invoke(this, new(nameof(Connection)));
        PropertyChanged?.Invoke(this, new(nameof(State)));
    }

    // Only a logged-in session of the profile's MISA version can be bound by the workflow.
    private IReadOnlyList<WindowIdentity> Sessions(IReadOnlyList<WindowIdentity> windows)
        => [.. windows.Where(window => window.Title.StartsWith(services.WorkflowProfile.VersionTitle + " - ", StringComparison.Ordinal))];

    private string NoSessionDetail(IReadOnlyList<WindowIdentity> windows)
        => Sessions(windows).Count > 0 ? ""
            : windows.Count == 0 ? "Không thấy cửa sổ MISA SME 2023 R20."
            : windows.Any(window => MisaWindowDiscovery.IsSessionTitle(window.Title))
                ? $"MISA đang mở không phải bản {services.WorkflowProfile.VersionTitle}."
                : "MISA đang ở màn hình đăng nhập/mở dữ liệu; đăng nhập vào dữ liệu kế toán để tool kết nối.";

    /// <summary>Cheap window-level check; skipped while a run owns the single UIA thread.</summary>
    public async Task ProbeConnectionAsync()
    {
        if (running || probing) return;
        probing = true;
        try
        {
            try { await services.Initialization; }
            catch (Exception error)
            {
                SetConnection(MisaConnection.Lost, "Dịch vụ nền chưa khởi động được (sẽ tự thử lại): " + error.GetBaseException().Message);
                return;
            }
            var windows = await services.Native.FindCandidatesAsync();
            SetConnection(Sessions(windows).Count > 0 ? MisaConnection.Connected : MisaConnection.Lost, NoSessionDetail(windows));
        }
        catch (Exception error)
        {
            SetConnection(MisaConnection.Lost, "Không đọc được cửa sổ MISA: " + error.Message);
        }
        finally { probing = false; }
    }

    /// <summary>The single answer to "what is the tool doing and what must the user do".</summary>
    public ToolState State
    {
        get
        {
            var current = services.Workflow.Current;
            var bound = current is not null && (SelectedInvoice is null || current.Invoice.Fingerprint == SelectedInvoice.Fingerprint)
                ? current : null;
            var number = (bound?.Invoice ?? SelectedInvoice)?.Identity.Number;
            var invoiceText = number is null ? "" : $"Hóa đơn {number}";
            if (running && stopRequested)
                return new(ToolStateKind.PauseRequested, ToolState.LabelOf(ToolStateKind.PauseRequested), "Làm xong thao tác hiện tại rồi dừng");
            if (running)
            {
                var activityField = services.Activity?.Field;
                return (activityField is "reconcile" or "save" or "discard" ? activityField : operation) switch
                {
                    "connect" => new(ToolStateKind.Connecting, ToolState.LabelOf(ToolStateKind.Connecting), "Đang tìm cửa sổ MISA…"),
                    "reconcile" => new(ToolStateKind.Reconciling, ToolState.LabelOf(ToolStateKind.Reconciling), invoiceText),
                    "save" => new(ToolStateKind.Saving, "Đang lưu", invoiceText),
                    "discard" => new(ToolStateKind.Saving, "Đang bỏ", invoiceText),
                    "resume" => new(ToolStateKind.Connecting, "Đang tiếp tục…", "Đang xác minh lại checkpoint."),
                    _ => new(ToolStateKind.Entering, ToolState.LabelOf(ToolStateKind.Entering), invoiceText)
                };
            }
            if (batchStopped)
                return new(ToolStateKind.Paused, ToolState.LabelOf(ToolStateKind.Paused), "Bấm Tiếp tục để chạy các hóa đơn đã tick còn lại.");
            if (connection == MisaConnection.Lost)
                return new(ToolStateKind.Disconnected, ToolState.LabelOf(ToolStateKind.Disconnected), connectionDetail,
                    ErrorHints.For("NOT_CONNECTED")!);
            if (connection == MisaConnection.Unknown)
                return new(ToolStateKind.Connecting, ToolState.LabelOf(ToolStateKind.Connecting), "Đang kiểm tra phiên MISA…");
            if (bound is { } context)
            {
                var snapshot = context.Snapshot;
                var code = ErrorHints.FindCode(snapshot.ErrorChain, snapshot.LastMessage, snapshot.LastErrorCode) ?? lastError?.Code;
                var message = snapshot.LastMessage ?? "";
                string Hint() => ErrorHints.For(code) ?? ErrorHints.ForStatus(snapshot.Status);
                switch (snapshot.Status)
                {
                    case WorkflowStatus.Saved:
                        return new(ToolStateKind.Saved, ToolState.LabelOf(ToolStateKind.Saved), invoiceText);
                    case WorkflowStatus.Discarded when RunOutcome.IsExisting(snapshot):
                        return new(ToolStateKind.Existing, ToolState.LabelOf(ToolStateKind.Existing),
                            invoiceText + " · số hóa đơn đã có trong MISA, đã chọn Không và đóng tab");
                    case WorkflowStatus.Discarded:
                        return new(ToolStateKind.Discarded, ToolState.LabelOf(ToolStateKind.Discarded), invoiceText + " · dữ liệu lệch, chứng từ đã được bỏ an toàn");
                    case WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain:
                        return new(ToolStateKind.Uncertain, ToolState.LabelOf(ToolStateKind.Uncertain), message,
                            ErrorHints.For(snapshot.Status == WorkflowStatus.SaveUncertain ? "SAVE_UNCERTAIN" : "INPUT_UNCERTAIN")!);
                    case WorkflowStatus.Blocked:
                        return new(ToolStateKind.NeedsAction, ToolState.LabelOf(ToolStateKind.NeedsAction), message, Hint());
                    case WorkflowStatus.Failed when RunOutcome.IsUnfinished(snapshot):
                        return new(ToolStateKind.NeedsAction, RunOutcome.UnfinishedLabel, message, Hint());
                    case WorkflowStatus.Failed:
                        return new(ToolStateKind.Error, ToolState.LabelOf(ToolStateKind.Error), message, Hint());
                    case WorkflowStatus.Paused:
                        return new(ToolStateKind.Paused, ToolState.LabelOf(ToolStateKind.Paused), "Workflow đang tạm dừng tại checkpoint; bấm Tiếp tục.");
                }
            }
            if (lastError is { } error)
                return new(ToolStateKind.Error, ToolState.LabelOf(ToolStateKind.Error), error.Message,
                    ErrorHints.For(error.Code) ?? "Xem chi tiết lỗi, kiểm tra chứng từ đang mở trong MISA rồi chạy lại.");
            return new(ToolStateKind.Ready, ToolState.LabelOf(ToolStateKind.Ready), invoiceText);
        }
    }

    /// <summary>
    /// Read the current sales scope without sending input. The UI uses this
    /// only to close the comparison surface when MISA closes the sales tab.
    /// </summary>
    public async Task<(bool IsOpen, WindowIdentity? Window)> ObserveSalesSurfaceAsync(CancellationToken token = default)
    {
        var window = ResolveWindow(services.Workflow.Current?.Snapshot.WindowIdentity);
        if (window is null) return (false, null);
        try
        {
            var profile = services.ProfileFor(services.Workflow.Current?.Invoice);
            var observation = await services.Native.ObserveAsync(window.WindowHandle, null, token, profile.SalesScopeId);
            return (profile.IsSalesSurface(observation.Elements), observation.Frame.Window);
        }
        catch (Exception error) when (error is AutomationException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, null);
        }
    }

    /// <summary>Reads the voucher tab so auxiliary UI can close when the MISA tab is closed.</summary>
    public async Task<(bool IsOpen, WindowIdentity? Window)> ObserveVoucherEditorAsync(CancellationToken token = default)
    {
        var window = ResolveWindow(services.Workflow.Current?.Snapshot.WindowIdentity);
        if (window is null) return (false, null);
        try
        {
            // Existence only: walking the editor's subtree here every probe kept MISA's UI thread busy.
            return await services.Native.ScopeWindowOpenAsync(window.WindowHandle,
                services.ProfileFor(services.Workflow.Current?.Invoice).EditorId, token);
        }
        catch (Exception error) when (error is AutomationException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, null);
        }
    }

    private static WindowIdentity? ResolveWindow(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        try
        {
            var values = identity.Split(';')
                .Select(part => part.Split('=', 2))
                .Where(part => part.Length == 2)
                .ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
            return new WindowIdentity(
                int.Parse(values["pid"], System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(values["start"], System.Globalization.CultureInfo.InvariantCulture).UtcDateTime,
                (nint)long.Parse(values["hwnd"], System.Globalization.CultureInfo.InvariantCulture),
                values["exe"], "", 96, new Bounds(0, 0, 0, 0), "unknown");
        }
        catch (Exception error) when (error is FormatException or KeyNotFoundException or OverflowException)
        {
            return null;
        }
    }

    private sealed class ActionCommand(Func<Task> action) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        // async void: an escaped exception would reach the dispatcher, so surface it as status instead.
        public async void Execute(object? parameter)
        {
            try { await action(); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Command failed: {0}", error); App.WriteCrashLog("command", error); }
        }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
