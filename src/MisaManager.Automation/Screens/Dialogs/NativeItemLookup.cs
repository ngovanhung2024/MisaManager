using MisaManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace MisaManager.Automation;

public sealed partial class NativeInputService
{
    public Task OpenItemLookupAsync(NativeFrame frame, NativeCell cell, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame, forInput: true);
            var target = ResolveCell(cell, preserveComboBox: true);
            RealizeAndScroll(target);
            target = ResolveCell(cell, preserveComboBox: true);
            FocusTarget(target, activateGridCell: true, token: token);
            var handle = KeyboardHandle(target);
            if (!NativeKeyboard.HasTargetFocus(handle))
                throw new AutomationException("FOCUS_FAILED", "Ô Mã hàng chưa nhận focus; không mở Tìm nhanh.");
            RecordInput();
            MarkWorkflowInput();
            NativeKeyboard.Press(handle, identity!.WindowHandle, identity.ProcessId, "F3", token);
            epoch++;
            return true;
        }, token, maySendInput: true);

    public Task<NativeFrame> BindItemLookupAsync(ScreenProfile screen, CancellationToken token)
        => NativeWait.UntilAsync(() => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            Capture();
            var grid = screen.Grid("items").AutomationId;
            var dialogs = HostWindows(identity!.ProcessId)
                .Where(window => window.NativeWindowHandle != identity.WindowHandle)
                .Select(window => window.Element)
                .Where(window => OwnedBy((nint)window.Properties.NativeWindowHandle.ValueOrDefault, identity.WindowHandle))
                .Where(window => window.FindAllDescendants(automation.ConditionFactory.ByAutomationId(grid))
                    .Count(element => !element.Properties.IsOffscreen.ValueOrDefault) == 1)
                .Where(window => window.Properties.IsEnabled.ValueOrDefault && !window.Properties.IsOffscreen.ValueOrDefault
                    && PInvoke.IsWindowVisible((HWND)window.Properties.NativeWindowHandle.ValueOrDefault)
                    && PInvoke.IsWindowEnabled((HWND)window.Properties.NativeWindowHandle.ValueOrDefault))
                .ToArray();
            if (dialogs.Length == 0) throw new AutomationException("TARGET_NOT_FOUND", "Danh mục MISA chưa mở.");
            if (dialogs.Length != 1) throw new AutomationException("ITEM_LOOKUP_AMBIGUOUS", "Đang mở nhiều danh mục MISA.");
            root = dialogs[0];
            epoch++;
            _ = Resolve(screen.Element("search").Selector);
            return Capture();
        }, token), _ => true, "ITEM_LOOKUP_MISSING", "Không mở được danh mục vật tư MISA bằng F3.", token);

    public async Task CloseItemLookupAsync(NativeFrame frame, NativeSelector search, CancellationToken token)
    {
        var handle = await dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame, forInput: true);
            var dialog = (nint)root!.Properties.NativeWindowHandle.ValueOrDefault;
            if (dialog == nint.Zero || dialog == identity!.WindowHandle)
                throw new AutomationException("ITEM_LOOKUP_SCOPE_CHANGED", "Danh mục MISA đã đổi cửa sổ.");
            var target = Resolve(search);
            FocusTarget(target, token: token);
            RecordInput();
            MarkWorkflowInput();
            NativeKeyboard.Press(KeyboardHandle(target), identity.WindowHandle, identity.ProcessId, "Escape", token);
            epoch++;
            root = automation.FromHandle(identity.WindowHandle);
            return dialog;
        }, token, maySendInput: true);
        await NativeWait.UntilAsync(() => dispatcher.RunAsync(() =>
            !PInvoke.IsWindow((HWND)handle) || !PInvoke.IsWindowVisible((HWND)handle), token),
            closed => closed, "ITEM_LOOKUP_NOT_CLOSED", "Danh mục MISA chưa đóng; không nhập tiếp.", token);
    }
}
