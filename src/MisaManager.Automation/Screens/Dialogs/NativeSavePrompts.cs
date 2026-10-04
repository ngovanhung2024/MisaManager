using System.Text;
using MisaManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace MisaManager.Automation;

public sealed partial class NativeInputService
{
    public Task<nint> BoundScopeHandleAsync(CancellationToken token)
        => dispatcher.RunAsync(() => root is null ? nint.Zero : (nint)root.Properties.NativeWindowHandle.ValueOrDefault, token);

    /// <summary>
    /// Waits up to <paramref name="waitMilliseconds"/> for MISA's save prompt
    /// "Ký hiệu hóa đơn &lt;..&gt;, Số hóa đơn &lt;..&gt; đã tồn tại. Bạn có muốn chương trình tự động tăng số hóa đơn không?"
    /// and answers No, so MISA never renumbers the invoice. Returns the prompt text, or null when it did not appear.
    /// </summary>
    public Task<string?> AnswerDuplicateInvoicePromptAsync(nint editorHandle, int waitMilliseconds, CancellationToken token)
        => dispatcher.RunAsync<string?>(() =>
        {
            if (identity is null) throw new AutomationException("NOT_CONNECTED", "Chưa bind cửa sổ MISA.");
            var deadline = Environment.TickCount64 + Math.Max(0, waitMilliseconds);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var prompts = DuplicateInvoicePrompts(editorHandle);
                if (prompts.Count > 1)
                    throw new AutomationException("SAVE_PROMPT_AMBIGUOUS", "Có nhiều hộp thông báo 'số hóa đơn đã tồn tại' cùng lúc; xử lý trong MISA rồi chạy lại.");
                if (prompts.Count == 1)
                {
                    var (dialog, text, no) = prompts[0];
                    MarkWorkflowInput();
                    epoch++;
                    if (SendMessageTimeout(no, 0x00F5, 0, nint.Zero, 2, 1000, out _) == nint.Zero)
                        throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được thao tác bấm No; không bấm lại.", true);
                    var closeBy = Environment.TickCount64 + 3000;
                    while (PInvoke.IsWindow((HWND)dialog) && PInvoke.IsWindowVisible((HWND)dialog))
                    {
                        if (Environment.TickCount64 > closeBy)
                            throw new AutomationException("SAVE_PROMPT_NOT_CLOSED", "Hộp thông báo 'số hóa đơn đã tồn tại' chưa đóng sau khi bấm No.", true);
                        if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested();
                    }
                    return text;
                }
                if (Environment.TickCount64 >= deadline) return null;
                if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested();
            }
        }, token, maySendInput: true);

    private List<(nint Dialog, string Text, nint No)> DuplicateInvoicePrompts(nint editorHandle)
    {
        var result = new List<(nint, string, nint)>();
        var main = identity!.WindowHandle;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            if (processId != identity.ProcessId || !PInvoke.IsWindowVisible((HWND)handle)
                || !WindowText(handle).StartsWith("MISA", StringComparison.OrdinalIgnoreCase)
                || !(editorHandle != nint.Zero && OwnedBy(handle, editorHandle) || OwnedBy(handle, main)))
                return true;
            var texts = new List<string>();
            var noButtons = new List<nint>();
            EnumChildWindows(handle, (child, _) =>
            {
                var label = WindowText(child).Trim();
                if (label.Length > 0) texts.Add(label.Normalize(NormalizationForm.FormC));
                if (label is "&No" or "No" or "&Không" or "Không" && PInvoke.IsWindowEnabled((HWND)child)
                    && PInvoke.IsWindowVisible((HWND)child)) noButtons.Add(child);
                return true;
            }, nint.Zero);
            var message = string.Join(" ", texts);
            if (noButtons.Count == 1 && message.Contains("đã tồn tại", StringComparison.OrdinalIgnoreCase)
                && message.Contains("số hóa đơn", StringComparison.OrdinalIgnoreCase))
                result.Add((handle, message, noButtons[0]));
            return true;
        }, nint.Zero);
        return result;
    }
}
