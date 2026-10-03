using FlaUI.Core.AutomationElements;
using MisaManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MisaManager.Automation;

/// <summary>Outcome of one poll after clicking "Cất" in MISA's new-item dialog.</summary>
public sealed record NewItemSaveState(bool DialogOpen, string? Message);

public sealed partial class NativeInputService
{
    // HWND of the bound "Thêm Vật tư, hàng hóa, dịch vụ" dialog; input there must stay in this window.
    private nint newItemHandle;

    public Task OpenNewItemAsync(NativeFrame frame, NativeCell cell, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var target = ResolveCell(cell, requireEmptyCode: true, preserveComboBox: true);
            RealizeAndScroll(target);
            target = ResolveCell(cell, requireEmptyCode: true, preserveComboBox: true);
            FocusTarget(target, activateGridCell: true, token: token, verifyFocus: false);
            // F9 opens the new-item dialog only from the item grid; refuse when Win32 focus left it.
            var gridHandle = KeyboardHandle(target);
            if (!NativeKeyboard.HasTargetFocus(gridHandle))
                throw new AutomationException("FOCUS_FAILED", "Ô Mã hàng chưa nhận focus; không nhấn F9 để tránh gửi phím sang ô khác.", true);
            RecordInput();
            MarkWorkflowInput();
            NativeKeyboard.Press(gridHandle, identity!.WindowHandle, identity.ProcessId, "F9", token);
            epoch++;
            return true;
        }, token, maySendInput: true);

    public Task<NativeFrame> BindNewItemAsync(NativeSelector code, NativeSelector name, NativeSelector save, CancellationToken token)
        => NativeWait.UntilAsync(() => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            Capture();
            var dialogs = HostWindows(identity!.ProcessId)
                .Where(element => element.NativeWindowHandle != identity.WindowHandle)
                .Select(element => element.Element)
                .Where(IsNewItemDialog)
                .ToArray();
            if (dialogs.Length == 0)
                throw new AutomationException("TARGET_NOT_FOUND", "MISA new-item dialog is not ready.");
            if (dialogs.Length != 1)
                throw new AutomationException("NEW_ITEM_DIALOG_AMBIGUOUS", "Đang mở nhiều hộp Thêm vật tư; đóng bớt rồi chạy lại.");
            var dialog = dialogs[0];
            // R20 exposes the modal during its opening transition; bind only an
            // enabled, onscreen top-level dialog whose required controls are live.
            var handle = (nint)dialog.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle == nint.Zero || !dialog.Properties.IsEnabled.ValueOrDefault || dialog.Properties.IsOffscreen.ValueOrDefault
                || !PInvoke.IsWindowVisible((HWND)handle) || !PInvoke.IsWindowEnabled((HWND)handle)
                || (nint)PInvoke.GetAncestor((HWND)handle, GET_ANCESTOR_FLAGS.GA_ROOT) != handle)
                throw new AutomationException("TARGET_NOT_FOUND", "MISA new-item dialog is still opening.");
            root = dialog;
            epoch++;
            var controls = ResolveMany([code, name, save]);
            if (controls.Any(control => !control.Properties.IsEnabled.ValueOrDefault || control.Properties.IsOffscreen.ValueOrDefault))
                throw new AutomationException("TARGET_NOT_FOUND", "MISA new-item controls are not ready.");
            var frame = Capture();
            if (!root.Properties.IsEnabled.ValueOrDefault || root.Properties.IsOffscreen.ValueOrDefault
                || root.Properties.ProcessId.ValueOrDefault != identity!.ProcessId
                || root.Name != frame.TabIdentity)
                throw new AutomationException("TARGET_NOT_FOUND", "MISA new-item dialog scope is not stable yet.");
            newItemHandle = handle;
            return frame;
        }, token), _ => true, "NEW_ITEM_DIALOG_MISSING", "Hộp Thêm vật tư không mở được (F9 trên ô Mã hàng).", token,
            timeoutMilliseconds: 10000);

    /// <summary>
    /// Clicks the dialog's own "Cất" (never the voucher's "Cất") after proving the bound
    /// scope is still the same new-item dialog and the button belongs to that window.
    /// </summary>
    public Task<nint> SaveNewItemAsync(NativeFrame frame, NativeSelector save, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var dialogHandle = (nint)root!.Properties.NativeWindowHandle.ValueOrDefault;
            if (dialogHandle == nint.Zero || dialogHandle != newItemHandle || !IsNewItemDialog(root))
                throw new AutomationException("NEW_ITEM_SCOPE_CHANGED", "Hộp Thêm vật tư đã thay đổi; không bấm Cất.");
            ThrowIfBlockingDialog();
            var target = Resolve(save);
            if (!target.Properties.IsEnabled.ValueOrDefault || target.Properties.IsOffscreen.ValueOrDefault
                || !target.Patterns.Invoke.IsSupported)
                throw new AutomationException("ITEM_SAVE_TARGET_INVALID", "Nút Cất của hộp Thêm vật tư chưa sẵn sàng.");
            var owner = (nint)PInvoke.GetAncestor((HWND)WorkflowTargetHandle(target), GET_ANCESTOR_FLAGS.GA_ROOT);
            if (owner != dialogHandle)
                throw new AutomationException("ITEM_SAVE_TARGET_INVALID", "Nút Cất tìm được không thuộc hộp Thêm vật tư; không bấm.");
            var bounds = target.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new AutomationException("BOUNDS_UNREADABLE", "Nút Cất của hộp Thêm vật tư không có vị trí hiển thị.");
            token.ThrowIfCancellationRequested();
            try
            {
                MarkWorkflowInput();
                NativeKeyboard.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                    identity!.WindowHandle, identity.ProcessId, WorkflowTargetHandle(target));
                epoch++;
                // The dialog closes on success; later reads rebind the voucher editor.
                root = automation.FromHandle(identity.WindowHandle);
                newItemHandle = nint.Zero;
                return dialogHandle;
            }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", "Đã có thể bấm Cất mã hàng; cần kiểm tra lại.", true, error); }
        }, token, maySendInput: true);

    /// <summary>Read-only poll: is the new-item dialog still open, and is a MISA message box showing?</summary>
    public Task<NewItemSaveState> NewItemSaveStateAsync(nint dialogHandle, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery(false);
            if (identity is null) throw new AutomationException("NOT_CONNECTED", "Chưa bind cửa sổ MISA.");
            var open = PInvoke.IsWindow((HWND)dialogHandle) && PInvoke.IsWindowVisible((HWND)dialogHandle);
            string? message = null;
            foreach (var handle in OwnedDialogCandidates(new HashSet<nint> { dialogHandle }))
            {
                var texts = new List<string>();
                EnumChildWindows(handle, (child, _) =>
                {
                    var text = WindowText(child).Trim();
                    if (text.Length > 0 && PInvoke.IsWindowVisible((HWND)child)) texts.Add(text);
                    return true;
                }, nint.Zero);
                var title = WindowText(handle).Trim();
                // Ignore the voucher editor and other large MISA forms; message boxes are short Win32 dialogs.
                if (!title.StartsWith("MISA", StringComparison.OrdinalIgnoreCase) || title.Contains(" - ", StringComparison.Ordinal)) continue;
                message = title + ": " + string.Join(" | ", texts.Where(text => text is not ("&Yes" or "&No" or "Yes" or "No" or "OK" or "Cancel")).Take(6));
                break;
            }
            return new NewItemSaveState(open, message);
        }, token);

    private bool IsNewItemDialog(AutomationElement element)
    {
        var name = element.Properties.Name.ValueOrDefault ?? "";
        var id = element.Properties.AutomationId.ValueOrDefault ?? "";
        if (name.Contains("Thêm vật tư", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Them vat tu", StringComparison.OrdinalIgnoreCase)
            || id.Equals("themvattu", StringComparison.OrdinalIgnoreCase))
            return true;
        return element.FindAllDescendants(automation.ConditionFactory.ByAutomationId("txtInventoryItemCode")).Length == 1
            && element.FindAllDescendants(automation.ConditionFactory.ByAutomationId("txtInventoryItemName")).Length == 1;
    }
}
