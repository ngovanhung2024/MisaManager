using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace MisaManager.Automation;

// Sales-voucher specific: MISA's "Tổng tiền thuế GTGT <..> khác ... chênh lệch <..>" prompt shown on Cất.
public sealed partial class NativeInputService
{
    private static readonly string[] TaxPromptKeys = [TextKey.Compact("Tổng tiền thuế"), TextKey.Compact("chênh lệch")];
    private static readonly string[] NoCaptions = ["&No", "No", "&Không", "Không"];

    /// <summary>
    /// Answers MISA's tax-difference prompt with "Không" whatever the difference, so MISA never moves the
    /// difference into the first line and the source tax typed by the tool is kept (the saved totals are read
    /// back afterwards). Returns the prompt text, or null when no such prompt is open.
    /// </summary>
    public Task<string?> AnswerTaxRoundingPromptAsync(nint editorHandle, CancellationToken token)
        => dispatcher.RunAsync<string?>(() =>
        {
            if (identity is null) throw new AutomationException("NOT_CONNECTED", "Chưa bind cửa sổ MISA.");
            var prompts = TaxRoundingPrompts(editorHandle);
            if (prompts.Count == 0) return null;
            if (prompts.Count > 1)
                throw new AutomationException("SAVE_PROMPT_AMBIGUOUS", "Có nhiều hộp thông báo chênh lệch tiền thuế cùng lúc; xử lý trong MISA rồi chạy lại.");
            var (dialog, text, noHandle, noElement) = prompts[0];
            token.ThrowIfCancellationRequested();
            MarkWorkflowInput();
            epoch++;
            if (noHandle != nint.Zero)
            {
                if (SendMessageTimeout(noHandle, 0x00F5, 0, nint.Zero, 2, 1000, out _) == nint.Zero)
                    throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được thao tác bấm Không; không bấm lại.", true);
            }
            else
            {
                var bounds = noElement!.BoundingRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    throw new AutomationException("BOUNDS_UNREADABLE", "Nút Không của hộp thông báo thuế không có vị trí hiển thị.");
                NativeKeyboard.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                    identity.WindowHandle, identity.ProcessId, WorkflowTargetHandle(noElement));
            }
            var closeBy = Environment.TickCount64 + 3000;
            while (PInvoke.IsWindow((HWND)dialog) && PInvoke.IsWindowVisible((HWND)dialog))
            {
                if (Environment.TickCount64 > closeBy)
                    throw new AutomationException("SAVE_PROMPT_NOT_CLOSED", "Hộp thông báo chênh lệch tiền thuế chưa đóng sau khi bấm Không.", true);
                if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested();
            }
            return text;
        }, token, maySendInput: true);

    private List<(nint Dialog, string Text, nint NoHandle, AutomationElement? NoElement)> TaxRoundingPrompts(nint editorHandle)
    {
        var result = new List<(nint, string, nint, AutomationElement?)>();
        var main = identity!.WindowHandle;
        var candidates = new List<nint>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            var title = WindowText(handle).Trim();
            // Message boxes are short "MISA SME 2023" windows; the main window and large forms carry " - ".
            if (processId == identity.ProcessId && handle != main && PInvoke.IsWindowVisible((HWND)handle)
                && title.StartsWith("MISA", StringComparison.OrdinalIgnoreCase) && !title.Contains(" - ", StringComparison.Ordinal)
                && (editorHandle != nint.Zero && OwnedBy(handle, editorHandle) || OwnedBy(handle, main)))
                candidates.Add(handle);
            return true;
        }, nint.Zero);
        foreach (var handle in candidates)
        {
            var texts = new List<string>();
            var noButtons = new List<nint>();
            EnumChildWindows(handle, (child, _) =>
            {
                var label = WindowText(child).Trim();
                if (label.Length > 0) texts.Add(label.Normalize(NormalizationForm.FormC));
                if (NoCaptions.Contains(label) && PInvoke.IsWindowEnabled((HWND)child) && PInvoke.IsWindowVisible((HWND)child))
                    noButtons.Add(child);
                return true;
            }, nint.Zero);
            var message = string.Join(" ", texts);
            if (IsTaxPrompt(message) && noButtons.Count == 1)
            {
                result.Add((handle, message, noButtons[0], null));
                continue;
            }
            // Windowless (DirectUI/WPF) prompts expose the text and buttons through UIA only.
            try
            {
                var window = automation.FromHandle(handle);
                var parts = window.FindAllDescendants()
                    .Where(element => element.ControlType is ControlType.Text or ControlType.Edit or ControlType.Document)
                    .Select(element => ReadNativeText(element).Value)
                    .Append(window.Properties.Name.ValueOrDefault ?? "")
                    .Where(value => !string.IsNullOrWhiteSpace(value));
                var uiaMessage = System.Net.WebUtility.HtmlDecode(string.Join(" ", parts));
                if (!IsTaxPrompt(uiaMessage)) continue;
                var noId = RequireProfile().TaxPromptNo.AutomationId;
                var buttons = window.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Button))
                    .Where(button => button.Properties.IsEnabled.ValueOrDefault && !button.Properties.IsOffscreen.ValueOrDefault
                        && (noId.Length > 0 && button.Properties.AutomationId.ValueOrDefault == noId
                            || NoCaptions.Contains(button.Properties.Name.ValueOrDefault?.Trim())))
                    .ToArray();
                if (buttons.Length != 1)
                    throw new AutomationException("SAVE_PROMPT_AMBIGUOUS", "Không xác định duy nhất nút Không trên hộp thông báo chênh lệch tiền thuế.");
                result.Add((handle, uiaMessage, nint.Zero, buttons[0]));
            }
            catch (FlaUI.Core.Exceptions.ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        return result;
    }

    private static bool IsTaxPrompt(string message)
    {
        var key = TextKey.Compact(message);
        return TaxPromptKeys.All(part => key.Contains(part, StringComparison.Ordinal));
    }
}
