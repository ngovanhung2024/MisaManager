using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed partial class NativeInputService
{
    public Task OpenNewItemAsync(NativeFrame frame, NativeCell cell, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var target = ResolveCell(cell, requireEmptyCode: true, preserveComboBox: true);
            RealizeAndScroll(target);
            target = ResolveCell(cell, requireEmptyCode: true, preserveComboBox: true);
            FocusTarget(target, activateGridCell: true, token: token, verifyFocus: false);
            RecordInput();
            MarkWorkflowInput();
            NativeKeyboard.PressFocused(identity!.WindowHandle, identity.ProcessId, "F9", token);
            epoch++;
            return true;
        }, token, maySendInput: true);

    public Task<NativeFrame> BindNewItemAsync(CancellationToken token)
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
                throw new AutomationException("NEW_ITEM_DIALOG_AMBIGUOUS", "More than one native new-item dialog is visible.");
            root = dialogs[0];
            epoch++;
            var frame = Capture();
            GuardWorkflow(frame);
            return frame;
        }, token), _ => true, "NEW_ITEM_DIALOG_MISSING", "MISA new-item dialog did not open.", token,
            timeoutMilliseconds: 10000);

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
