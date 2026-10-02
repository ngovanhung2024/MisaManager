using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed record NativeSelector(string AutomationId = "", string Name = "", string ControlType = "");
public sealed record NativeCell(string GridId, int Row, string Column, string CodeColumn, string ExpectedCode);
public sealed record NativeCellWrite(string Column, string Text);
public sealed record NativeTable(IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, NativeFrame Frame,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Sources);
public sealed record NativeFields(IReadOnlyDictionary<string, Readback> Values, NativeFrame Frame);

public sealed partial class NativeInputService
{
    private long workflowInputSequence;
    public long WorkflowInputSequence => Interlocked.Read(ref workflowInputSequence);
    private void MarkWorkflowInput() => Interlocked.Increment(ref workflowInputSequence);

    public Task<bool> WorkflowScopeExistsAsync(WindowIdentity window, string scopeId, CancellationToken token)
        => dispatcher.RunAsync(() => WorkflowScopes(window, scopeId).Length != 0, token);

    public Task<NativeFrame?> TryBindWorkflowScopeAsync(WindowIdentity window, string scopeId, string? expectedTitle,
        CancellationToken token, bool waitForEnabled = false)
        => dispatcher.RunAsync<NativeFrame?>(() =>
        {
            var scopes = WorkflowScopes(window, scopeId);
            if (scopes.Length == 0) return null;
            if (scopes.Length != 1) throw new AutomationException("SCOPE_AMBIGUOUS", "Native scope không duy nhất: " + scopeId);
            var scope = scopes[0];
            if (expectedTitle is not null && scope.Name != expectedTitle)
                throw new AutomationException("EDITOR_IDENTITY_INVALID", "Editor không khớp công ty/loại chứng từ đã chọn.");
            if (!scope.IsEnabled)
            {
                if (waitForEnabled) return null;
                throw new AutomationException("SCOPE_DISABLED", "MISA scope đang bị khóa hoặc có hộp thoại cần xử lý.");
            }
            root = scope; identity = window; epoch++;
            var frame = Capture();
            GuardWorkflow(frame);
            return frame;
        }, token);

    private AutomationElement[] WorkflowScopes(WindowIdentity window, string scopeId)
    {
        RecordQuery(false);
        var current = new MisaWindowDiscovery().Bind(window.WindowHandle, window.ProcessId);
        if (current.ProcessStartUtc != window.ProcessStartUtc || current.ExecutablePath != window.ExecutablePath
            || current.Title != window.Title)
            throw new AutomationException("WINDOW_IDENTITY_CHANGED", "Phiên MISA thay đổi trong khi bind scope.");
        return automation.FromHandle(window.WindowHandle).FindAllDescendants(automation.ConditionFactory.ByAutomationId(scopeId))
            .Where(element => element.Properties.ProcessId.ValueOrDefault == window.ProcessId
                && !element.Properties.IsOffscreen.ValueOrDefault).ToArray();
    }

    public Task<NativeTable> ReadTableAsync(NativeFrame frame, string gridId, IReadOnlyList<string> columns, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            GuardWorkflow(frame);
            var grid = ResolveGrid(gridId);
            if (!grid.Patterns.Grid.IsSupported || !grid.Patterns.Table.IsSupported)
                return ReadCustomTable(grid, columns, token);
            var pattern = grid.Patterns.Grid.Pattern;
            var count = pattern.RowCount.Value;
            if (count > 5000) throw new AutomationException("GRID_LIMIT", "Bảng vượt 5000 dòng; cần lọc đúng phạm vi.");
            var headers = grid.Patterns.Table.Pattern.ColumnHeaders.Value;
            if (headers.Length != pattern.ColumnCount.Value) throw new AutomationException("GRID_COLUMNS_INVALID", "Thiếu tiêu đề native của bảng.");
            var headerNames = headers.Select(header => header.Name).ToArray();
            var indices = columns.ToDictionary(name => name, name =>
            {
                var matches = headerNames.Select((header, index) => (header, index)).Where(item => item.header == name).ToArray();
                if (matches.Length != 1) throw new AutomationException("GRID_COLUMN_AMBIGUOUS", "Không xác định duy nhất cột " + name);
                return matches[0].index;
            });
            var rows = new List<IReadOnlyDictionary<string, string>>();
            var sources = new List<IReadOnlyDictionary<string, string>>();
            for (var row = 0; row < count; row++)
            {
                token.ThrowIfCancellationRequested();
                var values = new Dictionary<string, string>();
                var rowSources = new Dictionary<string, string>();
                foreach (var column in indices)
                {
                    token.ThrowIfCancellationRequested();
                    var cell = pattern.GetItem(row, column.Value);
                    if (!cell.Patterns.GridItem.IsSupported || cell.Patterns.GridItem.Pattern.Row.Value != row
                        || cell.Patterns.GridItem.Pattern.Column.Value != column.Value
                        || cell.Properties.ProcessId.ValueOrDefault != identity!.ProcessId)
                        throw new AutomationException("GRID_IDENTITY_INVALID", "Native cell đổi vị trí trong khi đọc.");
                    // Realize virtualized cells before reading; scrolling is observational.
                    cell.Patterns.VirtualizedItem.PatternOrDefault?.Realize();
                    cell = pattern.GetItem(row, column.Value);
                    if (cell.Properties.IsOffscreen.ValueOrDefault && cell.Patterns.ScrollItem.IsSupported)
                    {
                        cell.Patterns.ScrollItem.Pattern.ScrollIntoView();
                        cell = pattern.GetItem(row, column.Value);
                    }
                    if (!cell.Patterns.GridItem.IsSupported || cell.Patterns.GridItem.Pattern.Row.Value != row
                        || cell.Patterns.GridItem.Pattern.Column.Value != column.Value
                        || cell.Properties.ProcessId.ValueOrDefault != identity!.ProcessId)
                        throw new AutomationException("GRID_IDENTITY_INVALID", "Cell identity changed after realization.");
                    var text = ReadCustomCellText(cell, column.Key);
                    values.Add(column.Key, text.Value);
                    rowSources.Add(column.Key, text.Source + "+GridItem");
                }
                rows.Add(values);
                sources.Add(rowSources);
                GuardWorkflow(frame);
                frame = Capture();
            }
            if (pattern.RowCount.Value != count || pattern.ColumnCount.Value != headers.Length
                || !grid.Patterns.Table.Pattern.ColumnHeaders.Value.Select(header => header.Name)
                    .SequenceEqual(headerNames))
                throw new AutomationException("GRID_CHANGED", "Bảng thay đổi trong khi đọc.");
            GuardWorkflow(frame);
            return new NativeTable(rows, Capture(), sources);
        }, token);

    public Task<NativeFields> ReadFieldsAsync(NativeFrame frame, IReadOnlyDictionary<string, NativeSelector> fields, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            GuardWorkflow(frame);
            var values = new Dictionary<string, (string Value, string Source)>();
            foreach (var field in fields)
            {
                token.ThrowIfCancellationRequested();
                values.Add(field.Key, ReadNativeText(Resolve(field.Value)));
            }
            GuardWorkflow(frame);
            var fresh = Capture();
            return new NativeFields(values.ToDictionary(item => item.Key,
                item => new Readback("supported", item.Value.Value, item.Value.Source, fresh.ObservedAt, null, fresh)), fresh);
        }, token);

    public Task<Readback> ReadFieldAsync(NativeFrame frame, NativeSelector selector, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            GuardWorkflow(frame);
            var element = Resolve(selector);
            var (value, source) = ReadNativeText(element);
            return new Readback("supported", value, source, DateTimeOffset.UtcNow, null, Capture());
        }, token);

    public async Task<Readback> WriteFieldAsync(NativeFrame frame, NativeSelector selector, string text,
        string expected, NativeValueKind kind, string cultureName, string? commit, CancellationToken token,
        bool selectedCustomer = false, bool allowUnverifiedFocus = false, bool verifyAfterWrite = true)
    {
        var sentFrame = await dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var target = Resolve(selector);
            var sent = false;
            try
            {
                SetNativeValue(target, text, token, ref sent, allowUnverifiedFocus);
                if (commit is not null) Commit(target, commit, token, allowUnverifiedFocus);
                epoch++;
                return Capture();
            }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", error.Message, sent, error); }
        }, token, maySendInput: true);
        // Callers that update a field group (dates, invoice metadata, or a
        // catalog filter) can defer verification to one authoritative read.
        if (!verifyAfterWrite)
            return new Readback("sent_unverified", text, "UIA3.ValuePattern", sentFrame.ObservedAt,
                "Đã gửi; sẽ xác minh trong lần đọc nhóm kế tiếp.", sentFrame);
        return await VerifyWrittenAsync(() => ReadFieldAsync(sentFrame, selector, token), expected, kind, cultureName, token,
            selectedCustomer ? actual => Guid.TryParse(actual.Value, out _)
                || NativeComparison.Matches(expected, actual.Value, kind, CultureInfo.GetCultureInfo(cultureName)) : null);
    }

    public async Task<Readback> SelectTabAsync(NativeFrame frame, string caption, CancellationToken token)
    {
        var selector = new NativeSelector(Name: caption, ControlType: "TabItem");
        var selection = await dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame, forInput: true);
            RecordQuery(false);
            var tab = Resolve(selector);
            if (!tab.Patterns.SelectionItem.IsSupported)
                throw new AutomationException("PATTERN_UNSUPPORTED", "Tab không có SelectionItemPattern.");
            var sent = !tab.Patterns.SelectionItem.Pattern.IsSelected.Value;
            if (sent)
            {
                token.ThrowIfCancellationRequested();
                RecordTab();
                RecordInput();
                try { MarkWorkflowInput(); tab.Patterns.SelectionItem.Pattern.Select(); epoch++; }
                catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được thao tác chuyển tab.", true, error); }
            }
            return (Frame: Capture(), Sent: sent);
        }, token, maySendInput: true);
        // IsSelected was read under the current scope guard. An already selected
        // tab needs no second UIA traversal or settling poll.
        if (!selection.Sent)
            return new Readback("supported", caption, "UIA3.SelectionItem", selection.Frame.ObservedAt, null, selection.Frame);
        try
        {
            return await NativeWait.UntilAsync(() => dispatcher.RunAsync(() =>
            {
                RecordQuery();
                GuardWorkflow(selection.Frame);
                var selected = Resolve(selector).Patterns.SelectionItem.Pattern.IsSelected.Value;
                return new Readback(selected ? "supported" : "pending", caption, "UIA3.SelectionItem", DateTimeOffset.UtcNow, null, Capture());
            }, token), value => value.Status == "supported", "READBACK_MISMATCH", "Tab chưa được chọn: " + caption, token);
        }
        catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", error.Message, selection.Sent, error); }
    }

    public Task<Readback> InvokeWorkflowAsync(NativeFrame frame, NativeSelector selector, CancellationToken token,
        bool leavesScope = false, bool discardChanges = false)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            if (discardChanges && !leavesScope)
                throw new ArgumentException("Discard must close the bound editor scope.", nameof(discardChanges));
            var editorHandle = discardChanges ? (nint)root!.Properties.NativeWindowHandle.ValueOrDefault : nint.Zero;
            if (discardChanges && editorHandle == nint.Zero)
                throw new AutomationException("DISCARD_OWNER_MISSING", "Cannot bind the discard prompt to the voucher editor.");
            var target = Resolve(selector);
            var targetHandle = WorkflowTargetHandle(target);
            if (!target.Patterns.Invoke.IsSupported)
                throw new AutomationException("PATTERN_UNSUPPORTED", "Control không có InvokePattern.");
            token.ThrowIfCancellationRequested();
            try
            {
                MarkWorkflowInput();
                var bounds = target.BoundingRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    throw new AutomationException("BOUNDS_UNREADABLE", "Control không có bounds để click native.");
            // Modal catalog buttons are owned by MISA's host but their child
            // HWND can steal activation from the dialog. Use the host root for
            // the OK command, matching the direct native-invoke path.
            var clickHandle = selector.AutomationId == "btnOK" ? nint.Zero : targetHandle;
            NativeKeyboard.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                identity!.WindowHandle, identity.ProcessId, clickHandle);
                epoch++;
                // Closing a voucher/dialog invalidates its UIA root before readback can run.
                if (leavesScope)
                {
                    if (discardChanges) DismissDirtyVoucherPrompt(editorHandle, token, waitMilliseconds: 1200);
                    root = automation.FromHandle(identity!.WindowHandle);
                }
                return new Readback("sent_unverified", null, "UIA3.Invoke", DateTimeOffset.UtcNow, null, Capture());
            }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", "Invocation cần được đọc lại.", true, error); }
        }, token, maySendInput: true);

    private nint WorkflowTargetHandle(AutomationElement target)
    {
        // Toolbar providers have no HWND; their native parent belongs to the
        // actual command window, which may be an owned top-level voucher.
        for (var current = target; current is not null; current = current.Parent)
        {
            if (current.Properties.ProcessId.ValueOrDefault != identity!.ProcessId) break;
            var handle = (nint)current.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle != nint.Zero) return handle;
            if (current.Equals(root)) break;
        }
        throw new AutomationException("COMMAND_WINDOW_MISSING", "Cannot bind the native window containing the workflow command.");
    }

    public Task<bool> TryPreserveSourceTaxAsync(WindowIdentity window, string editorId, string? editorTitle,
        NativeSelector voucherField, string expectedVoucher, decimal expectedTax, int lineCount, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            var editors = WorkflowScopes(window, editorId);
            if (editors.Length == 0) return false;
            if (editors.Length != 1 || editorTitle is not null && editors[0].Name != editorTitle)
                throw new AutomationException("EDITOR_IDENTITY_INVALID", "Cannot bind the invoice editor for tax prompt resolution.");
            var editor = editors[0];
            var dialogs = editor.FindAllDescendants(automation.ConditionFactory.ByAutomationId("frmShowMessageLinkCustom"))
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
            var vouchers = editor.FindAllDescendants(automation.ConditionFactory.ByAutomationId(voucherField.AutomationId));
            if (vouchers.Length != 1 || ReadNativeText(vouchers[0]).Value != expectedVoucher)
                throw new AutomationException("VOUCHER_IDENTITY_CHANGED", "Tax prompt voucher identity differs from the run.");
            var messages = dialog.FindAllDescendants(automation.ConditionFactory.ByAutomationId("lblMessage"));
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
            var buttons = dialog.FindAllDescendants(automation.ConditionFactory.ByAutomationId("btnNo"))
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

    public Task RequireDisabledCommandAsync(NativeFrame frame, NativeSelector selector, CancellationToken token)
        => RequireCommandEnabledAsync(frame, selector, false, token);

    public Task RequireCommandEnabledAsync(NativeFrame frame, NativeSelector selector, bool enabled, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame);
            var condition = selector.AutomationId.Length > 0
                ? automation.ConditionFactory.ByAutomationId(selector.AutomationId)
                : automation.ConditionFactory.ByName(selector.Name);
            var commands = root!.FindAllDescendants(condition)
                .Where(command => command.Properties.ProcessId.ValueOrDefault == identity!.ProcessId
                    && command.ControlType == ControlType.Button
                    && !command.Properties.IsOffscreen.ValueOrDefault).ToArray();
            if (commands.Length != 1 || commands[0].Properties.IsEnabled.ValueOrDefault != enabled)
                throw new AutomationException("COMMAND_STATE_MISMATCH", $"Expected one native command with enabled={enabled}; refusing to close the voucher.");
            return true;
        }, token);

    public Task<Readback> ReadCellAsync(NativeFrame frame, NativeCell cell, CancellationToken token, bool waitForCode = false)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            GuardWorkflow(frame);
            var target = ResolveCell(cell, waitForCode, preserveComboBox: true);
            // Use the same native display semantics as full-table reconciliation.
            // Unit ValuePattern exposes the internal GUID, not its displayed label.
            var (value, source) = ReadCustomCellText(target, cell.Column);
            return new Readback("supported", value, source + "+GridItem", DateTimeOffset.UtcNow, null, Capture());
        }, token);
    public async Task<Readback> WriteCellAsync(NativeFrame frame, NativeCell cell, string text, string expected,
        NativeValueKind kind, string cultureName, CancellationToken token)
    {
        var sentFrame = await SendCellAsync(frame, cell, text, token);
        var reread = cell.Column == cell.CodeColumn ? cell with { ExpectedCode = expected } : cell;
        return await VerifyWrittenAsync(() => ReadCellAsync(sentFrame, reread, token, waitForCode: cell.Column == cell.CodeColumn), expected, kind, cultureName, token);
    }

    // Commit each value through MISA, then verify the whole row before publishing progress.
    // Controls are resolved again after each commit because R20 recreates its editors.
    public async Task<NativeFields> WriteRowAsync(NativeFrame frame, NativeCell row,
        IReadOnlyList<NativeCellWrite> writes, IReadOnlyList<string> readColumns,
        Func<NativeFields, bool> verified, CancellationToken token, Action<string>? enteringColumn = null)
    {
        if (string.IsNullOrWhiteSpace(row.ExpectedCode) || writes.Count is < 1 or > 3
            || writes.Select(write => write.Column).Distinct(StringComparer.Ordinal).Count() != writes.Count
            || writes.Any(write => write.Column == row.CodeColumn || !readColumns.Contains(write.Column)
                || write.Text.Length > 32768 || write.Text.Any(char.IsControl)))
            throw new ArgumentException("A row batch requires a bound item code and 1-3 distinct readable value columns.");
        var sequence = WorkflowInputSequence;
        try
        {
            frame = await SendRowAsync(frame, row, writes, token, enteringColumn);
            return await NativeWait.UntilAsync(() => ReadRowAsync(frame, row, readColumns, token), verified,
                "READBACK_MISMATCH", $"Row {row.Row + 1} ({row.ExpectedCode}) does not match after native writes.", token);
        }
        catch (Exception error)
        {
            var sent = WorkflowInputSequence != sequence || error is AutomationException { InputMayHaveBeenSent: true };
            throw new AutomationException("ROW_INPUT_UNCERTAIN", $"Row {row.Row + 1} ({row.ExpectedCode}): {error.Message}", sent, error);
        }
    }

    public Task<NativeFields> ReadRowAsync(NativeFrame frame, NativeCell row,
        IReadOnlyList<string> columns, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            GuardWorkflow(frame);
            var cells = ResolveRowCells(row, columns.Append(row.CodeColumn).Distinct(StringComparer.Ordinal).ToArray(), token);
            var before = ReadCustomCellText(cells[row.CodeColumn], row.CodeColumn).Value;
            var values = new Dictionary<string, (string Value, string Source)>();
            foreach (var column in cells)
            {
                token.ThrowIfCancellationRequested();
                values.Add(column.Key, ReadCustomCellText(column.Value, column.Key));
            }
            if (ReadCustomCellText(cells[row.CodeColumn], row.CodeColumn).Value != before)
                throw new AutomationException("GRID_ROW_CHANGED", "Item identity changed during row readback.");
            GuardWorkflow(frame);
            var fresh = Capture();
            return new NativeFields(values.ToDictionary(item => item.Key,
                item => new Readback("supported", item.Value.Value, item.Value.Source + "+BoundRow",
                    fresh.ObservedAt, null, fresh)), fresh);
        }, token);

    // Keep a row batch on the native owner thread. R20 can recreate an editor
    // after Tab, so each next column is resolved again without another queue hop.
    private Task<NativeFrame> SendRowAsync(NativeFrame frame, NativeCell row,
        IReadOnlyList<NativeCellWrite> writes, CancellationToken token, Action<string>? enteringColumn)
        => dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame, forInput: true);
            var current = frame;
            foreach (var write in writes)
            {
                token.ThrowIfCancellationRequested();
                enteringColumn?.Invoke(write.Column);
                RecordInput();
                var cell = row with { Column = write.Column };
                var target = ResolveCell(cell);
                RealizeAndScroll(target);
                target = ResolveCell(cell);
                if (target.Properties.IsOffscreen.ValueOrDefault)
                    throw new AutomationException("GRID_OFFSCREEN", "Row cell remains offscreen after native realization.");
                var sent = false;
                try
                {
                    SetNativeValue(target, write.Text, token, ref sent);
                    Commit(target, "Tab", token);
                    epoch++;
                    current = Capture();
                }
                catch (Exception error)
                {
                    throw new AutomationException("ROW_INPUT_UNCERTAIN", error.Message, sent, error);
                }
            }
            return current;
        }, token, maySendInput: true);

    private Task<NativeFrame> SendCellAsync(NativeFrame frame, NativeCell cell, string text, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var requireEmptyCode = cell.Column == cell.CodeColumn && string.IsNullOrWhiteSpace(cell.ExpectedCode);
            var target = ResolveCell(cell, requireEmptyCode: requireEmptyCode);
            RealizeAndScroll(target);
            target = ResolveCell(cell, requireEmptyCode: requireEmptyCode);
            if (target.Properties.IsOffscreen.ValueOrDefault)
            {
                if (!target.Patterns.ScrollItem.IsSupported)
                    throw new AutomationException("GRID_OFFSCREEN", "Không thể đưa ô vào vùng nhập bằng native pattern.");
                token.ThrowIfCancellationRequested();
                MarkWorkflowInput();
                target.Patterns.ScrollItem.Pattern.ScrollIntoView();
                epoch++;
                RealizeAndScroll(target);
                target = ResolveCell(cell, requireEmptyCode: requireEmptyCode);
            }
            if (target.Properties.IsOffscreen.ValueOrDefault)
                throw new AutomationException("GRID_OFFSCREEN", "Ô vẫn nằm ngoài vùng nhập.");
            var sent = false;
            try
            {
                try
                {
                    SetNativeValue(target, text, token, ref sent);
                }
                catch (ObjectDisposedException) when (!sent)
                {
                    // Infragistics recreates the cell after catalog/unit
                    // updates. Re-resolve the cell once before declaring the
                    // write uncertain.
                    target = ResolveCell(cell, requireEmptyCode: requireEmptyCode);
                    SetNativeValue(target, text, token, ref sent);
                }
                Commit(target, "Tab", token);
                epoch++;
                return Capture();
            }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", error.Message, sent, error); }
        }, token, maySendInput: true);

    public async Task<Readback> SelectCellOptionAsync(NativeFrame frame, NativeCell cell, string expected,
        string cultureName, CancellationToken token, bool allowMissing = false)
    {
        string? absentOptions = null;
        var sentFrame = await dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var target = ResolveCell(cell, preserveComboBox: true);
            if (target.ControlType != ControlType.ComboBox)
                throw new AutomationException("GRID_OPTION_UNSUPPORTED", "Ô không phải ComboBox cho thao tác chọn danh sách.");
            var editor = target.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Edit))
                .Where(item => item.Properties.IsEnabled.ValueOrDefault && !item.Properties.IsOffscreen.ValueOrDefault
                    && item.BoundingRectangle.Width > 0 && item.BoundingRectangle.Height > 0)
                .OrderByDescending(item => item.BoundingRectangle.Width)
                .FirstOrDefault();
            // Infragistics keeps the embedded dropdown button at 0x0 until the
            // ComboBox itself is activated. Click the visible ComboBox bounds
            // first, then resolve the live editor again before locating its arrow.
            var cellBounds = target.BoundingRectangle;
            if (cellBounds.Width <= 0 || cellBounds.Height <= 0 || target.Properties.IsOffscreen.ValueOrDefault)
                throw new AutomationException("GRID_OFFSCREEN", "Grid cell has no visible native bounds.");
            MarkWorkflowInput();
            NativeKeyboard.Click(cellBounds.X + cellBounds.Width / 2, cellBounds.Y + cellBounds.Height / 2,
                identity!.WindowHandle, identity.ProcessId);
            Thread.Sleep(50);
            target = ResolveCell(cell, preserveComboBox: true);
            editor = target.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Edit))
                .Where(item => item.Properties.IsEnabled.ValueOrDefault && !item.Properties.IsOffscreen.ValueOrDefault
                    && item.BoundingRectangle.Width > 0 && item.BoundingRectangle.Height > 0)
                .OrderByDescending(item => item.BoundingRectangle.Width)
                .FirstOrDefault() ?? target;
            FocusTarget(editor, activateGridCell: true, token: token);
            AutomationElement[] dropdowns = [];
            for (var attempt = 0; attempt < 12 && dropdowns.Length != 1; attempt++)
            {
                target = ResolveCell(cell, preserveComboBox: true);
                var bounds = target.BoundingRectangle;
                dropdowns = target.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Button))
                    .Where(button => button.Patterns.Invoke.IsSupported && button.Properties.IsEnabled.ValueOrDefault
                        && !button.Properties.IsOffscreen.ValueOrDefault && button.BoundingRectangle.Width > 0
                        && button.BoundingRectangle.Height > 0 && button.BoundingRectangle.Y <= bounds.Bottom
                        && button.BoundingRectangle.Bottom >= bounds.Y
                        && (button.Properties.AutomationId.ValueOrDefault == "[Editor] dropdown button"
                            || button.BoundingRectangle.Right >= bounds.Right - 8))
                    .ToArray();
                if (dropdowns.Length != 1) Thread.Sleep(50);
            }
            if (dropdowns.Length != 1)
                throw new AutomationException("GRID_DROPDOWN_MISSING", "Chưa xác định duy nhất mũi tên của ô danh sách.");
            token.ThrowIfCancellationRequested();
            try
            {
                MarkWorkflowInput();
                var arrowBounds = dropdowns[0].BoundingRectangle;
                NativeKeyboard.Click(arrowBounds.X + arrowBounds.Width / 2, arrowBounds.Y + arrowBounds.Height / 2,
                    identity!.WindowHandle, identity.ProcessId);
                epoch++;
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được mở danh sách ĐVT.", true, error);
            }
            var host = automation.FromHandle(identity!.WindowHandle);
            AutomationElement[] registered = [];
            for (var attempt = 0; attempt < 12 && registered.Length == 0; attempt++)
            {
                token.ThrowIfCancellationRequested();
                registered = host.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.ListItem))
                    .Where(item => item.Properties.ProcessId.ValueOrDefault == identity.ProcessId
                        && item.Properties.IsEnabled.ValueOrDefault && !item.Properties.IsOffscreen.ValueOrDefault
                        && item.BoundingRectangle.Width > 0 && item.BoundingRectangle.Height > 0
                        && item.BoundingRectangle.Left < cellBounds.Right && item.BoundingRectangle.Right > cellBounds.Left
                        && Math.Abs(item.BoundingRectangle.Top - cellBounds.Bottom) < 420
                        && item.Patterns.SelectionItem.IsSupported).ToArray();
                if (registered.Length == 0) Thread.Sleep(50);
            }
            var options = registered.Where(item => item.Properties.IsEnabled.ValueOrDefault
                && !item.Properties.IsOffscreen.ValueOrDefault && item.Patterns.SelectionItem.IsSupported
                && string.Equals(item.Name?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (options.Length == 0 && allowMissing)
            {
                // Absence requires a complete native list, never a failed search
                // or only the currently visible portion of a scrollable list.
                if (registered.Length == 0
                    || registered.Any(item => item.Parent?.Patterns.Scroll.IsSupported == true
                        && item.Parent.Patterns.Scroll.Pattern.VerticallyScrollable.Value)
                    || registered.Any(item => string.IsNullOrWhiteSpace(item.Name)
                        || item.Properties.IsOffscreen.ValueOrDefault || !item.Patterns.SelectionItem.IsSupported
                        || item.BoundingRectangle.Height <= 0
                        || item.BoundingRectangle.Top < cellBounds.Top - 420
                        || item.BoundingRectangle.Bottom < cellBounds.Top - 420))
                    throw new AutomationException("GRID_OPTIONS_INCOMPLETE", "Cannot prove source unit absent from the complete registered list.", true);
                absentOptions = System.Text.Json.JsonSerializer.Serialize(registered.Select(item => item.Name.Trim()).ToArray());
                token.ThrowIfCancellationRequested();
                MarkWorkflowInput();
                NativeKeyboard.PressFocused(identity.WindowHandle, identity.ProcessId, "Escape", token);
                epoch++;
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    registered = host.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.ListItem))
                        .Where(item => item.Properties.ProcessId.ValueOrDefault == identity.ProcessId
                            && item.Properties.IsEnabled.ValueOrDefault && !item.Properties.IsOffscreen.ValueOrDefault
                            && item.BoundingRectangle.Width > 0 && item.BoundingRectangle.Height > 0
                            && item.BoundingRectangle.Left < cellBounds.Right && item.BoundingRectangle.Right > cellBounds.Left
                            && Math.Abs(item.BoundingRectangle.Top - cellBounds.Bottom) < 420
                            && item.Patterns.SelectionItem.IsSupported).ToArray();
                    if (registered.Length == 0) break;
                    Thread.Sleep(50);
                }
                if (registered.Length != 0)
                    throw new AutomationException("GRID_DROPDOWN_NOT_CLOSED", "Registered unit list remained open after Escape.", true);
                return Capture();
            }
            if (options.Length != 1)
                throw new AutomationException("GRID_OPTION_AMBIGUOUS", $"Không xác định duy nhất ĐVT '{expected}'.");
            try
            {
                MarkWorkflowInput();
                var optionBounds = options[0].BoundingRectangle;
                if (optionBounds.Width <= 0 || optionBounds.Height <= 0)
                    throw new AutomationException("BOUNDS_UNREADABLE", $"ĐVT '{expected}' không có bounds native.");
                NativeKeyboard.Click(optionBounds.X + optionBounds.Width / 2,
                    optionBounds.Y + optionBounds.Height / 2, identity!.WindowHandle, identity.ProcessId);
                epoch++;
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", $"Không xác nhận được chọn ĐVT '{expected}'.", true, error);
            }
            var fresh = Capture();
            return fresh;
        }, token, maySendInput: true);
        if (absentOptions is not null)
        {
            var unchanged = await ReadCellAsync(sentFrame, cell, token);
            return unchanged with { Status = "option_absent", Source = "UIA3.CompleteRegisteredUnitList", Reason = absentOptions };
        }
        return await VerifyWrittenAsync(() => ReadCellAsync(sentFrame, cell, token), expected,
            NativeValueKind.Text, cultureName, token);
    }

    private static async Task<Readback> VerifyWrittenAsync(Func<Task<Readback>> read, string expected,
        NativeValueKind kind, string cultureName, CancellationToken token, Func<Readback, bool>? selected = null)
    {
        try
        {
            return await NativeWait.UntilAsync(read,
                actual => selected?.Invoke(actual) ?? NativeComparison.Matches(expected, actual.Value, kind, CultureInfo.GetCultureInfo(cultureName)),
                "READBACK_MISMATCH", "Native readback chưa khớp giá trị vừa nhập.", token);
        }
        catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", error.Message, true, error); }
    }

    public Task<int> GridRowCountAsync(NativeFrame frame, string gridId, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            GuardWorkflow(frame);
            var grid = ResolveGrid(gridId);
            if (!grid.Patterns.Grid.IsSupported || !grid.Patterns.Table.IsSupported)
                return ReadCustomTable(grid, Array.Empty<string>(), token).Rows.Count;
            return grid.Patterns.Grid.Pattern.RowCount.Value;
        }, token);

    private AutomationElement ResolveCell(NativeCell cell, bool waitForCode = false, bool preserveComboBox = false,
        bool requireEmptyCode = false)
    {
        var target = ResolveRowCells(cell, [cell.Column], CancellationToken.None, waitForCode, requireEmptyCode)[cell.Column];
        return preserveComboBox || string.Equals(cell.Column, cell.CodeColumn, StringComparison.Ordinal)
            ? target : DisplayCell(target);
    }

    private IReadOnlyDictionary<string, AutomationElement> ResolveRowCells(NativeCell cell,
        IReadOnlyList<string> columns, CancellationToken token, bool waitForCode = false, bool requireEmptyCode = false)
    {
        var grid = ResolveGrid(cell.GridId);
        if (!grid.Patterns.Grid.IsSupported || !grid.Patterns.Table.IsSupported)
        {
            var layout = ReadCustomGridLayout(grid, token,
                columns.Append(cell.CodeColumn).Distinct(StringComparer.Ordinal).ToArray(), cell, waitForCode, requireEmptyCode);
            return columns.ToDictionary(column => column,
                column => layout.Rows[0].Cells[FindCustomColumn(layout.Columns, column)], StringComparer.Ordinal);
        }
        var pattern = grid.Patterns.Grid.Pattern;
        var headers = grid.Patterns.Table.Pattern.ColumnHeaders.Value;
        if (waitForCode && cell.Row >= 0 && cell.Row >= pattern.RowCount.Value)
            throw new AutomationException("TARGET_NOT_FOUND", "Dòng đích chưa có trong native grid.");
        if (headers.Length != pattern.ColumnCount.Value || cell.Row < 0 || cell.Row >= pattern.RowCount.Value)
            throw new AutomationException("GRID_IDENTITY_INVALID", "Số dòng/cột không khớp native grid.");
        int Column(string name)
        {
            var found = headers.Select((header, index) => (header, index))
                .Where(item => string.Equals(item.header.Name, name, StringComparison.Ordinal)).ToArray();
            if (found.Length != 1) throw new AutomationException("GRID_COLUMN_AMBIGUOUS", $"Không xác định duy nhất cột '{name}'.");
            return found[0].index;
        }
        AutomationElement Cell(int column)
        {
            var item = pattern.GetItem(cell.Row, column);
            if (!item.Patterns.GridItem.IsSupported || item.Patterns.GridItem.Pattern.Row.Value != cell.Row
                || item.Patterns.GridItem.Pattern.Column.Value != column || item.Properties.ProcessId.ValueOrDefault != identity!.ProcessId)
                throw new AutomationException("GRID_IDENTITY_INVALID", "Native cell không khớp dòng/cột/process yêu cầu.");
            return item;
        }
        var code = ReadNativeText(Cell(Column(cell.CodeColumn))).Value;
        if (requireEmptyCode && !string.IsNullOrWhiteSpace(code))
            throw new AutomationException("GRID_ROW_CHANGED", "The insertion row is no longer empty; refusing to overwrite its code.");
        if (!string.IsNullOrWhiteSpace(cell.ExpectedCode)
            && !string.Equals(code.Trim(), cell.ExpectedCode, StringComparison.Ordinal))
        {
            if (waitForCode && string.IsNullOrWhiteSpace(code))
                throw new AutomationException("TARGET_NOT_FOUND", "Mã hàng vừa chọn chưa cập nhật vào dòng đích.");
            throw new AutomationException("GRID_ROW_CHANGED", "Mã hàng ở dòng đích đã thay đổi; không nhập tiếp.");
        }
        return columns.ToDictionary(column => column, column =>
        {
            token.ThrowIfCancellationRequested();
            return Cell(Column(column));
        }, StringComparer.Ordinal);
    }

    private NativeTable ReadCustomTable(AutomationElement grid, IReadOnlyList<string> requestedColumns, CancellationToken token)
    {
        var layout = ReadCustomGridLayout(grid, token, requestedColumns);
        var columns = layout.Columns.Select(column => column.Name).ToArray();
        var indices = columns.ToDictionary(name => name, name =>
        {
            var matches = layout.Columns.Select((column, index) => (column, index))
                .Where(item => string.Equals(item.column.Name, name, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new AutomationException("GRID_COLUMN_AMBIGUOUS", "Không xác định duy nhất cột " + name);
            return matches[0].index;
        });
        var rows = new List<IReadOnlyDictionary<string, string>>();
        var sources = new List<IReadOnlyDictionary<string, string>>();
        foreach (var row in layout.Rows)
        {
            token.ThrowIfCancellationRequested();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var rowSources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var column in indices)
            {
                // ComboBox cells expose an internal ValuePattern containing the
                // identifier while the visible text lives in their child Edit.
                // Read the column's editor, otherwise a unit such as "Gói" can
                // be mistaken for the adjacent account field.
                var text = ReadCustomCellText(row.Cells[column.Value], column.Key);
                values[column.Key] = text.Value;
                rowSources[column.Key] = text.Source + "+UIA3.CustomGrid.DataItem";
            }
            if (values.Values.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(values);
            sources.Add(rowSources);
        }
        return new NativeTable(rows, Capture(), sources);
    }

    private (string Value, string Source) ReadCustomCellText(AutomationElement cell, string column)
    {
        if (string.Equals(column, "ĐVT", StringComparison.Ordinal))
        {
            var status = cell.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
            const string marker = "DisplayText^IG_TOKEN_SEPERATOR^";
            var markerIndex = status.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                var label = status[(markerIndex + marker.Length)..];
                var end = label.IndexOf("^IG_TOKEN_DELIMETER^", StringComparison.Ordinal);
                if (end >= 0) label = label[..end];
                if (!string.IsNullOrWhiteSpace(label))
                    return (label, "UIA3.ItemStatus.DisplayText");
            }
            var selected = cell.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.ListItem))
                .Where(item => item.Patterns.SelectionItem.IsSupported
                    && item.Patterns.SelectionItem.Pattern.IsSelected.Value
                    && !string.IsNullOrWhiteSpace(item.Name))
                .ToArray();
            if (selected.Length == 1)
                return (selected[0].Name, "UIA3.SelectionItem+UnitLabel");
        }
        return ReadNativeText(DisplayCell(cell));
    }

    private AutomationElement SelectCustomRow(IReadOnlyList<AutomationElement> rows,
        IReadOnlyList<CustomColumn> columns, NativeCell cell, bool waitForCode, bool requireEmptyCode, CancellationToken token)
    {
        var codeColumn = columns[FindCustomColumn(columns, cell.CodeColumn)];
        string Code(AutomationElement candidate)
        {
            token.ThrowIfCancellationRequested();
            return ReadNativeText(MapCustomCells(candidate, [codeColumn], token)[0]).Value.Trim();
        }
        AutomationElement? row = null;
        // Infragistics repeats AutomationId across rows. Resolve by the
        // business key first so recreated rows cannot steal the next write.
        if (!string.IsNullOrWhiteSpace(cell.ExpectedCode))
        {
            var matches = rows.Where(candidate => string.Equals(Code(candidate), cell.ExpectedCode.Trim(), StringComparison.Ordinal)).ToArray();
            if (matches.Length == 1) row = matches[0];
            else if (matches.Length > 1)
                throw new AutomationException("GRID_ROW_AMBIGUOUS", $"Mã hàng '{cell.ExpectedCode}' xuất hiện ở nhiều dòng native.");
        }
        if (row is null)
        {
            if (cell.Row < 0 || cell.Row >= rows.Count)
                throw new AutomationException(waitForCode ? "TARGET_NOT_FOUND" : "GRID_IDENTITY_INVALID", "Số dòng không khớp native grid.");
            row = rows[cell.Row];
            var code = Code(row);
            if (!string.IsNullOrWhiteSpace(cell.ExpectedCode) && !string.Equals(code, cell.ExpectedCode.Trim(), StringComparison.Ordinal))
            {
                if (waitForCode && string.IsNullOrWhiteSpace(code))
                    throw new AutomationException("TARGET_NOT_FOUND", "Mã hàng vừa chọn chưa cập nhật vào dòng đích.");
                throw new AutomationException("GRID_ROW_CHANGED", "Mã hàng ở dòng đích đã thay đổi; không nhập tiếp.");
            }
        }
        if (requireEmptyCode && !string.IsNullOrWhiteSpace(Code(row)))
            throw new AutomationException("GRID_ROW_CHANGED", "The insertion row is no longer empty; refusing to overwrite its code.");
        return row;
    }
    private AutomationElement DisplayCell(AutomationElement target)
    {
        if (target.ControlType != ControlType.ComboBox) return target;
        var bounds = target.BoundingRectangle;
        return target.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Edit))
            .Where(item => item.Properties.ProcessId.ValueOrDefault == identity!.ProcessId
                && item.Properties.IsEnabled.ValueOrDefault && !item.Properties.IsOffscreen.ValueOrDefault
                && item.BoundingRectangle.Width > 0 && item.BoundingRectangle.Height > 0
                && item.BoundingRectangle.X >= bounds.X - 2
                && item.BoundingRectangle.Right <= bounds.Right + 2
                && item.BoundingRectangle.Y >= bounds.Y - 2
                && item.BoundingRectangle.Bottom <= bounds.Bottom + 2)
            .OrderByDescending(item => item.BoundingRectangle.Width)
            .FirstOrDefault() ?? target;
    }

    private void RealizeAndScroll(AutomationElement target)
    {
        target.Patterns.VirtualizedItem.PatternOrDefault?.Realize();
        if (target.Properties.IsOffscreen.ValueOrDefault)
            target.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
    }

    private static int FindCustomColumn(IReadOnlyList<CustomColumn> columns, string name)
    {
        var matches = columns.Select((column, index) => (column, index))
            .Where(item => string.Equals(item.column.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) throw new AutomationException("GRID_COLUMN_MISSING", "Column not found: " + name);
        if (matches.Length == 1) return matches[0].index;
        return matches.OrderByDescending(item => item.column.Right - item.column.Left).ThenBy(item => item.column.Left).First().index;
    }

    private CustomGridLayout ReadCustomGridLayout(AutomationElement grid, CancellationToken token,
        IReadOnlyList<string>? requestedColumns = null, NativeCell? rowTarget = null,
        bool waitForCode = false, bool requireEmptyCode = false)
    {
        var headers = grid.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.HeaderItem))
            .Where(element => element.Properties.ProcessId.ValueOrDefault == identity!.ProcessId)
            .Select(element => new CustomColumn(element.Name, element.BoundingRectangle.X,
                element.BoundingRectangle.X + element.BoundingRectangle.Width, element))
            // Hidden/offscreen headers have no native cell in the current viewport.
            // Keep only columns whose native header is actually exposed so a
            // custom grid cannot be treated as if an invisible column existed.
            .Where(column => !string.IsNullOrWhiteSpace(column.Name) && column.Right > column.Left)
            .GroupBy(column => column.Name, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(column => column.Right - column.Left).First())
            .OrderBy(column => column.Left).ToArray();
        if (headers.Length == 0) throw new AutomationException("GRID_COLUMNS_INVALID", "Không có tiêu đề cột native của bảng.");

        if (requestedColumns is { Count: > 0 })
        {
            var requested = requestedColumns.ToHashSet(StringComparer.Ordinal);
            var missing = requested.Where(name => headers.All(column => !string.Equals(column.Name, name, StringComparison.Ordinal))).ToArray();
            if (missing.Length > 0)
                throw new AutomationException("GRID_COLUMN_AMBIGUOUS", "Native grid is missing requested column(s): " + string.Join(", ", missing));
            headers = headers.Where(column => requested.Contains(column.Name)).ToArray();
        }

        var rows = grid.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.DataItem))
            .Where(element => element.Properties.ProcessId.ValueOrDefault == identity!.ProcessId
                && element.Properties.IsEnabled.ValueOrDefault
                && element.BoundingRectangle.Width > 0 && element.BoundingRectangle.Height > 0)
            .Where(IsLogicalCustomRow)
            .OrderBy(element => element.BoundingRectangle.Y).ToArray();
        if (rows.Length == 0)
            throw new AutomationException("GRID_ROWS_INVALID", "Không có dòng native của bảng.");
        if (rowTarget is not null)
            rows = [SelectCustomRow(rows, headers, rowTarget, waitForCode, requireEmptyCode, token)];
        return new CustomGridLayout(headers, rows.Select(row => new CustomRow(row, MapCustomCells(row, headers, token))).ToArray());
    }

    private bool IsLogicalCustomRow(AutomationElement row)
    {
        var id = row.Properties.AutomationId.ValueOrDefault;
        // R20 uses -1 for the enabled insertion row even when committed rows
        // already have numeric IDs. The disabled -1 row at the bottom is the
        // grid's placeholder and is excluded by the caller's enabled check.
        return id != "-1" || row.Properties.IsEnabled.ValueOrDefault;
    }

    private static IReadOnlyList<AutomationElement> MapCustomCells(AutomationElement row,
        IReadOnlyList<CustomColumn> columns, CancellationToken token)
    {
        var cells = row.FindAllDescendants()
            .Where(element => element.ControlType is ControlType.Edit or ControlType.ComboBox or ControlType.Text
                && element.Properties.ProcessId.ValueOrDefault == row.Properties.ProcessId.ValueOrDefault
                && element.BoundingRectangle.Width > 0 && element.BoundingRectangle.Height > 0)
            .GroupBy(element => (element.BoundingRectangle.X, element.BoundingRectangle.Y,
                element.BoundingRectangle.Width, element.BoundingRectangle.Height))
            .Select(group => group.First()).ToArray();
        return columns.Select(column =>
        {
            token.ThrowIfCancellationRequested();
            var matches = cells.Select(cell => (cell, overlap: HorizontalOverlap(cell.BoundingRectangle.X,
                    cell.BoundingRectangle.X + cell.BoundingRectangle.Width, column.Left, column.Right)))
                .Where(item => item.overlap > 0)
                .OrderByDescending(item => item.overlap)
                .ThenBy(item => Math.Abs(item.cell.BoundingRectangle.X - column.Left))
                .ToArray();
            if (matches.Length == 0)
                throw new AutomationException("GRID_COLUMNS_INVALID", "Không ánh xạ được ô native cho cột " + column.Name);
            return matches[0].cell;
        }).ToArray();
    }

    private static double HorizontalOverlap(double left, double right, double otherLeft, double otherRight)
        => Math.Max(0, Math.Min(right, otherRight) - Math.Max(left, otherLeft));

    private sealed record CustomColumn(string Name, double Left, double Right, AutomationElement Header);
    private sealed record CustomRow(AutomationElement Element, IReadOnlyList<AutomationElement> Cells);
    private sealed record CustomGridLayout(IReadOnlyList<CustomColumn> Columns, IReadOnlyList<CustomRow> Rows);

    private void SetNativeValue(AutomationElement element, string text, CancellationToken token,
        ref bool sent, bool allowUnverifiedFocus = false)
    {
        if (text.Length > 32768 || text.Any(char.IsControl)) throw new AutomationException("INVALID_TEXT", "Giá trị nhập không hợp lệ.");
        if (!element.Patterns.Value.IsSupported || element.Patterns.Value.Pattern.IsReadOnly.Value)
            throw new AutomationException("PATTERN_UNSUPPORTED", "Ô không hỗ trợ ghi ValuePattern.");
        // Infragistics ComboBox providers can accept ValuePattern.SetValue
        // without moving Win32 focus to the visible editor. In the catalog
        // filter that leaves Enter on the grid and the query is never applied.
        // Activate the visible child editor before committing the value.
        FocusTarget(element, activateGridCell: element.ControlType == ControlType.ComboBox || IsVirtualGridEditor(element),
            token: token, verifyFocus: !allowUnverifiedFocus);
        token.ThrowIfCancellationRequested();
        sent = true;
        MarkWorkflowInput();
        element.Patterns.Value.Pattern.SetValue(text);
    }

    private void FocusTarget(AutomationElement target, bool activateGridCell = false,
        CancellationToken token = default, bool verifyFocus = true)
    {
        ThrowIfBlockingDialog();
        var handle = KeyboardHandle(target);
        NativeKeyboard.Activate(handle, identity!.WindowHandle, identity.ProcessId);
        target.Focus();
        if (activateGridCell)
        {
            var clickTarget = target;
            if (target.ControlType == ControlType.ComboBox)
            {
                clickTarget = target.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Edit))
                    .Where(item => item.Properties.IsEnabled.ValueOrDefault && !item.Properties.IsOffscreen.ValueOrDefault
                        && item.BoundingRectangle.Width > 0 && item.BoundingRectangle.Height > 0)
                    .OrderByDescending(item => item.BoundingRectangle.Width)
                    .FirstOrDefault() ?? target;
            }
            var bounds = clickTarget.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0 || target.Properties.IsOffscreen.ValueOrDefault)
                throw new AutomationException("GRID_OFFSCREEN", "Grid cell has no visible native bounds.");
            MarkWorkflowInput();
            NativeKeyboard.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                identity!.WindowHandle, identity.ProcessId);
            // A Win32 error dialog can appear asynchronously after the grid
            // click and owns focus before UIA reports the change. Surface the
            // dialog identity instead of reducing this to a misleading focus
            // failure.
            ThrowIfBlockingDialog();
        }
        if (verifyFocus && !IsFocusedTarget(target, handle))
            throw new AutomationException("FOCUS_FAILED", "Ô đích chưa nhận focus UIA/Win32.");
    }

    private static bool IsVirtualGridEditor(AutomationElement element)
    {
        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.ControlType is ControlType.DataItem or ControlType.DataGrid)
                return true;
            if (parent.ControlType == ControlType.Custom
                && (parent.Properties.ItemStatus.ValueOrDefault ?? "").Contains("UltraGridUIAProviderStub", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void Commit(AutomationElement target, string key, CancellationToken token,
        bool allowUnverifiedFocus = false)
    {
        token.ThrowIfCancellationRequested();
        var inputTarget = ResolveKeyboardTarget(target);
        if (target.ControlType == ControlType.ComboBox)
            FocusTarget(inputTarget, activateGridCell: true, token: token, verifyFocus: !allowUnverifiedFocus);
        var inputHandle = KeyboardHandle(inputTarget);
        if (!allowUnverifiedFocus && !IsFocusedTarget(inputTarget, inputHandle))
            throw new AutomationException("FOCUS_LOST", "The native editor lost focus before commit.");
        MarkWorkflowInput();
        if (allowUnverifiedFocus)
            NativeKeyboard.PressFocused(identity!.WindowHandle, identity.ProcessId, key, token);
        else
            NativeKeyboard.Press(inputHandle, identity!.WindowHandle, identity.ProcessId, key, token);
    }

    private AutomationElement ResolveKeyboardTarget(AutomationElement target)
    {
        if (target.ControlType != ControlType.ComboBox) return target;
        return target.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Edit))
            .Where(item => item.Properties.ProcessId.ValueOrDefault == identity!.ProcessId
                && item.Properties.IsEnabled.ValueOrDefault
                && !item.Properties.IsOffscreen.ValueOrDefault
                && item.BoundingRectangle.Width > 0 && item.BoundingRectangle.Height > 0)
            .OrderByDescending(item => item.BoundingRectangle.Width)
            .FirstOrDefault() ?? target;
    }

    private nint KeyboardHandle(AutomationElement target)
    {
        for (var ancestor = target; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Properties.ProcessId.ValueOrDefault != identity!.ProcessId) break;
            var handle = ancestor.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle != nint.Zero) return handle;
        }
        throw new AutomationException("KEY_TARGET_INVALID", "Không xác minh được HWND của ô đích.");
    }

    private bool FocusBelongsTo(AutomationElement target)
    {
        for (var focused = automation.FocusedElement(); focused is not null; focused = focused.Parent)
        {
            if (focused.Equals(target)) return true;
            if (focused.Properties.ProcessId.ValueOrDefault != identity!.ProcessId) break;
        }
        return false;
    }

    private bool IsFocusedTarget(AutomationElement target, nint handle)
        => FocusBelongsTo(target)
            // Virtual R20 cells often expose no HWND on the leaf editor. The
            // nearest native grid/editor ancestor is the authoritative focus
            // scope for Win32 keyboard input.
            || handle != nint.Zero
            && (target.Properties.NativeWindowHandle.ValueOrDefault == handle
                || target.Properties.NativeWindowHandle.ValueOrDefault == nint.Zero && IsVirtualGridEditor(target))
            && NativeKeyboard.HasTargetFocus(handle);

    private static bool IsCatalogDialogName(string? name, string? automationId = null)
    {
        var values = new[] { name, automationId }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim());
        return values.Any(value => value.Equals("frmQuickSearch", StringComparison.OrdinalIgnoreCase)
            || value.Contains("T" + '\u00ec' + "m nhanh", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsCatalogDialog(AutomationElement element)
    {
        if (IsCatalogDialogName(element.Properties.Name.ValueOrDefault))
            return true;

        // Some R20 builds expose the modal without a stable form name or
        // AutomationId. Its catalog contract is stable: the result grid is
        // grdData and the search editor is the visible ComboBox with id 4.
        var grid = element.FindAllDescendants(automation.ConditionFactory.ByAutomationId("grdData"))
            .Any(child => child.Properties.IsEnabled.ValueOrDefault
                && !child.Properties.IsOffscreen.ValueOrDefault
                && child.BoundingRectangle.Width > 0 && child.BoundingRectangle.Height > 0);
        return grid;
    }

    private void ThrowIfBlockingDialog()
    {
        if (identity is null) return;
        var host = automation.FromHandle(identity.WindowHandle);
        var dialogs = host.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Window))
            .Where(element => element.Properties.ProcessId.ValueOrDefault == identity.ProcessId
                && element.Properties.IsEnabled.ValueOrDefault
                && !element.Properties.IsOffscreen.ValueOrDefault
                && element.Properties.NativeWindowHandle.ValueOrDefault != nint.Zero
                && element.Properties.NativeWindowHandle.ValueOrDefault != identity.WindowHandle
                && !string.Equals(element.Properties.AutomationId.ValueOrDefault, "frmSAVoucherDetail", StringComparison.OrdinalIgnoreCase))
            .Where(element => element.Patterns.Window.IsSupported && element.Patterns.Window.Pattern.IsModal.Value
                || string.Equals(element.Properties.ClassName.ValueOrDefault, "#32770", StringComparison.Ordinal))
            .Where(element => !IsCatalogDialog(element))
            .ToArray();
        if (dialogs.Length == 0) return;

        var details = dialogs.Select(dialog =>
        {
            var labels = dialog.FindAllDescendants()
                .Select(element => element.Properties.Name.ValueOrDefault?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .Take(8);
            return $"{dialog.Properties.Name.ValueOrDefault}: {string.Join(" | ", labels)}";
        });
        throw new AutomationException("DIALOG_REQUIRES_ATTENTION",
            "MISA dialog đang chặn editor. Hãy xử lý dialog trước khi tiếp tục: " + string.Join("; ", details), true);
    }

    private AutomationElement Resolve(NativeSelector selector)
    {
        if (root is null || identity is null) throw new AutomationException("NOT_CONNECTED", "Chưa chọn cửa sổ MISA.");
        var condition = !string.IsNullOrWhiteSpace(selector.AutomationId)
            ? automation.ConditionFactory.ByAutomationId(selector.AutomationId)
            : !string.IsNullOrWhiteSpace(selector.Name) ? automation.ConditionFactory.ByName(selector.Name)
            : automation.ConditionFactory.ByControlType(Enum.Parse<ControlType>(selector.ControlType));
        var elements = root.FindAllDescendants(condition).Where(element => element.Properties.ProcessId.ValueOrDefault == identity.ProcessId
            && (selector.Name.Length == 0 || element.Name == selector.Name)
            && (selector.ControlType.Length == 0 || element.ControlType.ToString() == selector.ControlType)
            && element.Properties.IsEnabled.ValueOrDefault && !element.Properties.IsOffscreen.ValueOrDefault
            && element.BoundingRectangle.Width > 0 && element.BoundingRectangle.Height > 0).ToArray();
        if (elements.Length == 0) throw new AutomationException("TARGET_NOT_FOUND", $"Chưa có native target: {selector.AutomationId}/{selector.Name}.");
        if (elements.Length != 1) throw new AutomationException("TARGET_AMBIGUOUS", $"Cần đúng một native target: {selector.AutomationId}/{selector.Name}.");
        return elements[0];
    }

    private AutomationElement ResolveGrid(string gridId)
        => Resolve(gridId.Length > 0 ? new(gridId) : new(ControlType: "DataGrid"));

    private void GuardWorkflow(NativeFrame frame, bool forInput = false)
    {
        EnsureFrame(frame);
        var current = Capture();
        if (current.Window.Dpi != frame.Window.Dpi || current.Window.ClientBounds != frame.Window.ClientBounds
            // Slow provider reads may take a minute; input requires a recent frame.
            || DateTimeOffset.UtcNow - frame.ObservedAt > TimeSpan.FromSeconds(forInput ? 10 : 60))
            throw new AutomationException("STALE_NATIVE_FRAME", "Frame/bounds/DPI đã thay đổi; cần quan sát mới.");
        if (!root!.Properties.IsEnabled.ValueOrDefault || root.Properties.IsOffscreen.ValueOrDefault
            || root.Properties.ProcessId.ValueOrDefault != identity!.ProcessId
            || root.Name != frame.TabIdentity)
            throw new AutomationException("SCOPE_IDENTITY_CHANGED", "Scope đã đổi, bị ẩn hoặc bị khóa; không gửi input.");
        var ancestors = new List<AutomationElement>();
        for (var parent = root.Parent; parent is not null && parent.Properties.ProcessId.ValueOrDefault == identity.ProcessId; parent = parent.Parent)
            ancestors.Add(parent);
        var host = automation.FromHandle(identity!.WindowHandle);
        var dialogs = host.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Window))
            .Where(element => element.Properties.IsEnabled.ValueOrDefault && !element.Properties.IsOffscreen.ValueOrDefault
                && element.Patterns.Window.IsSupported
                && element.Patterns.Window.Pattern.IsModal.Value && !element.Equals(root)
                && !ancestors.Any(parent => parent.Equals(element))).ToArray();
        if (dialogs.Length > 0)
        {
            var details = dialogs.Select(dialog =>
            {
                var labels = dialog.FindAllDescendants()
                    .Select(element => element.Name?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .Take(8);
                return $"{dialog.Name}: {string.Join(" | ", labels)}";
            });
            throw new AutomationException("DIALOG_REQUIRES_ATTENTION",
                "MISA modal dialog requires attention. Dialog: " + string.Join("; ", details));
        }
    }

    private static (string Value, string Source) ReadNativeText(AutomationElement element)
    {
        if (element.Patterns.Value.IsSupported)
        {
            var value = element.Patterns.Value.Pattern.Value.Value;
            if (value.Length > 32768) throw new AutomationException("READBACK_TRUNCATED", "Native value vượt giới hạn đọc.");
            return (value, "UIA3.ValuePattern");
        }
        if (element.Patterns.Text.IsSupported)
        {
            var text = element.Patterns.Text.Pattern.DocumentRange.GetText(32769);
            if (text.Length > 32768) throw new AutomationException("READBACK_TRUNCATED", "Native text vượt giới hạn đọc.");
            return (text, "UIA3.TextPattern");
        }
        var handle = element.Properties.NativeWindowHandle.ValueOrDefault;
        if (handle == nint.Zero || element.ControlType != ControlType.Edit)
            throw new AutomationException("READBACK_UNSUPPORTED", "Không có Value/Text pattern hoặc HWND Edit để đọc dữ liệu.");
        if (SendMessageTimeout(handle, 0x000E, 0, nint.Zero, 2, 500, out var length) == nint.Zero || length > 32768)
            throw new AutomationException("READBACK_UNAVAILABLE", "WM_GETTEXTLENGTH không xác nhận được độ dài.");
        var buffer = new StringBuilder(checked((int)length + 1));
        if (SendMessageTimeoutText(handle, 0x000D, (nuint)buffer.Capacity, buffer, 2, 500, out var copied) == nint.Zero
            || copied != length) throw new AutomationException("READBACK_TRUNCATED", "WM_GETTEXT không đọc đủ giá trị.");
        return (buffer.ToString(), "Win32.WM_GETTEXT");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutText(nint hwnd, uint message, nuint wparam, StringBuilder lparam, uint flags, uint timeout, out nuint result);
}
