using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MisaManager.Automation;

public sealed partial class NativeInputService
{
    // HWND of the bound "Thêm Khách hàng" dialog; input there must stay in this window.
    private nint newCustomerHandle;

    /// <summary>
    /// Presses the quick-add key (F9, same as the + button) on the voucher's still empty customer field.
    /// </summary>
    public Task OpenNewCustomerAsync(NativeFrame frame, NativeSelector customer, string key, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var target = Resolve(customer);
            var current = ReadNativeText(target).Value?.Trim() ?? "";
            if (current.Length > 0)
                throw new AutomationException("CUSTOMER_FIELD_NOT_EMPTY",
                    $"Ô Khách hàng đang có '{current}'; không mở hộp Thêm Khách hàng.");
            var inputTarget = ResolveKeyboardTarget(target);
            FocusTarget(inputTarget, activateGridCell: target.ControlType == ControlType.ComboBox, token: token);
            var handle = KeyboardHandle(inputTarget);
            if (!IsFocusedTarget(inputTarget, handle))
                throw new AutomationException("FOCUS_FAILED", "Ô Khách hàng chưa nhận focus; không nhấn F9 để tránh gửi phím sang ô khác.", true);
            RecordInput();
            MarkWorkflowInput();
            NativeKeyboard.Press(handle, identity!.WindowHandle, identity.ProcessId, key, token);
            epoch++;
            return true;
        }, token, maySendInput: true);

    public Task<NativeFrame> BindNewCustomerAsync(CustomerDialogProfile dialog, CancellationToken token)
        => NativeWait.UntilAsync(() => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            Capture();
            var dialogs = HostWindows(identity!.ProcessId)
                .Where(element => element.NativeWindowHandle != identity.WindowHandle)
                .Select(element => element.Element)
                .Where(element => IsNewCustomerDialog(element, dialog.Title))
                .ToArray();
            if (dialogs.Length == 0)
                throw new AutomationException("TARGET_NOT_FOUND", "MISA new-customer dialog is not ready.");
            if (dialogs.Length != 1)
                throw new AutomationException("NEW_CUSTOMER_DIALOG_AMBIGUOUS", "Đang mở nhiều hộp Thêm Khách hàng; đóng bớt rồi chạy lại.", true);
            var window = dialogs[0];
            var handle = (nint)window.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle == nint.Zero || !window.Properties.IsEnabled.ValueOrDefault || window.Properties.IsOffscreen.ValueOrDefault
                || !PInvoke.IsWindowVisible((HWND)handle) || !PInvoke.IsWindowEnabled((HWND)handle)
                || (nint)PInvoke.GetAncestor((HWND)handle, GET_ANCESTOR_FLAGS.GA_ROOT) != handle)
                throw new AutomationException("TARGET_NOT_FOUND", "MISA new-customer dialog is still opening.");
            root = window;
            epoch++;
            var controls = ResolveMany([dialog.Code, dialog.Save]);
            if (controls.Any(control => !control.Properties.IsEnabled.ValueOrDefault || control.Properties.IsOffscreen.ValueOrDefault))
                throw new AutomationException("TARGET_NOT_FOUND", "MISA new-customer controls are not ready.");
            var frame = Capture();
            if (!root.Properties.IsEnabled.ValueOrDefault || root.Properties.IsOffscreen.ValueOrDefault
                || root.Properties.ProcessId.ValueOrDefault != identity!.ProcessId
                || root.Name != frame.TabIdentity)
                throw new AutomationException("TARGET_NOT_FOUND", "MISA new-customer dialog scope is not stable yet.");
            newCustomerHandle = handle;
            return frame;
        }, token), _ => true, "NEW_CUSTOMER_DIALOG_MISSING",
            "Hộp Thêm Khách hàng không mở được (F9 / nút + trên ô Khách hàng).", token, timeoutMilliseconds: 10000);

    /// <summary>
    /// Shows the "1. Tổ chức" / "2. Cá nhân" layout whose name field is <paramref name="layoutField"/>. R20 does not expose
    /// these options as UIA RadioButtons, so the option is found by its caption whatever its control type, and the
    /// layout is proven by the visible name-field label instead of a selection state. Returns false when the layout was
    /// already shown (nothing sent).
    /// </summary>
    public Task<bool> EnsureNewCustomerKindAsync(NativeFrame frame, NativeSelector kind, NativeSelector layoutField, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            RequireNewCustomerScope();
            ThrowIfBlockingDialog();
            var pid = identity!.ProcessId;
            if (LabelledMatches(Snapshots(root!, Live(SelectorCondition(layoutField), pid)), layoutField).Length == 1) return false;
            var visible = Snapshots(root!, Live(automation.ConditionFactory.ByProcessId(pid), pid))
                .Where(item => !item.Offscreen && item.HasArea).ToArray();
            var key = LabelKey(kind.Name);
            var named = visible.Where(item => LabelKey(item.Name) == key).ToArray();
            var preferred = named.Where(item => item.ControlType is ControlType.RadioButton or ControlType.CheckBox or ControlType.Button).ToArray();
            var candidates = preferred.Length > 0 ? preferred : named;
            // A wrapper and its caption can both carry the name; the outermost one is the clickable option.
            candidates = candidates.Where(item => !candidates.Any(other => !ReferenceEquals(other, item) && other.Bounds != item.Bounds
                && other.Bounds.Contains(item.Bounds))).ToArray();
            if (candidates.Length != 1)
                throw new AutomationException(candidates.Length == 0 ? "CUSTOMER_KIND_NOT_FOUND" : "CUSTOMER_KIND_AMBIGUOUS",
                    $"Hộp Thêm Khách hàng: {(candidates.Length == 0 ? "không thấy" : $"có {candidates.Length}")} lựa chọn '{kind.Name}'. "
                    + "Control đang hiển thị: " + string.Join("; ", visible.Where(item => item.Name.Length > 0 || item.AutomationId.Length > 0)
                        .Take(80).Select(item => $"{item.ControlType} '{item.Name}' #{item.AutomationId}")));
            var target = candidates[0];
            token.ThrowIfCancellationRequested();
            try
            {
                MarkWorkflowInput();
                var selection = target.Element.Patterns.SelectionItem;
                if (target.ControlType == ControlType.RadioButton && selection.IsSupported) selection.Pattern.Select();
                else NativeKeyboard.Click(target.Bounds.X + target.Bounds.Width / 2, target.Bounds.Y + target.Bounds.Height / 2,
                    identity.WindowHandle, pid, WorkflowTargetHandle(target.Element));
                epoch++;
                return true;
            }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", $"Không xác nhận được thao tác chọn '{kind.Name}'.", true, error); }
        }, token, maySendInput: true);

    /// <summary>
    /// Closes the bound dialog with its own "Hủy bỏ" before anything was saved, so the voucher underneath can be discarded.
    /// MISA's "dữ liệu đã bị thay đổi, có muốn cất" prompt is answered No. Best effort: returns false when the dialog stays open.
    /// </summary>
    public Task<bool> CancelNewCustomerAsync(NativeSelector cancel, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            var dialogHandle = newCustomerHandle;
            if (identity is null || dialogHandle == nint.Zero || !PInvoke.IsWindow((HWND)dialogHandle)) return true;
            RecordInput();
            var dialog = automation.FromHandle(dialogHandle);
            var buttons = Snapshots(dialog, Live(SelectorCondition(cancel), identity.ProcessId)).Where(item => Matches(item, cancel)).ToArray();
            if (buttons.Length != 1) return false;
            var bounds = buttons[0].Bounds;
            MarkWorkflowInput();
            NativeKeyboard.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                identity.WindowHandle, identity.ProcessId, WorkflowTargetHandle(buttons[0].Element));
            epoch++;
            DismissDirtyVoucherPrompt(dialogHandle, token, waitMilliseconds: 1200);
            var deadline = Environment.TickCount64 + 3000;
            while (PInvoke.IsWindow((HWND)dialogHandle) && PInvoke.IsWindowVisible((HWND)dialogHandle) && Environment.TickCount64 < deadline)
                Thread.Sleep(50);
            var closed = !PInvoke.IsWindow((HWND)dialogHandle) || !PInvoke.IsWindowVisible((HWND)dialogHandle);
            if (closed)
            {
                root = automation.FromHandle(identity.WindowHandle);
                newCustomerHandle = nint.Zero;
            }
            return closed;
        }, token, maySendInput: true);

    /// <summary>
    /// Clicks the dialog's own "Cất" (never the voucher's) after proving the bound scope is still the same
    /// new-customer dialog and the button belongs to that window.
    /// </summary>
    public Task<nint> SaveNewCustomerAsync(NativeFrame frame, NativeSelector save, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var dialogHandle = RequireNewCustomerScope();
            ThrowIfBlockingDialog();
            var target = Resolve(save);
            if (!target.Properties.IsEnabled.ValueOrDefault || target.Properties.IsOffscreen.ValueOrDefault
                || !target.Patterns.Invoke.IsSupported)
                throw new AutomationException("CUSTOMER_SAVE_TARGET_INVALID", "Nút Cất của hộp Thêm Khách hàng chưa sẵn sàng.");
            var owner = (nint)PInvoke.GetAncestor((HWND)WorkflowTargetHandle(target), GET_ANCESTOR_FLAGS.GA_ROOT);
            if (owner != dialogHandle)
                throw new AutomationException("CUSTOMER_SAVE_TARGET_INVALID", "Nút Cất tìm được không thuộc hộp Thêm Khách hàng; không bấm.");
            var bounds = target.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new AutomationException("BOUNDS_UNREADABLE", "Nút Cất của hộp Thêm Khách hàng không có vị trí hiển thị.");
            token.ThrowIfCancellationRequested();
            try
            {
                MarkWorkflowInput();
                NativeKeyboard.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                    identity!.WindowHandle, identity.ProcessId, WorkflowTargetHandle(target));
                epoch++;
                // The dialog closes on success; later reads rebind the voucher editor.
                root = automation.FromHandle(identity.WindowHandle);
                newCustomerHandle = nint.Zero;
                return dialogHandle;
            }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", "Đã có thể bấm Cất khách hàng; cần kiểm tra lại.", true, error); }
        }, token, maySendInput: true);

    private nint RequireNewCustomerScope()
    {
        var handle = (nint)root!.Properties.NativeWindowHandle.ValueOrDefault;
        if (handle == nint.Zero || handle != newCustomerHandle)
            throw new AutomationException("NEW_CUSTOMER_SCOPE_CHANGED", "Hộp Thêm Khách hàng đã thay đổi; không thao tác tiếp.", true);
        return handle;
    }

    private static bool IsNewCustomerDialog(AutomationElement element, string title)
        => string.Equals((element.Properties.Name.ValueOrDefault ?? "").Trim(), title, StringComparison.OrdinalIgnoreCase);
}
