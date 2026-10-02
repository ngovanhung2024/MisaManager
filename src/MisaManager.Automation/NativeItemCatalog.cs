using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed partial class NativeInputService
{
    private sealed record CatalogOrigin(AutomationElement ParentScope, NativeCell Cell, string[] Codes);
    private CatalogOrigin? catalogOrigin;

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
            var dialogs = automation.FromHandle(identity!.WindowHandle)
                .FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Window))
                .Where(element => element.Properties.ProcessId.ValueOrDefault == identity.ProcessId
                    && element.Properties.IsEnabled.ValueOrDefault && !element.Properties.IsOffscreen.ValueOrDefault
                    && element.Properties.NativeWindowHandle.ValueOrDefault != identity.WindowHandle
                    && IsNewItemDialog(element))
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

    public async Task OpenItemCatalogAsync(NativeFrame frame, NativeCell cell, CancellationToken token)
    {
        var before = await ReadTableAsync(frame, cell.GridId, [cell.CodeColumn], token);
        var origin = await dispatcher.RunAsync(() => new CatalogOrigin(root!, cell,
            before.Rows.Select(row => row[cell.CodeColumn].Trim()).ToArray()), token);
        await dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(before.Frame, forInput: true);
            var target = ResolveCell(cell, requireEmptyCode: true, preserveComboBox: true);
            RealizeAndScroll(target);
            target = ResolveCell(cell, requireEmptyCode: true, preserveComboBox: true);
            // R20's virtual grid can leave UIA focus unset after the native
            // cell click even though the foreground window is valid. Send F3
            // through the guarded foreground path instead of replaying the click.
            FocusTarget(target, activateGridCell: true, token: token, verifyFocus: false);
            RecordInput();
            catalogOrigin = origin;
            MarkWorkflowInput();
            NativeKeyboard.PressFocused(identity!.WindowHandle, identity.ProcessId, "F3", token);
            epoch++;
            return true;
        }, token, maySendInput: true);
    }

    public Task<NativeFrame> BindItemCatalogAsync(CancellationToken token)
        => NativeWait.UntilAsync(() => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            Capture();
            var dialogs = automation.FromHandle(identity!.WindowHandle)
                .FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Window))
                .Where(element => element.Properties.ProcessId.ValueOrDefault == identity.ProcessId
                    && element.Properties.IsEnabled.ValueOrDefault && !element.Properties.IsOffscreen.ValueOrDefault
                    && element.Properties.NativeWindowHandle.ValueOrDefault != identity.WindowHandle
                    && IsCatalogDialog(element))
                .Where(element => element.FindAllDescendants(automation.ConditionFactory.ByAutomationId("4"))
                    .Any(editor => editor.ControlType == ControlType.ComboBox && editor.IsEnabled
                        && !editor.Properties.IsOffscreen.ValueOrDefault)).ToArray();
            if (dialogs.Length == 0)
                throw new AutomationException("TARGET_NOT_FOUND", "MISA item search dialog is not ready.");
            if (dialogs.Length != 1)
                throw new AutomationException("CATALOG_DIALOG_AMBIGUOUS", "More than one native item search dialog is visible.");
            root = dialogs[0];
            epoch++;
            var frame = Capture();
            GuardWorkflow(frame);
            return frame;
        }, token), _ => true, "CATALOG_DIALOG_MISSING", "MISA item search did not open.", token,
            timeoutMilliseconds: 10000);

    public async Task CloseItemCatalogAsync(NativeFrame frame, CancellationToken token)
    {
        await dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame, forInput: true);
            if (!IsCatalogDialog(root!))
                throw new AutomationException("CATALOG_DIALOG_MISSING", "The bound scope is no longer the item search dialog.");
            CancelBoundCatalog();
            return true;
        }, token, maySendInput: true);
        await VerifyCatalogCancelledAsync(token);
    }

    public async Task<bool> TryCloseItemCatalogAsync(CancellationToken token)
    {
        var closed = await dispatcher.RunAsync(() =>
        {
            if (identity is null) return false;
            RecordQuery(false);
            var dialogs = automation.FromHandle(identity.WindowHandle)
                .FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Window))
                .Where(element => element.Properties.ProcessId.ValueOrDefault == identity.ProcessId
                    && element.IsEnabled && !element.Properties.IsOffscreen.ValueOrDefault
                    && element.Properties.NativeWindowHandle.ValueOrDefault != nint.Zero
                    && element.Properties.NativeWindowHandle.ValueOrDefault != identity.WindowHandle
                    // A few R20 provider stubs do not expose AutomationId on
                    // the dialog itself. The title plus the stable grid/editor
                    // contract is sufficient for this recovery path.
                    && IsCatalogDialog(element))
                .Where(element => element.FindAllDescendants(automation.ConditionFactory.ByAutomationId("grdData")).Length == 1
                    && element.FindAllDescendants(automation.ConditionFactory.ByAutomationId("4"))
                        .Any(editor => editor.ControlType == ControlType.ComboBox && editor.IsEnabled
                            && !editor.Properties.IsOffscreen.ValueOrDefault)).ToArray();
            if (dialogs.Length == 0) return false;
            if (dialogs.Length != 1)
                throw new AutomationException("CATALOG_DIALOG_AMBIGUOUS", "More than one native item search dialog is visible.");
            root = dialogs[0];
            epoch++;
            var frame = Capture();
            GuardWorkflow(frame, forInput: true);
            CancelBoundCatalog();
            return true;
        }, token, maySendInput: true);
        if (closed) await VerifyCatalogCancelledAsync(token);
        return closed;
    }

    private void CancelBoundCatalog()
    {
        var cancel = Resolve(new("btnCancel", ControlType: "Button"));
        RecordInput();
        MarkWorkflowInput();
        if (cancel.Patterns.Invoke.IsSupported)
            cancel.Patterns.Invoke.Pattern.Invoke();
        else
        {
            var bounds = cancel.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new AutomationException("BOUNDS_UNREADABLE", "Catalog Cancel has no visible native bounds.");
            NativeKeyboard.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                identity!.WindowHandle, identity.ProcessId);
        }
        epoch++;
        root = automation.FromHandle(identity!.WindowHandle);
    }

    private async Task VerifyCatalogCancelledAsync(CancellationToken token)
    {
        await NativeWait.UntilAsync(() => dispatcher.RunAsync(() =>
            !automation.FromHandle(identity!.WindowHandle)
                .FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Window))
                .Any(dialog => dialog.Properties.ProcessId.ValueOrDefault == identity.ProcessId
                    && dialog.IsEnabled && !dialog.Properties.IsOffscreen.ValueOrDefault
                    && IsCatalogDialog(dialog)), token), closed => closed,
            "CATALOG_STILL_OPEN", "Catalog Cancel did not close the dialog; input is not replayed.", token,
            timeoutMilliseconds: 15000);
        var origin = catalogOrigin;
        if (origin is null) return; // Recovery after an app restart has no pre-open snapshot.
        var parent = await NativeWait.UntilAsync(() => dispatcher.RunAsync(() =>
        {
            root = origin.ParentScope;
            if (!root.Properties.IsEnabled.ValueOrDefault || root.Properties.IsOffscreen.ValueOrDefault)
                throw new AutomationException("EDITOR_NOT_FOUND", "The catalog's parent editor did not become ready.");
            epoch++;
            var frame = Capture();
            GuardWorkflow(frame);
            return frame;
        }, token), frame => frame is not null, "EDITOR_NOT_FOUND",
            "The catalog's parent editor did not become ready.", token, timeoutMilliseconds: 60000);
        var table = await ReadTableAsync(parent, origin.Cell.GridId, [origin.Cell.CodeColumn], token);
        if (!origin.Codes.SequenceEqual(table.Rows.Select(row => row[origin.Cell.CodeColumn].Trim())))
            throw new AutomationException("CATALOG_CANCEL_CHANGED_ROWS",
                "Catalog Cancel changed the parent item codes; refusing to open a new item or overwrite rows.", true);
        catalogOrigin = null;
    }

    public Task SelectItemCatalogRowAsync(NativeFrame frame, int row, string code, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame, forInput: true);
            var cell = ResolveCell(new NativeCell("grdData", row, "Mã hàng", "Mã hàng", code));
            var dataRow = cell;
            while (dataRow is not null && dataRow.ControlType != ControlType.DataItem) dataRow = dataRow.Parent;
            if (dataRow is null)
                throw new AutomationException("CATALOG_SELECTION_AMBIGUOUS", "MISA catalog row has no native DataItem identity.");
            var selected = dataRow.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.CheckBox))
                .Where(item => item.Properties.IsEnabled.ValueOrDefault && !item.Properties.IsOffscreen.ValueOrDefault
                    && item.Patterns.Toggle.IsSupported && item.BoundingRectangle.Width > 0
                    && item.BoundingRectangle.Height > 0).ToArray();
            if (selected.Length == 1)
            {
                var checkbox = selected[0].Patterns.Toggle.Pattern;
                if (checkbox.ToggleState.Value != ToggleState.On)
                {
                    MarkWorkflowInput();
                    checkbox.Toggle();
                }
                if (checkbox.ToggleState.Value != ToggleState.On)
                    throw new AutomationException("CATALOG_SELECTION_FAILED", "The requested catalog row is not checked.", true);
            }
            else if (dataRow.Patterns.SelectionItem.IsSupported)
            {
                MarkWorkflowInput();
                dataRow.Patterns.SelectionItem.Pattern.Select();
            }
            else throw new AutomationException("CATALOG_SELECTION_AMBIGUOUS", "MISA catalog row has no native selection pattern.");
            epoch++;
            return Capture();
        }, token, maySendInput: true);
}
