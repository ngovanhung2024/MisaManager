using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;

namespace MisaManager.Automation;

// Sales-voucher specific: MISA's "tổng tiền thuế chênh lệch" prompt shown on Cất.
public sealed partial class NativeInputService
{
    public Task<bool> TryPreserveSourceTaxAsync(WindowIdentity window, string editorId, string? editorTitle,
        NativeSelector voucherField, string expectedVoucher, decimal expectedTax, int lineCount, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            var editors = WorkflowScopes(window, editorId);
            if (editors.Length == 0) return false;
            if (editors.Length != 1 || editorTitle is not null && editors[0].Name != editorTitle)
                throw new AutomationException("EDITOR_IDENTITY_INVALID", "Cannot bind the invoice editor for tax prompt resolution.");
            var editor = editors[0];
            var dialogs = editor.FindAllDescendants(automation.ConditionFactory.ByAutomationId(RequireProfile().TaxPromptScopeId))
                .Where(item => item.Properties.ProcessId.ValueOrDefault == window.ProcessId
                    && item.Properties.IsEnabled.ValueOrDefault && !item.Properties.IsOffscreen.ValueOrDefault).ToArray();
            if (dialogs.Length == 0) return false;
            if (dialogs.Length != 1)
                throw new AutomationException("SAVE_PROMPT_AMBIGUOUS", "Multiple save prompts belong to the voucher.");
            var dialog = dialogs[0];
            var editorHandle = (nint)editor.Properties.NativeWindowHandle.ValueOrDefault;
            var dialogHandle = (nint)dialog.Properties.NativeWindowHandle.ValueOrDefault;
            if (editorHandle == nint.Zero || dialogHandle == nint.Zero || !OwnedBy(dialogHandle, editorHandle))
                throw new AutomationException("SAVE_PROMPT_OWNER_INVALID", "Tax prompt is not owned by the bound voucher.");
            var vouchers = editor.FindAllDescendants(automation.ConditionFactory.ByAutomationId(voucherField.AutomationId))
                .Where(item => item.Properties.ProcessId.ValueOrDefault == window.ProcessId
                    && !item.Properties.IsOffscreen.ValueOrDefault)
                .Where(item => string.Equals(ReadNativeText(item).Value, expectedVoucher, StringComparison.Ordinal))
                .ToArray();
            if (vouchers.Length == 0)
                throw new AutomationException("VOUCHER_IDENTITY_CHANGED", "Tax prompt voucher identity differs from the run.");
            var messages = dialog.FindAllDescendants(automation.ConditionFactory.ByAutomationId(RequireProfile().TaxPromptMessage.AutomationId));
            if (messages.Length != 1)
                throw new AutomationException("SAVE_PROMPT_UNREADABLE", "A unique complete tax prompt message is required.");
            var message = System.Net.WebUtility.HtmlDecode(ReadNativeText(messages[0]).Value);
            var match = System.Text.RegularExpressions.Regex.Match(message,
                @"^T\u1ed5ng ti\u1ec1n thu\u1ebf GTGT <(?<source>[^<>]+)> kh\u00e1c \(Ti\u1ec1n h\u00e0ng - Ti\u1ec1n chi\u1ebft kh\u1ea5u\) \* Thu\u1ebf su\u1ea5t <(?<calculated>[^<>]+)>, ch\u00eanh l\u1ec7ch <(?<difference>[^<>]+)>\. B\u1ea1n c\u00f3 mu\u1ed1n ch\u01b0\u01a1ng tr\u00ecnh t\u1ef1 \u0111i\u1ec1u ch\u1ec9nh s\u1ed1 ti\u1ec1n ch\u00eanh l\u1ec7ch n\u00e0y v\u00e0o d\u00f2ng h\u00e0ng \u0111\u1ea7u ti\u00ean c\u00f3 ti\u1ec1n thu\u1ebf kh\u00f4ng\? Xem h\u01b0\u1edbng d\u1eabn <a href=""https://helpsme\.misa\.vn/2023/kb/canh_bao_tong_tien_thue_chenh_lech_khi_cat_chung_tu_ban_hang/"">T\u1ea0I \u0110\u00c2Y</a>$");
            var culture = CultureInfo.GetCultureInfo("vi-VN");
            if (!match.Success
                || !decimal.TryParse(match.Groups["source"].Value, NumberStyles.Number, culture, out var sourceTax)
                || !decimal.TryParse(match.Groups["calculated"].Value, NumberStyles.Number, culture, out var calculatedTax)
                || !decimal.TryParse(match.Groups["difference"].Value, NumberStyles.Number, culture, out var difference)
                || sourceTax != expectedTax || Math.Abs(sourceTax - calculatedTax) != Math.Abs(difference)
                || difference == 0 || Math.Abs(difference) > lineCount)
                throw new AutomationException("SAVE_PROMPT_REQUIRES_ATTENTION", "Unrecognized or non-rounding save prompt: " + message);
            var buttons = dialog.FindAllDescendants(automation.ConditionFactory.ByAutomationId(RequireProfile().TaxPromptNo.AutomationId))
                .Where(item => item.Properties.ProcessId.ValueOrDefault == window.ProcessId
                    && item.Properties.IsEnabled.ValueOrDefault && !item.Properties.IsOffscreen.ValueOrDefault
                    && item.Patterns.Invoke.IsSupported).ToArray();
            if (buttons.Length != 1)
                throw new AutomationException("SAVE_PROMPT_AMBIGUOUS", "A unique native No command is required.");
            identity = window; root = dialog; epoch++;
            GuardWorkflow(Capture());
            var bounds = buttons[0].BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new AutomationException("BOUNDS_UNREADABLE", "Tax prompt command bounds are unavailable.");
            token.ThrowIfCancellationRequested();
            MarkWorkflowInput();
            NativeKeyboard.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                window.WindowHandle, window.ProcessId, WorkflowTargetHandle(buttons[0]));
            epoch++;
            return true;
        }, token, maySendInput: true);
}
