using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;

namespace MisaManager.Automation;

/// <param name="Label">
/// Caption of the form label left of the control, for MISA dialogs whose editors have no known AutomationId: the
/// control is the nearest one of <paramref name="ControlType"/> (default Edit) to the right of that label.
/// </param>
public sealed record NativeSelector(string AutomationId = "", string Name = "", string ControlType = "", string Label = "")
{
    public bool ByLabel => AutomationId.Length == 0 && Label.Length > 0;
}
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
    private long tabInputSequence;
    // Input that can change voucher values; tab selection only changes the view.
    public long ValueInputSequence => WorkflowInputSequence - Interlocked.Read(ref tabInputSequence);

    public Task<bool> WorkflowScopeExistsAsync(WindowIdentity window, string scopeId, CancellationToken token)
        => dispatcher.RunAsync(() => WorkflowScopes(window, scopeId).Length != 0, token);

    public Task<NativeFrame?> TryBindWorkflowScopeAsync(WindowIdentity window, string scopeId, string? expectedTitle,
        CancellationToken token, bool waitForEnabled = false)
        => dispatcher.RunAsync<NativeFrame?>(() =>
        {
            var scopes = BoundScope(window, scopeId) is { } bound ? [bound] : WorkflowScopes(window, scopeId);
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

    /// <summary>Read-only: the text of a MISA modal dialog blocking the bound window, or null when none is shown.</summary>
    public Task<string?> BlockingDialogAsync(WindowIdentity window, CancellationToken token)
        => dispatcher.RunAsync<string?>(() =>
        {
            if (identity is null || identity.ProcessId != window.ProcessId || identity.WindowHandle != window.WindowHandle) return null;
            try { ThrowIfBlockingDialogOnce(); return null; }
            catch (AutomationException error) when (error.Code is "DIALOG_REQUIRES_ATTENTION" or "MISA_OUT_OF_MEMORY") { return error.Message; }
        }, token);

    // R20 can show the voucher as a window owned by MISA's main window. Its grid must be read while that window is active:
    // inactive, its "Thuế" grid exposed rows without cells (VPS 0.2.166, HĐ 81). Sends nothing when it is already in front.
    public Task<NativeFrame> ActivateWorkflowScopeAsync(NativeFrame frame, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame, forInput: true);
            NativeKeyboard.Activate(KeyboardHandle(root!), identity!.WindowHandle, identity.ProcessId, token);
            return Capture();
        }, token);

    private string ScopeActivation()
    {
        try { return NativeKeyboard.IsForegroundRoot(KeyboardHandle(root!)) ? "đang active" : "không active"; }
        catch (Exception error) when (error is AutomationException or COMException || Vanished(error)) { return "không rõ"; }
    }

    private long scopeSearches;
    /// <summary>Full searches of MISA's window for a workflow scope (each one walks the Bán hàng list too).</summary>
    public long WorkflowScopeSearches => Interlocked.Read(ref scopeSearches);

    private void RequireWindow(WindowIdentity window)
    {
        var current = RefreshWindow(window);
        if (current.ProcessStartUtc != window.ProcessStartUtc || current.ExecutablePath != window.ExecutablePath
            || current.Title != window.Title)
            throw new AutomationException("WINDOW_IDENTITY_CHANGED", "Phiên MISA thay đổi trong khi bind scope.");
    }

    // The scope an earlier step bound (unique when found) stays the proven target while that same element is live,
    // on screen and still carries the scope id. Closed, replaced or hidden scopes are searched for again.
    private AutomationElement? BoundScope(WindowIdentity window, string scopeId)
    {
        if (root is null || identity is null || identity.WindowHandle != window.WindowHandle
            || identity.ProcessId != window.ProcessId || identity.ProcessStartUtc != window.ProcessStartUtc)
            return null;
        RecordQuery(false);
        RequireWindow(window);
        ElementSnapshot? snapshot;
        try { snapshot = TrySnapshot(root); }
        catch (Exception error) when (Vanished(error)) { return null; }
        return snapshot is { Offscreen: false, HasArea: true } && snapshot.AutomationId == scopeId
            && snapshot.ProcessId == window.ProcessId ? root : null;
    }

    private AutomationElement[] WorkflowScopes(WindowIdentity window, string scopeId)
    {
        RecordQuery(false);
        Interlocked.Increment(ref scopeSearches);
        RequireWindow(window);
        return ProcessSnapshots(window.WindowHandle, window.ProcessId,
                Live(automation.ConditionFactory.ByAutomationId(scopeId), window.ProcessId, enabled: false))
            .Select(item => item.Element).ToArray();
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
                    // Realize virtualized cells before reading; scrolling is observational.
                    // Identity is proven once, on the cell that is actually read.
                    var virtualized = cell.Patterns.VirtualizedItem.PatternOrDefault;
                    if (virtualized is not null)
                    {
                        virtualized.Realize();
                        cell = pattern.GetItem(row, column.Value);
                    }
                    if (cell.Properties.IsOffscreen.ValueOrDefault && cell.Patterns.ScrollItem.IsSupported)
                    {
                        cell.Patterns.ScrollItem.Pattern.ScrollIntoView();
                        cell = pattern.GetItem(row, column.Value);
                    }
                    var gridItem = cell.Patterns.GridItem.PatternOrDefault;
                    if (gridItem is null || gridItem.Row.Value != row || gridItem.Column.Value != column.Value
                        || cell.Properties.ProcessId.ValueOrDefault != identity!.ProcessId)
                        throw new AutomationException("GRID_IDENTITY_INVALID", "Native cell đổi vị trí trong khi đọc.");
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

    public Task<NativeFields> ReadFieldsAsync(NativeFrame frame, IReadOnlyDictionary<string, NativeSelector> fields,
        CancellationToken token, bool preferDisplayText = false)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            GuardWorkflow(frame);
            var values = new Dictionary<string, (string Value, string Source)>();
            var targets = ResolveMany(fields.Values.ToArray());
            var index = 0;
            foreach (var field in fields)
            {
                token.ThrowIfCancellationRequested();
                values.Add(field.Key, preferDisplayText
                    ? ReadDisplayText(targets[index++])
                    : ReadNativeText(targets[index++]));
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

    /// <summary>
    /// Chooses an option of a drop-down-only combo as a user does: click its arrow, then the option in the open list.
    /// R20's "Chứng từ mua hàng" takes no focus or typing (report 0.2.188, HĐ 205: "Ô đích chưa nhận focus"). Sends
    /// nothing when the combo already shows the option (R20 reopens the voucher type used last).
    /// </summary>
    public async Task<Readback> SelectFieldOptionAsync(NativeFrame frame, NativeSelector selector, string expected,
        string cultureName, CancellationToken token)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        bool Shown(Readback read) => NativeComparison.Matches(expected, read.Value, NativeValueKind.Text, culture);
        var current = await ReadFieldOptionAsync(frame, selector, expected, token);
        if (Shown(current)) return current;
        var expectedName = NormalizeOptionName(expected);
        var sentFrame = await dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var combo = Resolve(selector);
            var bounds = combo.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0 || combo.Properties.IsOffscreen.ValueOrDefault)
                throw new AutomationException("FIELD_OFFSCREEN", "Ô danh sách không hiển thị trên màn hình.");
            var comboWindow = KeyboardHandle(combo);
            ThrowIfBlockingDialog();
            try
            {
                MarkWorkflowInput();
                // The arrow is the right end of the combo.
                NativeKeyboard.Click(bounds.Right - Math.Min(8, bounds.Width / 4), bounds.Y + bounds.Height / 2,
                    identity!.WindowHandle, identity.ProcessId, comboWindow);
                epoch++;
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được mở danh sách '" + expected + "'.", true, error);
            }
            AutomationElement[] options = [];
            for (var attempt = 0; attempt < 40 && options.Length == 0; attempt++)
            {
                token.ThrowIfCancellationRequested();
                options = OpenOptions(expectedName, comboWindow);
                if (options.Length == 0) Thread.Sleep(50);
            }
            var option = options.FirstOrDefault(ShowsAt) ?? (options.Length == 1 ? options[0] : null)
                ?? throw new AutomationException("INPUT_UNCERTAIN", $"Danh sách không mở hoặc không có mục '{expected}'; không chọn.", true);
            try
            {
                MarkWorkflowInput();
                var optionBounds = option.BoundingRectangle;
                // The list can close between the search and the click; never click through it onto the form.
                NativeKeyboard.ClickOpenList(optionBounds.X + optionBounds.Width / 2, optionBounds.Y + optionBounds.Height / 2,
                    identity!.ProcessId, hit => InOwnPopup(hit, comboWindow));
                epoch++;
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", $"Không xác nhận được chọn '{expected}'.", true, error);
            }
            return Capture();
        }, token, maySendInput: true);
        Readback last;
        try
        {
            last = await NativeWait.UntilAsync(() => ReadFieldOptionAsync(sentFrame, selector, expected, token), Shown,
                "READBACK_MISMATCH", "Native readback chưa khớp giá trị vừa chọn.", token, returnLastMismatch: true);
        }
        catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", error.Message, true, error); }
        if (!Shown(last))
            throw new AutomationException("INPUT_UNCERTAIN", $"Đã chọn '{expected}' nhưng MISA chưa hiện mục này. {last.Reason}", true);
        return last;
    }

    /// <summary>
    /// The option a combo shows. R20's ValuePattern gives the item key ("1" while "2. Mua hàng trong nước không qua kho" is
    /// shown, report 0.2.189, HĐ 205), so the window text, the inner edit, the selected item and the editor title (R20
    /// titles a purchase form after its type) are read too; the value is the one naming <paramref name="expected"/>, if any.
    /// </summary>
    private Task<Readback> ReadFieldOptionAsync(NativeFrame frame, NativeSelector selector, string expected, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            // R20 renames a purchase form after its type once the re-layout ends, after the post-click frame was taken
            // (report 0.2.197, HĐ 392). Only a title naming the chosen type, with the same company suffix, is that form.
            var title = root!.Properties.Name.ValueOrDefault;
            if (title is not null && title != frame.TabIdentity && RetitledTo(frame.TabIdentity, title, expected))
                frame = frame with { TabIdentity = title };
            GuardWorkflow(frame);
            var combo = Resolve(selector);
            var seen = new List<(string Value, string Source)>();
            void Read(Func<(string? Value, string Source)> read)
            {
                try { if (read() is { Value: { } value } item) seen.Add((value, item.Source)); }
                catch (Exception error) when (error is AutomationException or COMException || Vanished(error)) { }
            }
            Read(() => ReadNativeText(combo));
            Read(() => (ControlText(combo.Properties.NativeWindowHandle.ValueOrDefault), "Win32.WM_GETTEXT"));
            Read(() => WidestEditor(combo) is { } editor ? ReadNativeText(editor) : (null, ""));
            Read(() => ReadDisplayText(combo));
            Read(() => (TitleNames(root!.Properties.Name.ValueOrDefault, expected) ? expected : null, "UIA3.EditorTitle"));
            var key = TextKey.Canonical(expected);
            var shown = seen.FirstOrDefault(item => TextKey.Canonical(item.Value) == key);
            var fresh = Capture();
            return shown.Value is not null
                ? new Readback("supported", shown.Value, shown.Source, fresh.ObservedAt, null, fresh)
                : new Readback("supported", seen.FirstOrDefault().Value, seen.FirstOrDefault().Source ?? "none", fresh.ObservedAt,
                    "Đọc được: " + string.Join("; ", seen.Select(item => $"{item.Source}='{item.Value}'"))
                    + $"; tiêu đề '{root!.Properties.Name.ValueOrDefault}'.", fresh);
        }, token);

    // "2. Mua hàng trong nước không qua kho" → the form is titled "Mua hàng trong nước không qua kho chưa thanh toán - …".
    private static bool TitleNames(string? title, string option)
    {
        var label = TextKey.Canonical(OptionNumber().Replace(option, ""));
        var text = TextKey.Canonical(title);
        return label.Length > 0 && (text == label || text.StartsWith(label + " ", StringComparison.Ordinal));
    }

    private static bool RetitledTo(string before, string after, string option)
    {
        static string Suffix(string title) => title.IndexOf(" - ", StringComparison.Ordinal) is var at and >= 0 ? title[at..] : "";
        return TitleNames(after, option) && !TitleNames(before, option) && Suffix(before) == Suffix(after);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\d+\.\s*")]
    private static partial System.Text.RegularExpressions.Regex OptionNumber();

    // WM_GETTEXT: GetWindowText does not read a control of another process.
    private static string? ControlText(nint handle)
    {
        if (handle == nint.Zero || SendMessageTimeout(handle, 0x000E, 0, nint.Zero, 2, 500, out var length) == nint.Zero
            || length > 32768) return null;
        var buffer = new StringBuilder(checked((int)length + 1));
        return SendMessageTimeoutText(handle, 0x000D, (nuint)buffer.Capacity, buffer, 2, 500, out _) == nint.Zero
            ? null : buffer.ToString();
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
                try { MarkWorkflowInput(); Interlocked.Increment(ref tabInputSequence); tab.Patterns.SelectionItem.Pattern.Select(); epoch++; }
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

    public Task RequireDisabledCommandAsync(NativeFrame frame, NativeSelector selector, CancellationToken token)
        => RequireCommandEnabledAsync(frame, selector, false, token);

    public Task RequireCommandEnabledAsync(NativeFrame frame, NativeSelector selector, bool enabled, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame);
            var condition = selector.AutomationId.Length > 0
                ? automation.ConditionFactory.ByAutomationId(selector.AutomationId)
                : automation.ConditionFactory.ByName(selector.Name);
            var commands = Snapshots(root!, Live(condition.And(automation.ConditionFactory.ByControlType(ControlType.Button)),
                identity!.ProcessId, enabled: false)).ToArray();
            if (commands.Length != 1 || commands[0].Enabled != enabled)
                throw new AutomationException("COMMAND_STATE_MISMATCH", $"Expected one native command with enabled={enabled}; refusing to close the voucher.");
            return true;
        }, token);

    // Non-blocking probe for optional early exits: a disabled scope (modal prompt, posting) reads false at once
    // instead of settling like GuardWorkflow. Never use it as proof that a command is unavailable.
    public Task<bool> CommandEnabledNowAsync(NativeSelector selector, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            if (!ScopeReady(Capture())) return false;
            var condition = selector.AutomationId.Length > 0
                ? automation.ConditionFactory.ByAutomationId(selector.AutomationId)
                : automation.ConditionFactory.ByName(selector.Name);
            var commands = Snapshots(root!, Live(condition.And(automation.ConditionFactory.ByControlType(ControlType.Button)),
                identity!.ProcessId, enabled: false)).ToArray();
            return commands.Length == 1 && commands[0].Enabled;
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
        NativeValueKind kind, string cultureName, CancellationToken token, string commitKey = "Tab")
    {
        var sentFrame = await SendCellAsync(frame, cell, text, token, commitKey);
        var reread = cell.Column == cell.CodeColumn ? cell with { ExpectedCode = expected } : cell;
        return await VerifyWrittenAsync(() => ReadCellAsync(sentFrame, reread, token, waitForCode: cell.Column == cell.CodeColumn), expected, kind, cultureName, token);
    }

    // Commit each value through MISA, then verify the whole row before publishing progress.
    // Controls are resolved again after each commit because R20 recreates its editors.
    // explain: the reason for a row MISA still shows differently after the wait (report 0.2.195 gave none).
    public async Task<NativeFields> WriteRowAsync(NativeFrame frame, NativeCell row,
        IReadOnlyList<NativeCellWrite> writes, IReadOnlyList<string> readColumns,
        Func<NativeFields, bool> verified, CancellationToken token, Action<string>? enteringColumn = null,
        Func<NativeFields, string>? explain = null)
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
            var last = await NativeWait.UntilAsync(() => ReadRowAsync(frame, row, readColumns, token), verified,
                "READBACK_MISMATCH", $"Row {row.Row + 1} ({row.ExpectedCode}) does not match after native writes.", token,
                returnLastMismatch: true);
            if (verified(last)) return last;
            throw new AutomationException("READBACK_MISMATCH", explain?.Invoke(last)
                ?? "MISA đọc lại: " + string.Join("; ", last.Values.Select(pair => $"{pair.Key}='{pair.Value.Value}'")) + ".", true);
        }
        catch (Exception error)
        {
            var sent = WorkflowInputSequence != sequence || error is AutomationException { InputMayHaveBeenSent: true };
            throw new AutomationException("ROW_INPUT_UNCERTAIN", $"Row {row.Row + 1} ({row.ExpectedCode}): {error.Message}", sent, error);
        }
    }

    /// <summary>Types the values into one bound row without a readback; the caller reconciles later.</summary>
    public async Task<NativeFrame> SendRowValuesAsync(NativeFrame frame, NativeCell row,
        IReadOnlyList<NativeCellWrite> writes, CancellationToken token, Action<string>? enteringColumn = null)
    {
        if (string.IsNullOrWhiteSpace(row.ExpectedCode) || writes.Count is < 1 or > 3
            || writes.Select(write => write.Column).Distinct(StringComparer.Ordinal).Count() != writes.Count
            || writes.Any(write => write.Column == row.CodeColumn || write.Text.Length > 32768 || write.Text.Any(char.IsControl)))
            throw new ArgumentException("A row batch requires a bound item code and 1-3 distinct value columns.");
        var sequence = WorkflowInputSequence;
        try { return await SendRowAsync(frame, row, writes, token, enteringColumn); }
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
                if (target.Properties.IsOffscreen.ValueOrDefault)
                {
                    RealizeAndScroll(target);
                    target = ResolveCell(cell);
                }
                if (target.Properties.IsOffscreen.ValueOrDefault)
                    throw new AutomationException("GRID_OFFSCREEN", "Row cell remains offscreen after native realization.");
                var sent = false;
                // Infragistics virtual cells expose a reliable ValuePattern but
                // can report focus on the grid host instead of the clicked cell.
                // Keep the final row readback as the business guard while allowing
                // the native value write and Tab commit to proceed.
                var allowUnverifiedFocus = IsVirtualGridEditor(target);
                try
                {
                    SetNativeValue(target, write.Text, token, ref sent, allowUnverifiedFocus);
                    Commit(target, "Tab", token, allowUnverifiedFocus);
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

    private Task<NativeFrame> SendCellAsync(NativeFrame frame, NativeCell cell, string text, CancellationToken token,
        string commitKey = "Tab")
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var requireEmptyCode = cell.Column == cell.CodeColumn && string.IsNullOrWhiteSpace(cell.ExpectedCode);
            var target = ResolveCell(cell, requireEmptyCode: requireEmptyCode);
            if (target.Properties.IsOffscreen.ValueOrDefault)
            {
                RealizeAndScroll(target);
                target = ResolveCell(cell, requireEmptyCode: requireEmptyCode);
            }
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
                Commit(target, commitKey, token);
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
            var editor = WidestEditor(target);
            // Infragistics keeps the embedded dropdown button at 0x0 until the
            // ComboBox itself is activated. Click the visible ComboBox bounds
            // first, then resolve the live editor again before locating its arrow.
            var cellBounds = target.BoundingRectangle;
            if (cellBounds.Width <= 0 || cellBounds.Height <= 0 || target.Properties.IsOffscreen.ValueOrDefault)
                throw new AutomationException("GRID_OFFSCREEN", "Grid cell has no visible native bounds.");
            MarkWorkflowInput();
            NativeKeyboard.Click(cellBounds.X + cellBounds.Width / 2, cellBounds.Y + cellBounds.Height / 2,
                identity!.WindowHandle, identity.ProcessId, KeyboardHandle(target));
            Thread.Sleep(50);
            target = ResolveCell(cell, preserveComboBox: true);
            editor = WidestEditor(target) ?? target;
            FocusTarget(editor, activateGridCell: true, token: token);
            AutomationElement[] dropdowns = [];
            for (var attempt = 0; attempt < 12 && dropdowns.Length != 1; attempt++)
            {
                target = ResolveCell(cell, preserveComboBox: true);
                var bounds = target.BoundingRectangle;
                dropdowns = Snapshots(target, Live(automation.ConditionFactory.ByControlType(ControlType.Button), identity!.ProcessId))
                    .Where(button => button.HasArea && button.Bounds.Y <= bounds.Bottom && button.Bounds.Bottom >= bounds.Y
                        && (button.AutomationId == "[Editor] dropdown button" || button.Bounds.Right >= bounds.Right - 8))
                    .Select(button => button.Element)
                    .Where(button => button.Patterns.Invoke.IsSupported)
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
                    identity!.WindowHandle, identity.ProcessId, KeyboardHandle(dropdowns[0]));
                epoch++;
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được mở danh sách ĐVT.", true, error);
            }
            var host = automation.FromHandle(identity!.WindowHandle);
            var gridWindow = KeyboardHandle(target);
            AutomationElement[] registered = [];
            AutomationElement[] options = [];
            var expectedName = NormalizeOptionName(expected);
            // A slow MISA can open the list after the first polls; keep waiting while nothing is registered yet.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                token.ThrowIfCancellationRequested();
                registered = RegisteredOptions(host, cellBounds, gridWindow);
                // R20 can expose the same ValueList row more than once through
                // UIA, and can populate the list in more than one pass. Read
                // each name once, normalize invisible formatting, then keep a
                // single representative for each logical option.
                options = registered.Where(item => item.Properties.IsEnabled.ValueOrDefault
                    && !item.Properties.IsOffscreen.ValueOrDefault && item.Patterns.SelectionItem.IsSupported)
                    .Select(item => (Element: item, Name: NormalizeOptionName(item.Name)))
                    .Where(item => item.Name == expectedName)
                    .GroupBy(item => item.Name, StringComparer.Ordinal)
                    // Prefer the copy UIA hit-tests at its own point: a stale copy can lie under the open list.
                    .Select(group => group.Select(item => item.Element).FirstOrDefault(ShowsAt) ?? group.First().Element)
                    .ToArray();
                if (options.Length > 0 || registered.Length > 0 && attempt >= 11) break;
                Thread.Sleep(50);
            }
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
                NativeKeyboard.PressFocused(identity.WindowHandle, identity.ProcessId, "Escape", token, gridWindow);
                epoch++;
                var toggleCloseSent = false;
                for (var attempt = 0; attempt < 24; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    registered = RegisteredOptions(host, cellBounds, gridWindow);
                    if (registered.Length == 0) break;

                    // R20 can leave the Infragistics ValueList focused after
                    // Escape. Re-resolve the same arrow and toggle it closed
                    // only after a fresh readback still proves the list open.
                    if (!toggleCloseSent && attempt == 3)
                    {
                        var closeTarget = ResolveCell(cell, preserveComboBox: true);
                        var closeBounds = closeTarget.BoundingRectangle;
                        var closeDropdowns = Snapshots(closeTarget,
                                Live(automation.ConditionFactory.ByControlType(ControlType.Button), identity.ProcessId))
                            .Where(button => button.HasArea && button.Bounds.Y <= closeBounds.Bottom
                                && button.Bounds.Bottom >= closeBounds.Y
                                && (button.AutomationId == "[Editor] dropdown button"
                                    || button.Bounds.Right >= closeBounds.Right - 8))
                            .Select(button => button.Element)
                            .Where(button => button.Patterns.Invoke.IsSupported)
                            .ToArray();
                        if (closeDropdowns.Length == 1)
                        {
                            try
                            {
                                MarkWorkflowInput();
                                var closeArrowBounds = closeDropdowns[0].BoundingRectangle;
                                NativeKeyboard.Click(closeArrowBounds.X + closeArrowBounds.Width / 2,
                                    closeArrowBounds.Y + closeArrowBounds.Height / 2,
                                    identity.WindowHandle, identity.ProcessId, KeyboardHandle(closeDropdowns[0]));
                                epoch++;
                                toggleCloseSent = true;
                            }
                            catch (Exception error)
                            {
                                throw new AutomationException("INPUT_UNCERTAIN",
                                    "Không xác nhận được đóng danh sách ĐVT sau Escape.", true, error);
                            }
                        }
                    }
                    Thread.Sleep(50);
                }
                if (registered.Length != 0)
                    throw new AutomationException("GRID_DROPDOWN_NOT_CLOSED",
                        "Registered unit list remained open after Escape and close-toggle.", true);
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
                // The list can close between the poll and the click; never click through it onto the grid.
                NativeKeyboard.ClickOpenList(optionBounds.X + optionBounds.Width / 2,
                    optionBounds.Y + optionBounds.Height / 2, identity!.ProcessId, hit => OutsideGrid(hit, gridWindow));
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

    private bool ShowsAt(AutomationElement item)
    {
        try
        {
            var bounds = item.BoundingRectangle;
            var at = automation.FromPoint(new System.Drawing.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            for (var depth = 0; at is not null && depth < 4; depth++, at = at.Parent)
                if (at.Equals(item)) return true;
        }
        catch (Exception error) when (error is COMException or TimeoutException
            or FlaUI.Core.Exceptions.ElementNotAvailableException) { }
        return false;
    }

    private static async Task<Readback> VerifyWrittenAsync(Func<Task<Readback>> read, string expected,
        NativeValueKind kind, string cultureName, CancellationToken token, Func<Readback, bool>? selected = null)
    {
        try
        {
            bool Ok(Readback actual) => selected?.Invoke(actual)
                ?? NativeComparison.Matches(expected, actual.Value, kind, CultureInfo.GetCultureInfo(cultureName));
            var last = await NativeWait.UntilAsync(read, Ok,
                "READBACK_MISMATCH", "Native readback chưa khớp giá trị vừa nhập.", token, returnLastMismatch: true);
            if (Ok(last)) return last;
            // Name what MISA shows: report 0.2.200 (HĐ 18523, KKKNT) gave no value to work from.
            throw new AutomationException("READBACK_MISMATCH",
                $"Native readback chưa khớp giá trị vừa nhập: MISA hiện '{last.Value}' ({last.Source}), cần '{expected}'.", true);
        }
        catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", error.Message, true, error); }
    }

    private static string NormalizeOptionName(string? value) => TextKey.Canonical(value);

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
            // The add-new row shows a prompt instead of data.
            if (values.Values.All(value => string.IsNullOrWhiteSpace(value) || IsCustomNewRowPrompt(value))) continue;
            rows.Add(values);
            sources.Add(rowSources);
        }
        return new NativeTable(rows, Capture(), sources);
    }

    private (string Value, string Source) ReadCustomCellText(AutomationElement cell, string column)
    {
        var unit = string.Equals(column, UnitColumnCaption, StringComparison.Ordinal);
        // Value-list cells give their data value through ValuePattern: a GUID for ĐVT, and for "% thuế GTGT" a number
        // that reads like the rate except for KCT/KKKNT (report 0.2.200, HĐ 18523). The label shown is in DisplayText.
        if (unit || string.Equals(column, TaxRateColumnCaption, StringComparison.Ordinal))
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
        }
        if (unit)
        {
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
                throw new AutomationException("GRID_ROW_AMBIGUOUS", $"Mã hàng '{cell.ExpectedCode}' xuất hiện ở nhiều dòng native: "
                    + string.Join(", ", matches.Select(DescribeRow)) + ".");
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
                throw new AutomationException("GRID_ROW_CHANGED",
                    $"Mã hàng ở dòng đích đã thay đổi (đọc được '{code}', cần '{cell.ExpectedCode.Trim()}'); không nhập tiếp.");
            }
        }
        if (requireEmptyCode && !string.IsNullOrWhiteSpace(Code(row)))
            throw new AutomationException("GRID_ROW_CHANGED", "The insertion row is no longer empty; refusing to overwrite its code.");
        return row;
    }
    private static string DescribeRow(AutomationElement row)
    {
        try
        {
            var bounds = row.BoundingRectangle;
            return $"#{row.Properties.AutomationId.ValueOrDefault} {(row.Properties.IsEnabled.ValueOrDefault ? "bật" : "tắt")} "
                + $"[{bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}]";
        }
        catch (Exception error) when (error is COMException or TimeoutException
            or FlaUI.Core.Exceptions.ElementNotAvailableException) { return "#?"; }
    }

    private AutomationElement DisplayCell(AutomationElement target)
    {
        if (target.ControlType != ControlType.ComboBox) return target;
        var bounds = target.BoundingRectangle;
        return Snapshots(target, Live(automation.ConditionFactory.ByControlType(ControlType.Edit), identity!.ProcessId))
            .Where(item => item.HasArea
                && item.Bounds.X >= bounds.X - 2
                && item.Bounds.Right <= bounds.Right + 2
                && item.Bounds.Y >= bounds.Y - 2
                && item.Bounds.Bottom <= bounds.Bottom + 2)
            .OrderByDescending(item => item.Bounds.Width)
            .FirstOrDefault()?.Element ?? target;
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

    private static bool IsCustomNewRowPrompt(string text)
        => string.Equals(text.Normalize(NormalizationForm.FormC).Trim(), "Bấm vào đây để thêm mới",
            StringComparison.OrdinalIgnoreCase);

    // dataRowsOnly drops the -1 add-new row; onscreenOnly drops rows clipped by the
    // viewport, whose cells have no stable geometry to map.
    private CustomGridLayout ReadCustomGridLayout(AutomationElement grid, CancellationToken token,
        IReadOnlyList<string>? requestedColumns = null, NativeCell? rowTarget = null,
        bool waitForCode = false, bool requireEmptyCode = false,
        bool dataRowsOnly = false, bool onscreenOnly = false, bool allowEmpty = false)
    {
        var headers = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(ControlType.HeaderItem),
                identity!.ProcessId, enabled: false, onscreen: false))
            .Select(element => new CustomColumn(element.Name, element.Bounds.X,
                element.Bounds.X + element.Bounds.Width, element.Element))
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

        var rows = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(ControlType.DataItem),
                identity!.ProcessId, onscreen: false))
            .Where(element => element.HasArea && IsLogicalCustomRow(element)
                && (!dataRowsOnly || element.AutomationId != "-1") && (!onscreenOnly || !element.Offscreen))
            // One screen rectangle is one grid row: a second provider lying exactly over a row (same bounds) is not
            // another line. Keep the committed id so the code lookup and row ordinals stay unique.
            .GroupBy(element => element.Bounds)
            .Select(group => group.OrderBy(element => element.AutomationId == "-1" ? 1 : 0).First())
            .OrderBy(element => element.Bounds.Y).Select(element => element.Element).ToArray();
        if (rows.Length == 0 && !allowEmpty)
            throw new AutomationException("GRID_ROWS_INVALID", "Không có dòng native của bảng.");
        if (rowTarget is not null)
            rows = [SelectCustomRow(rows, headers, rowTarget, waitForCode, requireEmptyCode, token)];
        return new CustomGridLayout(headers, rows.Select(row => new CustomRow(row, MapCustomCells(row, headers, token))).ToArray());
    }

    private static bool IsLogicalCustomRow(ElementSnapshot row)
    {
        // R20 uses -1 for the enabled insertion row even when committed rows
        // already have numeric IDs. The disabled -1 row at the bottom is the
        // grid's placeholder and is excluded by the caller's enabled check.
        return row.AutomationId != "-1" || row.Enabled;
    }

    private IReadOnlyList<AutomationElement> MapCustomCells(AutomationElement row,
        IReadOnlyList<CustomColumn> columns, CancellationToken token)
    {
        var cells = Snapshots(row, Live(AnyControlType(ControlType.Edit, ControlType.ComboBox, ControlType.Text),
                row.Properties.ProcessId.ValueOrDefault, enabled: false, onscreen: false))
            .Where(element => element.HasArea)
            .GroupBy(element => element.Bounds)
            .Select(group => group.First()).ToArray();
        return columns.Select(column =>
        {
            token.ThrowIfCancellationRequested();
            // Rank by the share of the cell inside the column first: when the grid is scrolled sideways, a wide
            // scrolled cell (Tên hàng) lies under the frozen Mã hàng column and covers its header more than the
            // slightly inset code cell does (R20 phiếu nhập, report 0.2.143).
            var matches = cells.Select(cell => (cell, overlap: HorizontalOverlap(cell.Bounds.X,
                    cell.Bounds.X + cell.Bounds.Width, column.Left, column.Right)))
                .Where(item => item.overlap > 0)
                .OrderByDescending(item => Math.Round(item.overlap / Math.Max(1, item.cell.Bounds.Width), 2))
                .ThenByDescending(item => item.overlap)
                .ThenBy(item => Math.Abs(item.cell.Bounds.X - column.Left))
                .ToArray();
            if (matches.Length == 0)
                throw new AutomationException("GRID_COLUMNS_INVALID", "Không ánh xạ được ô native cho cột " + column.Name);
            return matches[0].cell.Element;
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
        // The exact cell must own focus before input; retry the click a few times before failing.
        for (var attempt = 1; ; attempt++)
        {
            try { FocusTargetOnce(target, activateGridCell, verifyFocus); return; }
            catch (AutomationException error) when (error.Code == "FOCUS_FAILED" && attempt < 3)
            {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(80 * attempt);
            }
        }
    }

    private void FocusTargetOnce(AutomationElement target, bool activateGridCell, bool verifyFocus)
    {
        ThrowIfBlockingDialog();
        var handle = KeyboardHandle(target);
        NativeKeyboard.Activate(handle, identity!.WindowHandle, identity.ProcessId);
        target.Focus();
        if (activateGridCell)
        {
            var clickTarget = target;
            if (target.ControlType == ControlType.ComboBox)
                clickTarget = WidestEditor(target) ?? target;
            var bounds = clickTarget.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0 || target.Properties.IsOffscreen.ValueOrDefault)
                throw new AutomationException("GRID_OFFSCREEN", "Grid cell has no visible native bounds.");
            MarkWorkflowInput();
            NativeKeyboard.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                identity!.WindowHandle, identity.ProcessId, KeyboardHandle(clickTarget));
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
        {
            // R20 "Mẫu số HĐ"/loại chứng từ: the inner editor refuses focus (UIA SetFocus throws), so the combo keeps
            // keyboard focus and takes the commit key itself.
            if (!inputTarget.Equals(target) && !inputTarget.Properties.IsKeyboardFocusable.ValueOrDefault)
                inputTarget = target;
            FocusTarget(inputTarget, activateGridCell: true, token: token, verifyFocus: !allowUnverifiedFocus);
        }
        var inputHandle = KeyboardHandle(inputTarget);
        if (!allowUnverifiedFocus && !IsFocusedTarget(inputTarget, inputHandle))
            throw new AutomationException("FOCUS_LOST", "The native editor lost focus before commit.");
        MarkWorkflowInput();
        if (allowUnverifiedFocus)
            NativeKeyboard.PressFocused(identity!.WindowHandle, identity.ProcessId, key, token, inputHandle);
        else
            NativeKeyboard.Press(inputHandle, identity!.WindowHandle, identity.ProcessId, key, token);
    }

    private AutomationElement ResolveKeyboardTarget(AutomationElement target)
    {
        if (target.ControlType != ControlType.ComboBox) return target;
        return WidestEditor(target) ?? target;
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
        if (!RequireProfile().Screens.TryGetValue("item_lookup", out var lookup)) return false;
        var grid = Snapshots(element, Live(automation.ConditionFactory.ByAutomationId(lookup.Grid("items").AutomationId), identity!.ProcessId))
            .Any(child => child.HasArea);
        return grid;
    }

    private void ThrowIfBlockingDialog()
    {
        if (identity is null) return;
        WhileMisaBusy(ThrowIfBlockingDialogOnce);
    }

    // MISA busy recalculating after a grid click (report 0.2.171, HĐ 125: ElementFromHandle timed out right after
    // the Số lượng click) answers UIA with UIA_E_TIMEOUT. Dialog probes only read, so wait for MISA to answer
    // instead of turning the input they follow into an uncertain row; nothing is sent again.
    private static void WhileMisaBusy(Action probe)
    {
        var deadline = Environment.TickCount64 + BusyProbeMilliseconds;
        while (true)
        {
            try { probe(); return; }
            catch (Exception error) when (IsUiaTimeout(error) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(250);
            }
        }
    }

    private const int BusyProbeMilliseconds = 20000;

    private static bool IsUiaTimeout(Exception error)
        => error is TimeoutException || error is COMException { HResult: unchecked((int)0x80131505) };

    private void ThrowIfBlockingDialogOnce()
    {
        if (identity is null || !MayHaveBlockingDialog()) return;
        var dialogs = HostWindows(identity.ProcessId)
            .Where(element => element.NativeWindowHandle != nint.Zero
                && element.NativeWindowHandle != identity.WindowHandle
                && !IsEditorScope(element.AutomationId))
            .Select(element => element.Element)
            // The new-item editor is itself a modal scope. While that scope is
            // bound, its own window must not be reported as an unrelated blocker.
            .Where(element => root is null || !element.Equals(root))
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
        }).ToArray();
        // MISA's own crash dialog: the process is out of memory and keeps failing until it is restarted.
        if (details.Any(detail => detail.Contains("OutOfMemoryException", StringComparison.OrdinalIgnoreCase)))
            throw new AutomationException("MISA_OUT_OF_MEMORY",
                "MISA báo hết bộ nhớ (OutOfMemoryException) khi vẽ danh sách chứng từ: " + string.Join("; ", details), true);
        throw new AutomationException("DIALOG_REQUIRES_ATTENTION",
            "MISA dialog đang chặn editor. Hãy xử lý dialog trước khi tiếp tục: " + string.Join("; ", details), true);
    }

    private void GuardWorkflow(NativeFrame frame, bool forInput = false)
    {
        EnsureFrame(frame);
        var current = Capture();
        if (current.Window.Dpi != frame.Window.Dpi || current.Window.ClientBounds != frame.Window.ClientBounds
            // Slow provider reads may take a minute; input requires a recent frame.
            || DateTimeOffset.UtcNow - frame.ObservedAt > TimeSpan.FromSeconds(forInput ? 10 : 60))
            throw new AutomationException("STALE_NATIVE_FRAME", "Frame/bounds/DPI đã thay đổi; cần quan sát mới.");
        if (!ScopeReady(frame))
        {
            // R20 briefly disables a scope while it processes a commit (combo lookups,
            // the new-item dialog opening/closing). Settle before deciding it changed.
            var deadline = Environment.TickCount64 + 1500;
            while (!ScopeReady(frame) && Environment.TickCount64 < deadline) Thread.Sleep(50);
            if (!ScopeReady(frame))
            {
                // A MISA message box is the usual reason; report it instead of a generic scope change.
                ThrowIfBlockingDialog();
                throw new AutomationException("SCOPE_IDENTITY_CHANGED",
                    "Cửa sổ MISA đang nhập đã đổi, bị ẩn hoặc bị khóa; không gửi input. Đóng hộp thoại MISA đang mở (nếu có) rồi bấm Tiếp tục.");
            }
        }
        // Cheap Win32 proof first; the full host scan below walks every grid cell.
        WhileMisaBusy(ThrowIfModalDialog);
    }

    private void ThrowIfModalDialog()
    {
        if (!MayHaveModalDialog()) return;
        var ancestors = new List<AutomationElement>();
        for (var parent = root!.Parent; parent is not null && parent.Properties.ProcessId.ValueOrDefault == identity!.ProcessId; parent = parent.Parent)
            ancestors.Add(parent);
        var dialogs = HostWindows(null).Select(element => element.Element)
            .Where(element => element.Patterns.Window.IsSupported
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
            }).ToArray();
            throw new AutomationException(details.Any(detail => detail.Contains("OutOfMemoryException", StringComparison.OrdinalIgnoreCase))
                    ? "MISA_OUT_OF_MEMORY" : "DIALOG_REQUIRES_ATTENTION",
                "MISA modal dialog requires attention. Dialog: " + string.Join("; ", details));
        }
    }

    private bool ScopeReady(NativeFrame frame)
        => root!.Properties.IsEnabled.ValueOrDefault && !root.Properties.IsOffscreen.ValueOrDefault
            && root.Properties.ProcessId.ValueOrDefault == identity!.ProcessId
            && root.Name == frame.TabIdentity;

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

    private (string Value, string Source) ReadDisplayText(AutomationElement element)
    {
        if (element.ControlType == ControlType.ComboBox)
        {
            var selected = element.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.ListItem))
                .Where(item => item.Patterns.SelectionItem.IsSupported
                    && item.Patterns.SelectionItem.Pattern.IsSelected.Value
                    && !string.IsNullOrWhiteSpace(item.Name))
                .ToArray();
            if (selected.Length == 1)
                return (selected[0].Name, "UIA3.SelectionItem+DisplayText");
        }
        return ReadNativeText(element);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutText(nint hwnd, uint message, nuint wparam, StringBuilder lparam, uint flags, uint timeout, out nuint result);
}
