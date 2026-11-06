using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace MisaManager.Automation;

// Sales-voucher specific: MISA's tax-difference prompt ("... tiền thuế GTGT ... chênh lệch <..>") shown on Cất.
/// <summary>Text of MISA's tax-difference prompt and whether it was answered "Có" (difference of at most 2 đồng).</summary>
public sealed record TaxPromptAnswer(string Text, bool Accepted);

public sealed partial class NativeInputService
{
    private static readonly string[] NoCaptions = ["&No", "No", "&Không", "Không"];
    private static readonly string[] YesCaptions = ["&Yes", "Yes", "&Có", "Có"];

    /// <summary>
    /// Answers MISA's tax-difference prompt: "Có" when every "chênh lệch &lt;d&gt;" is at most 2 đồng (MISA moves it into
    /// line 1; the user accepts that rounding), otherwise "Không" so the source tax is kept. The saved totals are read
    /// back afterwards either way. Returns the prompt text and answer, or null when no such prompt is open.
    /// </summary>
    public Task<TaxPromptAnswer?> AnswerTaxRoundingPromptAsync(nint editorHandle, CancellationToken token)
        => AnswerSaveQuestionAsync(editorHandle, NativeComparison.IsTaxPrompt, NativeComparison.TaxPromptAcceptable,
            "chênh lệch tiền thuế", token);

    /// <summary>
    /// Answers "Có" to MISA's Cất warning that the buyer's MST may have stopped operating ("... kiểm tra lại tình trạng hoạt
    /// động ... Bạn có muốn tiếp tục Cất không?"): the user wants the invoice saved anyway. Null when no such prompt is open.
    /// </summary>
    public Task<TaxPromptAnswer?> AnswerTaxpayerStatusPromptAsync(nint editorHandle, CancellationToken token)
        => AnswerSaveQuestionAsync(editorHandle, NativeComparison.IsTaxpayerStatusPrompt, _ => true, "tình trạng hoạt động MST", token);

    // One Có/Không question MISA asks while Cất is in progress; accept decides the answer from its text.
    private async Task<TaxPromptAnswer?> AnswerSaveQuestionAsync(nint editorHandle, Func<string, bool> isPrompt,
        Func<string, bool> acceptable, string subject, CancellationToken token)
    {
        // Finding the prompt only reads. MISA can stop answering UIA while it shows the prompt; that timeout
        // must stay a retryable read (NATIVE_READ_TIMEOUT), not an interrupted input that ends SaveUncertain.
        var found = await dispatcher.RunAsync<TaxPromptTarget?>(() =>
        {
            if (identity is null) throw new AutomationException("NOT_CONNECTED", "Chưa bind cửa sổ MISA.");
            var prompts = SaveQuestionPrompts(editorHandle, isPrompt, subject);
            if (prompts.Count == 0) return null;
            if (prompts.Count > 1)
                throw new AutomationException("SAVE_PROMPT_AMBIGUOUS", $"Có nhiều hộp thông báo {subject} cùng lúc; xử lý trong MISA rồi chạy lại.");
            var (dialog, text, no, yes) = prompts[0];
            // Without a single "Có" button (or when the text does not allow it), keep the safe answer "Không".
            var accept = acceptable(text) && (yes.Handle != nint.Zero || yes.Element is not null);
            var (handle, element) = accept ? yes : no;
            var caption = accept ? "Có" : "Không";
            if (handle != nint.Zero) return new TaxPromptTarget(dialog, text, accept, handle, 0, 0, nint.Zero);
            var bounds = element!.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new AutomationException("BOUNDS_UNREADABLE", $"Nút {caption} của hộp thông báo {subject} không có vị trí hiển thị.");
            return new TaxPromptTarget(dialog, text, accept, nint.Zero, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                WorkflowTargetHandle(element));
        }, token);
        if (found is not { } prompt) return null;
        var answer = prompt.Accept ? "Có" : "Không";
        return await dispatcher.RunAsync<TaxPromptAnswer?>(() =>
        {
            if (identity is null) throw new AutomationException("NOT_CONNECTED", "Chưa bind cửa sổ MISA.");
            var dialog = prompt.Dialog;
            // Closed since the scan: nothing to answer; the caller polls again.
            if (!PInvoke.IsWindow((HWND)dialog) || !PInvoke.IsWindowVisible((HWND)dialog)) return null;
            token.ThrowIfCancellationRequested();
            MarkWorkflowInput();
            epoch++;
            if (prompt.ButtonHandle != nint.Zero)
            {
                if (SendMessageTimeout(prompt.ButtonHandle, 0x00F5, 0, nint.Zero, 2, 1000, out _) == nint.Zero)
                    throw new AutomationException("INPUT_UNCERTAIN", $"Không xác nhận được thao tác bấm {answer}; không bấm lại.", true);
            }
            else
                NativeKeyboard.Click(prompt.X, prompt.Y, identity.WindowHandle, identity.ProcessId, prompt.TargetHandle);
            var closeBy = Environment.TickCount64 + 3000;
            while (PInvoke.IsWindow((HWND)dialog) && PInvoke.IsWindowVisible((HWND)dialog))
            {
                if (Environment.TickCount64 > closeBy)
                    throw new AutomationException("SAVE_PROMPT_NOT_CLOSED", $"Hộp thông báo {subject} chưa đóng sau khi bấm {answer}.", true);
                if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested();
            }
            return new TaxPromptAnswer(prompt.Text, prompt.Accept);
        }, token, maySendInput: true);
    }

    private sealed record TaxPromptTarget(nint Dialog, string Text, bool Accept, nint ButtonHandle, int X, int Y, nint TargetHandle);

    private List<(nint Dialog, string Text, (nint Handle, AutomationElement? Element) No, (nint Handle, AutomationElement? Element) Yes)>
        SaveQuestionPrompts(nint editorHandle, Func<string, bool> isPrompt, string subject)
    {
        var result = new List<(nint, string, (nint, AutomationElement?), (nint, AutomationElement?))>();
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
            var yesButtons = new List<nint>();
            EnumChildWindows(handle, (child, _) =>
            {
                var label = WindowText(child).Trim();
                if (label.Length > 0) texts.Add(label.Normalize(NormalizationForm.FormC));
                if (PInvoke.IsWindowEnabled((HWND)child) && PInvoke.IsWindowVisible((HWND)child))
                {
                    if (NoCaptions.Contains(label)) noButtons.Add(child);
                    else if (YesCaptions.Contains(label)) yesButtons.Add(child);
                }
                return true;
            }, nint.Zero);
            var message = string.Join(" ", texts);
            if (isPrompt(message) && noButtons.Count == 1)
            {
                result.Add((handle, message, (noButtons[0], null), (yesButtons.Count == 1 ? yesButtons[0] : nint.Zero, null)));
                continue;
            }
            // Windowless (DirectUI/WPF) prompts expose the text and buttons through UIA only.
            try
            {
                var window = automation.FromHandle(handle);
                var parts = window.FindAllDescendants()
                    .Where(element => element.ControlType is ControlType.Text or ControlType.Edit or ControlType.Document)
                    .Select(PromptText)
                    .Append(window.Properties.Name.ValueOrDefault ?? "")
                    .Where(value => !string.IsNullOrWhiteSpace(value));
                var uiaMessage = System.Net.WebUtility.HtmlDecode(string.Join(" ", parts));
                if (!isPrompt(uiaMessage)) continue;
                var enabled = window.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Button))
                    .Where(button => button.Properties.IsEnabled.ValueOrDefault && !button.Properties.IsOffscreen.ValueOrDefault).ToArray();
                AutomationElement[] Find(NativeSelector selector, string[] captions) => enabled
                    .Where(button => selector.AutomationId.Length > 0 && button.Properties.AutomationId.ValueOrDefault == selector.AutomationId
                        || captions.Contains(button.Properties.Name.ValueOrDefault?.Trim())).ToArray();
                var buttons = Find(RequireProfile().TaxPromptNo, NoCaptions);
                if (buttons.Length != 1)
                    throw new AutomationException("SAVE_PROMPT_AMBIGUOUS", $"Không xác định duy nhất nút Không trên hộp thông báo {subject}.");
                var yes = Find(RequireProfile().TaxPromptYes, YesCaptions);
                result.Add((handle, uiaMessage, (nint.Zero, buttons[0]), (nint.Zero, yes.Length == 1 ? yes[0] : null)));
            }
            catch (FlaUI.Core.Exceptions.ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        return result;
    }

    // Only identifies the prompt (no input is verified here): MISA's message box shows its text in labels that
    // expose just a Name, without Value/Text pattern or HWND.
    private static string PromptText(AutomationElement element)
    {
        try
        {
            return ReadNativeText(element).Value;
        }
        catch (AutomationException error) when (error.Code == "READBACK_UNSUPPORTED")
        {
            return element.Properties.Name.ValueOrDefault ?? "";
        }
    }
}
