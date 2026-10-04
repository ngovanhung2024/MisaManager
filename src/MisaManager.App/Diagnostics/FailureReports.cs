using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MisaManager.Automation;
using MisaManager.Core;

namespace MisaManager.App;

public sealed record UiErrorRecord(string Message, string Details, DateTimeOffset OccurredAt);

/// <summary>
/// Captures what MISA looked like when a step failed and packages it, with state and logs,
/// into one zip the user can hand to a developer or an AI assistant.
/// </summary>
public sealed partial class AppServices
{
    private sealed record FailureCapture(string RunId, string Step, DateTimeOffset At, UiFailureSnapshot Ui,
        byte[]? Screenshot, IReadOnlyList<WorkflowStepTiming> Steps);

    private static readonly JsonSerializerOptions ReportJson = new(ContractJson.Options) { WriteIndented = true };
    private readonly object reportGate = new();
    private UiErrorRecord? lastUiError;
    private FailureCapture? lastFailure;
    private string? batchInvoice;
    private DateTimeOffset? batchStartedAt;
    private string? batchOutcome;

    public void BeginUiBatch(string invoiceNumber)
    {
        lock (reportGate) { batchInvoice = invoiceNumber; batchStartedAt = DateTimeOffset.UtcNow; batchOutcome = null; lastUiError = null; }
    }

    public void EndUiBatch(string outcome)
    {
        lock (reportGate) batchOutcome = outcome;
    }

    public void RecordUiError(string message, string details)
    {
        lock (reportGate) lastUiError = new(message, Clip(details, 20000), DateTimeOffset.UtcNow);
    }

    // Runs before the runner discards the voucher, while the failing dialog/focus is still on screen.
    private async Task CaptureFailureUiAsync(WorkflowContext context, string step)
    {
        var screenshot = await ScreenCapture.CaptureVirtualScreenAsync();
        UiFailureSnapshot ui;
        if (!TryReadHandle(context.Snapshot.WindowIdentity ?? context.WindowIdentity, out var handle))
            ui = new(0, "", [], [], [], DateTimeOffset.UtcNow, "Workflow chưa gắn cửa sổ MISA.");
        else
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { ui = await Native.InspectFailureAsync(handle, FailureTargets(step), timeout.Token); }
            catch (Exception error) { ui = new(handle, "", [], [], [], DateTimeOffset.UtcNow, error.Message); }
        }
        var steps = workflowGateway.Timeline().Steps;
        lock (reportGate) lastFailure = new(context.Snapshot.RunId, step, DateTimeOffset.UtcNow, ui, screenshot, steps);
    }

    private string[] FailureTargets(string? step)
        => step is not null && WorkflowProfile.Fields.TryGetValue(step, out var selector) && selector.AutomationId.Length > 0
            ? [selector.AutomationId] : [];

    internal static bool TryReadHandle(string? serialized, out nint handle)
    {
        handle = 0;
        var part = serialized?.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2)).FirstOrDefault(item => item.Length == 2 && item[0] == "hwnd");
        if (part is null || !long.TryParse(part[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value == 0)
            return false;
        handle = (nint)value;
        return true;
    }

    /// <summary>Writes the diagnostic zip and returns its path. Never sends input to MISA.</summary>
    public async Task<string> ExportFailureReportAsync(string? uiMessage = null, string? uiDetails = null,
        string? performance = null, CancellationToken token = default)
    {
        var createdAt = DateTimeOffset.Now;
        var current = Workflow.Current;
        var snapshot = current?.Snapshot;
        FailureCapture? failure;
        UiErrorRecord? uiError;
        lock (reportGate)
        {
            failure = lastFailure;
            uiError = uiMessage is null ? lastUiError : new(uiMessage, uiDetails ?? "", createdAt);
        }
        var live = Workflow.LastLiveRun is { } last && last.Context.Snapshot.RunId == snapshot?.RunId ? last : null;
        LiveVerificationRecord? verification = live?.Verification;
        if (verification is null && snapshot is not null)
        {
            try { verification = (await Workflow.ReadLiveVerificationHistoryAsync(token)).FirstOrDefault(item => item.RunId == snapshot.RunId); }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException) { }
        }

        var freshScreenshot = await ScreenCapture.CaptureVirtualScreenAsync();
        object? uiNow = null;
        object? screens = null;
        string? uiNowError = null;
        bool busy;
        lock (reportGate) busy = batchStartedAt is not null && batchOutcome is null;
        var windows = await SafeAsync(() => Native.FindCandidatesAsync(token));
        var session = windows?.FirstOrDefault(window => snapshot?.WindowIdentity is { } id && TryReadHandle(id, out var h) && h == window.WindowHandle)
            ?? windows?.FirstOrDefault(window => window.Title.StartsWith(WorkflowProfile.VersionTitle + " - ", StringComparison.Ordinal));
        if (busy) uiNowError = "Tool đang nhập hóa đơn; không đọc giao diện MISA để tránh làm chậm/chen ngang. Xem ui-at-failure.json.";
        else if (session is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { uiNow = await Native.InspectFailureAsync(session.WindowHandle, FailureTargets(failure?.Step), timeout.Token); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { uiNowError = error.Message; }
            try { screens = await CheckScreensAsync(session, timeout.Token); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { screens = new { error = error.Message }; }
        }
        else uiNowError = "Không tìm thấy phiên MISA đang mở.";

        var lineIndex = snapshot?.ActiveLine;
        var activeLine = lineIndex is >= 0 && current is not null && lineIndex < current.Invoice.Lines.Length
            ? current.Invoice.Lines[lineIndex.Value] : null;
        var summary = new
        {
            documentType = "MisaManager.FailureReport",
            schemaVersion = 1,
            createdAt,
            app = new
            {
                version = ProductIdentity.RuntimeVersion,
                sourceRevision = ProductIdentity.SourceRevision,
                profile = new { WorkflowProfile.Id, WorkflowProfile.Revision, WorkflowProfile.VersionTitle },
                os = Environment.OSVersion.VersionString,
                machine = Environment.MachineName,
                is64Bit = Environment.Is64BitOperatingSystem,
                culture = CultureInfo.CurrentCulture.Name
            },
            batch = new { invoice = batchInvoice, startedAt = batchStartedAt, outcome = batchOutcome },
            error = new
            {
                code = snapshot?.LastErrorCode ?? verification?.ErrorCode,
                message = snapshot?.LastMessage ?? verification?.Error ?? uiError?.Message,
                chain = live?.Error is { } liveError && liveError != snapshot?.ErrorChain
                    ? liveError + Environment.NewLine + snapshot?.ErrorChain : snapshot?.ErrorChain ?? verification?.ErrorChain,
                failedStep = failure?.RunId == snapshot?.RunId ? failure?.Step : null,
                failedAt = failure?.At,
                ui = uiError
            },
            workflow = snapshot,
            outcome = live?.Outcome,
            expected = current is null ? null : new
            {
                current.Invoice.Identity, current.Invoice.Source, buyer = current.Invoice.Buyer,
                current.Invoice.VoucherDate, current.Invoice.PostedDate,
                current.Invoice.Subtotal, current.Invoice.TaxTotal, current.Invoice.GrandTotal,
                lineCount = current.Invoice.Lines.Length, activeLineIndex = lineIndex, activeLine
            },
            nativeValues = current?.NativeValues,
            comparison = verification,
            steps = (failure?.RunId == snapshot?.RunId ? failure?.Steps : null) ?? workflowGateway.Timeline().Steps,
            metrics = workflowGateway.Metrics,
            misaWindows = windows
        };

        var directory = Path.Combine(LogRoot, "reports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"misa-report-{createdAt:yyyyMMdd-HHmmss}.zip");
        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            Text(zip, "README-AI.md", AiReadme(summary.error.code, summary.error.message));
            Text(zip, "summary.json", JsonSerializer.Serialize(summary, ReportJson));
            if (failure is not null)
            {
                Text(zip, "ui-at-failure.json", JsonSerializer.Serialize(new { failure.RunId, failure.Step, failure.At, failure.Ui }, ReportJson));
                if (failure.Screenshot is { } shot) Bytes(zip, "screen-at-failure.png", shot);
            }
            Text(zip, "ui-now.json", JsonSerializer.Serialize(new { ui = uiNow, error = uiNowError }, ReportJson));
            if (screens is not null) Text(zip, "screen-check.json", JsonSerializer.Serialize(screens, ReportJson));
            if (freshScreenshot is not null) Bytes(zip, "screen-now.png", freshScreenshot);
            if (!string.IsNullOrWhiteSpace(performance)) Text(zip, "performance.txt", performance);
            CopyRecentLogs(zip);
        }, token);
        return path;
    }

    private void CopyRecentLogs(ZipArchive zip)
    {
        if (!Directory.Exists(LogRoot)) return;
        var cutoff = DateTime.UtcNow.AddDays(-2);
        foreach (var file in Directory.EnumerateFiles(LogRoot, "*", SearchOption.TopDirectoryOnly)
                     .Select(path => new FileInfo(path))
                     .Where(info => info.LastWriteTimeUtc >= cutoff && info.Extension is ".log" or ".jsonl" or ".json")
                     .OrderByDescending(info => info.LastWriteTimeUtc).Take(12))
        {
            try
            {
                using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // Keep only the tail of large logs: the failure is at the end.
                const long limit = 2 * 1024 * 1024;
                if (source.Length > limit) source.Seek(-limit, SeekOrigin.End);
                using var target = zip.CreateEntry("logs/" + file.Name, CompressionLevel.Optimal).Open();
                source.CopyTo(target);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string AiReadme(string? code, string? message) => $$"""
        # Báo cáo lỗi MisaManager

        Gói này được tạo bởi nút **Xuất báo cáo lỗi** trong MisaManager (tool nhập hóa đơn Excel vào MISA SME 2023 R20 bằng UI Automation).
        Hãy gửi cả file zip này cho AI/lập trình viên kèm mô tả ngắn bạn đang làm gì.

        - Mã lỗi: `{{code ?? "không có"}}`
        - Thông báo: {{message ?? "không có"}}

        ## Các file
        | File | Nội dung |
        |---|---|
        | `summary.json` | Phiên bản app/profile, lỗi + chuỗi exception, bước đang chạy (`error.failedStep`), checkpoint workflow, dữ liệu hóa đơn mong đợi, dòng hàng đang nhập, giá trị đã đọc từ MISA, kết quả đối chiếu, timeline từng bước. |
        | `ui-at-failure.json` | Cây UIA rút gọn **ngay lúc lỗi** (trước khi tool Bỏ chứng từ): hộp thoại đang mở, đường focus, control đích. |
        | `screen-at-failure.png` | Ảnh chụp toàn màn hình ngay lúc lỗi. |
        | `ui-now.json`, `screen-now.png` | Trạng thái hiện tại khi xuất báo cáo. |
        | `screen-check.json` | Kết quả kiểm tra selector của profile (`profiles/misa-sme2023-r20.json`) trên các màn hình MISA đang mở: selector nào không tìm thấy/không duy nhất. |
        | `logs/` | Nhật ký checkpoint (JSONL) và crash log gần nhất. |

        ## Gợi ý cho AI
        1. Đọc `summary.json` → `error` và `steps` để biết bước nào lỗi; đối chiếu `expected` với `nativeValues`.
        2. Nếu `screen-check.json` báo selector thiếu → MISA đã đổi giao diện: sửa `profiles/misa-sme2023-r20.json` (xem AGENTS.md mục "Cập nhật profile").
        3. Mã nguồn liên quan: `src/MisaManager.Automation/Workflows/SalesVoucher/*` (luồng nhập), `src/MisaManager.Automation/Screens/*` (thao tác control), `src/MisaManager.Core/Workflow.cs` (state machine).
        4. Ảnh chụp có thể chứa dữ liệu kế toán thật — không đăng công khai.
        """;

    private static void Text(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void Bytes(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        stream.Write(content);
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T>> action) where T : class
    {
        try { return await action(); }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
