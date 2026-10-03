using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text;
using System.Windows.Input;
using MisaManager.Automation;
using MisaManager.Core;

namespace MisaManager.App;


public enum MisaConnection { Unknown, Connected, Lost }

public sealed class WorkflowViewModel : INotifyPropertyChanged
{
    private readonly AppServices services;
    private string status = "Sẵn sàng. Chọn hóa đơn để tiếp tục.";
    private bool running;
    private bool stopRequested;
    private readonly HashSet<string> checkedFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> handledInBatch = new(StringComparer.Ordinal);
    private int batchIndex;
    private bool probing;
    private string? operation;
    private MisaConnection connection = MisaConnection.Unknown;
    private string connectionDetail = "";
    private (string? Code, string Message)? lastError;
    private DateTimeOffset? performanceStartedAt;
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

    /// <summary>Hard stop: cancels the step in flight; Resume continues from the paused checkpoint.</summary>
    public async Task StopNowAsync()
    {
        stopRequested = true;
        if (services.Workflow.Current is null) return;
        try { Status = (await services.ExecuteAsync("pause")).Message; }
        catch (Exception error) { Status = error.Message; }
    }

    public async Task ResumeAsync()
    {
        if (running) return;
        if (services.Workflow.Current?.Snapshot.Status != WorkflowStatus.Paused) return;
        running = true;
        operation = "resume";
        NotifyWorkflowChanged();
        try { Status = (await services.ExecuteAsync("resume")).Message; }
        catch (Exception error) { Status = error.Message; }
        finally { running = false; operation = null; NotifyWorkflowChanged(); }
    }

    public IReadOnlyList<InvoiceDocument> InvoiceQueue { get; set; } = [];

    /// <summary>Raised when the set of invoices ticked for the next run changes.</summary>
    public event EventHandler? CheckedChanged;
    public int CheckedCount => InvoiceQueue.Count(invoice => checkedFingerprints.Contains(invoice.Fingerprint));
    public bool IsChecked(string fingerprint) => checkedFingerprints.Contains(fingerprint);

    public void SetChecked(string fingerprint, bool value)
    {
        if (value ? checkedFingerprints.Add(fingerprint) : checkedFingerprints.Remove(fingerprint))
            CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCheckedMany(IEnumerable<string> fingerprints)
    {
        checkedFingerprints.Clear();
        checkedFingerprints.UnionWith(fingerprints);
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops ticks for invoices that are no longer in the list (after a re-import).</summary>
    public void KeepCheckedIn(IEnumerable<string> available)
    {
        if (checkedFingerprints.RemoveWhere(fingerprint => !available.Contains(fingerprint)) > 0)
            CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Invoices that can still be entered: not blocked and neither saved nor already in MISA.</summary>
    public IEnumerable<InvoiceDocument> PendingInvoices => InvoiceQueue.Where(invoice => !invoice.IsBlocked && !IsDone(invoice));

    private bool IsDone(InvoiceDocument invoice) => History.Any(run => run.Invoice.Fingerprint == invoice.Fingerprint
        && (run.Snapshot.Status == WorkflowStatus.Saved || RunOutcome.IsExisting(run.Snapshot)));

    private bool HasDiscardedRun(InvoiceDocument invoice) => History.Any(run => run.Invoice.Fingerprint == invoice.Fingerprint
        && run.Snapshot.Status == WorkflowStatus.Discarded);
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
    public bool CanResume => !running && services.Workflow.Current?.Snapshot.Status == WorkflowStatus.Paused;
    public MisaConnection Connection => connection;

    public void RefreshPerformance()
    {
        var activity = services.Activity;
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

        if (performanceStartedAt is { } started)
        {
            PerformanceElapsed = FormatDuration(DateTimeOffset.UtcNow - started);
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
        lines.AppendLine().AppendLine("HƯỚNG DẪN CHO CHATGPT");
        lines.AppendLine("Hãy xác định 3 bước tốn thời gian nhất, phân biệt chờ MISA/UI Automation với xử lý nội bộ, và đề xuất cách đo hoặc tối ưu tiếp theo. Không giả định thành công nếu có bước lỗi.");
        return lines.ToString();
    }

    public string ExportLastError(string message, string details, DateTimeOffset occurredAt)
        => new StringBuilder()
            .AppendLine("MISA Manager - LỖI KHI THÊM VÀO MISA")
            .AppendLine($"Thời điểm: {occurredAt:O}")
            .AppendLine($"Hóa đơn: {PerformanceInvoice}")
            .AppendLine($"Lỗi: {message}")
            .AppendLine()
            .AppendLine("CHI TIẾT")
            .AppendLine(details)
            .AppendLine()
            .AppendLine("HIỆU NĂNG ĐẾN THỜI ĐIỂM LỖI")
            .AppendLine(ExportPerformance())
            .ToString();

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
        // Ticked invoices run in list order; with none ticked, fall back to "from the selected invoice onward".
        var ticked = InvoiceQueue.Where(invoice => checkedFingerprints.Contains(invoice.Fingerprint)).ToArray();
        var explicitRun = ticked.Length > 0;
        if (!explicitRun && SelectedInvoice is null)
        { Status = "Tick chọn các hóa đơn cần chạy (hoặc chọn một hóa đơn) trước khi thêm vào MISA."; return; }
        if (!resume) { handledInBatch.Clear(); batchIndex = 0; }
        InvoiceDocument[] queue;
        if (explicitRun) queue = ticked;
        else
        {
            var selected = SelectedInvoice!;
            var offset = InvoiceQueue.ToList().FindIndex(invoice => invoice.Fingerprint == selected.Fingerprint);
            queue = offset < 0 ? [selected] : [.. InvoiceQueue.Skip(offset)];
        }
        queue = [.. queue.Where(invoice => !handledInBatch.Contains(invoice.Fingerprint))];
        if (queue.Length == 0) { Status = "Không còn hóa đơn nào cần chạy."; return; }
        var batchTotal = batchIndex + queue.Length;
        var first = queue[0];
        running = true;
        stopRequested = false;
        lastError = null;
        operation = "connect";
        var saved = 0;
        var existing = 0;
        var mismatched = 0;
        var skipped = 0;
        var blockedNumbers = new List<string>();
        string? stopNote = null;
        services.BeginUiBatch(first.Identity.Number);
        ResetPerformance();
        performanceStartedAt = DateTimeOffset.UtcNow;
        performanceInvoice = first.Identity.Number;
        PerformanceCurrent = "Chuẩn bị phiên";
        var wholeRun = BeginTrace("Toàn bộ function Thêm vào MISA", first.Identity.Number);
        var performanceOutcome = "Hoàn tất";
        NotifyWorkflowChanged();
        try
        {
            var initialize = BeginTrace("Khởi tạo phiên", "Đọc state và lịch sử workflow");
            await services.Initialization;
            await LoadHistoryAsync();
            FinishTrace(initialize, "Hoàn tất");
            if (services.ItemCatalog.Count == 0 && queue.Any(invoice => invoice.Lines.Any(line => line.ItemCode.Trim().Length == 0)))
            {
                Status = "Chưa nạp database mã hàng. Bấm 'Mã hàng' và chọn file danh mục vật tư, hàng hóa trước khi nhập.";
                return;
            }
            // One MISA session per batch: resolving it per invoice costs a UIA window scan each time.
            var findWindow = BeginTrace("Tìm cửa sổ MISA", "Xác định đúng PID/HWND phiên MISA");
            var identity = await ResolveWindowIdentityAsync();
            FinishTrace(findWindow, identity is null ? "Không hoàn tất" : "Hoàn tất", identity is null ? Status : null);
            if (identity is null) return;
            operation = "enter";
            SetConnection(MisaConnection.Connected, "");
            foreach (var invoice in queue)
            {
                if (stopRequested) break;
                var fingerprint = invoice.Fingerprint;
                var position = $"Hóa đơn {++batchIndex}/{batchTotal}: {invoice.Identity.Number}";
                if (IsDone(invoice) || (!explicitRun && HasDiscardedRun(invoice)))
                {
                    handledInBatch.Add(fingerprint);
                    SetChecked(fingerprint, false);
                    skipped++;
                    continue;
                }
                if (invoice.IsBlocked)
                {
                    if (!explicitRun)
                    { stopNote = $"Hóa đơn {invoice.Identity.Number} còn lỗi nguồn; cần bổ sung dữ liệu."; Status = stopNote; break; }
                    handledInBatch.Add(fingerprint);
                    blockedNumbers.Add(invoice.Identity.Number);
                    Status = $"{position} · còn lỗi nguồn, bỏ qua và chuyển sang hóa đơn kế tiếp.";
                    continue;
                }
                SelectedInvoice = invoice;
                PropertyChanged?.Invoke(this, new(nameof(SelectedInvoice)));
                performanceInvoice = invoice.Identity.Number;
                PropertyChanged?.Invoke(this, new(nameof(PerformanceInvoice)));
                Status = $"{position} · đang nhập và đối chiếu. Bấm Dừng ngay để hủy bước đang chạy."
                    + (explicitRun ? "" : " Chưa tick hóa đơn nào nên chạy từ hóa đơn đang chọn trở đi.");
                PerformanceCurrent = "live_run · đang chạy";
                OperationResult response;
                for (var rerun = 0; ; rerun++)
                {
                    var liveRun = BeginTrace("live_run", $"Nhập và đối chiếu hóa đơn {invoice.Identity.Number}"
                        + (rerun == 0 ? "" : $" · chạy lại lần {rerun}"));
                    response = await services.ExecuteAsync("live_run", new
                    {
                        invoice,
                        windowIdentity = identity,
                        executionMode = "live_production",
                        liveValidation = true,
                        allowKgToTon = true
                    });
                    FinishTrace(liveRun, response.Status == "completed" ? "Hoàn tất" : response.Status, response.Status == "completed" ? null : response.Message);
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
                Status = response.Message;
                NotifyWorkflowChanged();
                if (response.Status != "completed")
                {
                    performanceOutcome = response.Status;
                    RaiseError(response.Message, Serialize(response.Data));
                }
                // Update history in memory instead of re-reading every run file from disk.
                if (services.Workflow.Current is { } finished)
                {
                    History = [finished, .. History.Where(run => run.Snapshot.RunId != finished.Snapshot.RunId)];
                    NotifyWorkflowChanged();
                }
                // A completed run (saved, verified discard, or number already in MISA) advances the queue.
                // Any other response stops here so repair can rebuild and rerun from Ready.
                if (response.Status != "completed") break;
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
                    if (blockedNumbers.Count > 0) parts.Add($"{blockedNumbers.Count} thiếu dữ liệu ({string.Join(", ", blockedNumbers.Take(5))})");
                    var scope = explicitRun ? "các hóa đơn đã tick" : "từ hóa đơn đang chọn (chưa tick hóa đơn nào)";
                    Status = parts.Count == 0 ? "Không còn hóa đơn nào cần chạy " + (explicitRun ? "trong danh sách đã tick." : "từ vị trí đã chọn.")
                        : $"Xong {scope}: {string.Join(" · ", parts)}.";
                }
            }
        }
        catch (Exception error)
        {
            Status = error.Message;
            performanceOutcome = "Lỗi";
            RaiseError(error.Message, error.ToString());
            FinishActiveTrace("Lỗi", error.Message);
        }
        finally
        {
            if (stopRequested && performanceOutcome == "Hoàn tất") performanceOutcome = "Đã dừng";
            FinishActiveTrace(performanceOutcome);
            FinishAllRunning(performanceOutcome);
            FinishTrace(wholeRun, performanceOutcome);
            running = false;
            operation = null;
            services.EndUiBatch(performanceOutcome);
            PerformanceCurrent = "Đã kết thúc";
            NotifyWorkflowChanged();
        }
    }

    private void ResetPerformance()
    {
        PerformanceEntries.Clear();
        performanceTraceIndexes.Clear();
        activeNativeTraceId = null;
        activeNativeTrace = null;
        performanceStartedAt = null;
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
        if (performanceStartedAt is { } started) PerformanceElapsed = FormatDuration(DateTimeOffset.UtcNow - started);
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

    private async Task<string?> ResolveWindowIdentityAsync()
    {
        var windows = await services.Native.FindCandidatesAsync();
        if (windows.Count == 0) SetConnection(MisaConnection.Lost, "Không thấy cửa sổ MISA SME 2023 R20.");
        if (windows.Count != 1)
        {
            Status = "Cần đúng một phiên MISA đang mở; hãy đóng bớt phiên thừa rồi thử lại.";
            return null;
        }
        if (stopRequested) return null;
        var window = windows[0];
        return $"pid={window.ProcessId};start={window.ProcessStartUtc:O};hwnd={window.WindowHandle};exe={window.ExecutablePath}";
    }

    public async Task<OperationResult> ReconcileAsync()
    {
        if (SelectedInvoice is not { } invoice || services.Workflow.Current?.Invoice.Fingerprint != invoice.Fingerprint)
            return new("ui", "blocked", "Chọn hóa đơn đang gắn với chứng từ MISA để đối chiếu.");
        var owned = !running;
        if (owned) { running = true; operation = "reconcile"; NotifyWorkflowChanged(); }
        try
        {
            var result = await services.ExecuteAsync("reconcile", expectedRevision: invoice.Fingerprint);
            Status = result.Message;
            NotifyWorkflowChanged();
            return result;
        }
        finally { if (owned) { running = false; operation = null; NotifyWorkflowChanged(); } }
    }

    private void SetConnection(MisaConnection value, string detail)
    {
        if (connection == value && connectionDetail == detail) return;
        connection = value;
        connectionDetail = detail;
        PropertyChanged?.Invoke(this, new(nameof(Connection)));
        PropertyChanged?.Invoke(this, new(nameof(State)));
    }

    /// <summary>Cheap window-level check; skipped while a run owns the single UIA thread.</summary>
    public async Task ProbeConnectionAsync()
    {
        if (running || probing) return;
        probing = true;
        try
        {
            if (!services.Initialization.IsCompleted) { SetConnection(MisaConnection.Unknown, ""); return; }
            if (services.Initialization.IsFaulted)
            {
                SetConnection(MisaConnection.Lost, "Dịch vụ nền của MisaManager không khởi động được; hãy mở lại ứng dụng.");
                return;
            }
            var windows = await services.Native.FindCandidatesAsync();
            if (windows.Count == 0) SetConnection(MisaConnection.Lost, "Không thấy cửa sổ MISA SME 2023 R20.");
            else SetConnection(MisaConnection.Connected, "");
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
            var observation = await services.Native.ObserveAsync(window.WindowHandle, null, token,
                services.WorkflowProfile.SalesScopeId);
            return (services.MisaProfile.IsSalesSurface(observation.Elements), observation.Frame.Window);
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
            var observation = await services.Native.ObserveAsync(window.WindowHandle, null, token,
                services.WorkflowProfile.EditorId);
            var open = observation.Elements.Any(element =>
                element.AutomationId == services.WorkflowProfile.EditorId
                && element.ControlType == "Window" && element.Enabled && !element.Offscreen);
            return (open, observation.Frame.Window);
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
        public async void Execute(object? parameter) => await action();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
