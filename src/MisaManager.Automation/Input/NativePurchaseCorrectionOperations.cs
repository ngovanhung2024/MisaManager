using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed partial class NativeInputService
{
    internal const string EmptyPurchaseRegister = "empty_purchase_register";

    /// <summary>Writes a register filter editor below a grid column header and commits it with Enter.</summary>
    public Task<Readback> WriteGridFilterAsync(NativeFrame frame, string gridId, string column, string text,
        CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var grid = ResolveGrid(gridId);
            var target = ResolveRegisterFilter(grid, column);
            var sent = false;
            try
            {
                // The real R20 filter keeps an embedded Edit inside a ComboBox.
                // ValuePattern.SetValue can update the child text without
                // notifying the grid, so replace the focused text as a user
                // would and commit through the owning control.
                FocusTarget(target, activateGridCell: true, token: token);
                var inputTarget = ResolveKeyboardTarget(target);
                var inputHandle = KeyboardHandle(inputTarget);
                if (!IsFocusedTarget(inputTarget, inputHandle))
                    throw new AutomationException("FOCUS_FAILED", "Ô lọc số hóa đơn chưa nhận focus.");
                sent = true;
                MarkWorkflowInput();
                NativeKeyboard.ReplaceText(inputHandle, identity!.WindowHandle, identity.ProcessId, text, token);
                // Keep the editor focused after typing. Re-resolving and clicking the grid cell here makes R20
                // restore its previous filter (for example 205) before the Enter reaches the active editor.
                MarkWorkflowInput();
                NativeKeyboard.Press(inputHandle, identity!.WindowHandle, identity.ProcessId, "Enter", token);
                // Some R20 builds repaint the filter editor once after the first commit. The second Enter is sent
                // to the same still-focused editor, without clicking or resolving the cell again.
                Thread.Sleep(50);
                MarkWorkflowInput();
                NativeKeyboard.Press(inputHandle, identity!.WindowHandle, identity.ProcessId, "Enter", token);
                epoch++;
                return new Readback("sent_unverified", text, "Win32.SendInput+Enter", DateTimeOffset.UtcNow,
                    "Đã lọc; sẽ xác minh bằng cách đọc lại dòng hóa đơn.", Capture());
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được ô lọc Số hóa đơn.", sent, error);
            }
        }, token, maySendInput: true);

    private AutomationElement ResolveRegisterFilter(AutomationElement grid, string column)
    {
        var headers = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(ControlType.HeaderItem),
                identity!.ProcessId, enabled: false, onscreen: false))
            .Where(item => item.HasArea && string.Equals(item.Name, column, StringComparison.Ordinal)).ToArray();
        if (headers.Length != 1)
            throw new AutomationException("GRID_COLUMN_AMBIGUOUS", $"Không xác định duy nhất cột lọc '{column}'.");
        var header = headers[0].Bounds;
        var candidates = Snapshots(grid, Live(AnyControlType(ControlType.ComboBox, ControlType.Edit),
                identity.ProcessId, enabled: false, onscreen: false))
            .Where(item => item.HasArea && item.Bounds.Top >= header.Bottom - 2).ToArray();
        var centered = candidates.Where(item =>
        {
            var center = item.Bounds.Left + item.Bounds.Width / 2;
            return center >= header.Left - 2 && center <= header.Right + 2;
        }).ToArray();
        // Prefer the ComboBox owning the filter so Enter reaches the grid.
        var editor = (centered.Length > 0 ? centered : candidates)
            .OrderByDescending(item => item.ControlType == ControlType.ComboBox)
            .ThenBy(item => item.Bounds.Top).FirstOrDefault();
        return editor?.Element ?? throw new AutomationException("TARGET_NOT_FOUND", $"Chưa thấy ô lọc dưới cột '{column}'.");
    }

    /// <summary>Opens the verified row, or reports a confirmed empty register without clicking it.</summary>
    public Task<Readback> DoubleClickGridRowAsync(NativeFrame frame, string gridId, string column, string expected,
        CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var grid = ResolveGrid(gridId);
            var wanted = expected.Trim();
            var gridHandle = KeyboardHandle(grid);
            (int X, int Y, string Source)? match = null;
            var observedRowCounts = new List<int>();
            var observedValues = new List<string>();
            long? emptySince = null;
            // MISA redraws the filtered register asynchronously. Keep polling the
            // rows only. The visible row cells are selected with the mouse because
            // R20 can expose them outside the row's UIA subtree.
            for (var attempt = 0; attempt < 200 && match is null; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var layout = ReadCustomGridLayout(grid, token, dataRowsOnly: true, allowEmpty: true, mapCells: false);
                observedRowCounts.Add(layout.Rows.Count);
                if (layout.Rows.Count <= 1 && TryReadCustomFooterCount(grid) == 0)
                {
                    // R20 exposes a blank row provider in an empty list. Wait for the footer to settle because
                    // the next filter can initially still show the previous filter's zero count.
                    emptySince ??= Environment.TickCount64;
                    if (Environment.TickCount64 - emptySince.Value >= 1000)
                    {
                        GuardWorkflow(frame);
                        var filter = ResolveKeyboardTarget(ResolveRegisterFilter(grid, column));
                        var (text, filterSource) = ReadNativeText(filter);
                        if (!string.Equals(text.Trim(), wanted, StringComparison.Ordinal))
                            throw new AutomationException("INPUT_UNCERTAIN", $"Ô lọc '{column}' đọc được '{text}', cần '{wanted}'.", true);
                        if (TryReadCustomFooterCount(grid) == 0)
                            return new Readback(EmptyPurchaseRegister, text, filterSource + "+GridFooter", DateTimeOffset.UtcNow,
                                "MISA báo Số dòng = 0 sau khi lọc.", Capture());
                    }
                    Thread.Sleep(50);
                    continue;
                }
                emptySince = null;
                if (layout.Rows.Count == 1)
                {
                    var target = layout.Rows[0];
                    // Read the filtered row through the existing custom-grid cell reader. Selecting the row here
                    // moves the mouse away from the Số hóa đơn filter and can make R20 restore the old filter.
                    var targetColumn = layout.Columns[FindCustomColumn(layout.Columns, column)];
                    (string Value, string Source, int X, int Y) read;
                    try { read = ReadGridCellAtPoint(target.Element, targetColumn, token); }
                    catch (AutomationException error) when (error.Code is "GRID_CELL_READBACK" or "BOUNDS_UNREADABLE"
                        && TryReadCustomFooterCount(grid) == 0)
                    {
                        continue;
                    }
                    var actual = read.Value.Trim();
                    observedValues.Add(actual);
                    if (string.Equals(actual, wanted, StringComparison.Ordinal))
                    {
                        match = (read.X, read.Y, read.Source);
                        break;
                    }
                }
                if (match is null) Thread.Sleep(50);
            }
            if (match is null)
            {
                var last = observedValues.Count == 0 ? "" : observedValues[^1];
                throw new AutomationException("PURCHASE_NOT_FOUND",
                    $"Đã lọc '{wanted}' nhưng ô {column} vẫn đọc được '{last}' sau khi chờ grid redraw "
                    + $"(số dòng: {string.Join(",", observedRowCounts.Distinct())}; giá trị đã thấy: {string.Join(",", observedValues.Distinct())}).");
            }
            // The row provider can lose its live bounds after a redraw. Open at the point that was
            // verified inside the invoice cell, using the stable grid HWND to guard the click.
            GuardWorkflow(frame, forInput: true);
            var (x, y, source) = match.Value;
            token.ThrowIfCancellationRequested();
            try
            {
                MarkWorkflowInput();
                NativeKeyboard.DoubleClick(x, y, identity!.WindowHandle, identity.ProcessId, gridHandle);
                epoch++;
                return new Readback("sent_unverified", wanted, "Win32.DoubleClick", DateTimeOffset.UtcNow,
                    $"Ô tại ({x}, {y}) đọc được '{wanted}' qua {source}; chờ editor MISA xuất hiện.", Capture());
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được double-click dòng hóa đơn.", true, error);
            }
        }, token, maySendInput: true);

    private (string Value, string Source, int X, int Y) ReadGridCellAtPoint(AutomationElement row, CustomColumn column,
        CancellationToken token)
    {
        var rowBounds = row.BoundingRectangle;
        if (rowBounds.Width <= 0 || rowBounds.Height <= 0 || column.Right <= column.Left)
            throw new AutomationException("BOUNDS_UNREADABLE", "Dòng hoặc cột hóa đơn không có bounds để đọc.");
        var x = (int)Math.Round((column.Left + column.Right) / 2d);
        var y = (int)Math.Round((double)rowBounds.Top + rowBounds.Height / 2d);
        if (x < rowBounds.Left || x >= rowBounds.Right)
            throw new AutomationException("BOUNDS_UNREADABLE", $"Cột '{column.Name}' nằm ngoài dòng hóa đơn.");

        (string Value, string Source) last = ("", "none");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            token.ThrowIfCancellationRequested();
            foreach (var candidate in PointCandidates())
            {
                try
                {
                    seen.Add($"{candidate.ControlType} '{candidate.Properties.Name.ValueOrDefault}' [{candidate.BoundingRectangle.X},{candidate.BoundingRectangle.Y} {candidate.BoundingRectangle.Width}x{candidate.BoundingRectangle.Height}]");
                    var read = ReadPointText(candidate);
                    if (!string.IsNullOrWhiteSpace(read.Value)) return (read.Value, read.Source, x, y);
                    last = read;
                }
                catch (Exception error) when (error is AutomationException or COMException || Vanished(error)) { }
            }
            Thread.Sleep(50);
        }
        throw new AutomationException("GRID_CELL_READBACK",
            $"Đã đọc ô '{column.Name}' nhưng chưa có giá trị native (đọc được '{last.Value}'). Ứng viên: {string.Join(" | ", seen.Take(8))}.");

        AutomationElement[] PointCandidates()
        {
            var condition = Live(AnyControlType(ControlType.Edit, ControlType.ComboBox, ControlType.Text,
                ControlType.Custom), identity!.ProcessId, enabled: false, onscreen: false);
            var found = ProcessSnapshots(identity.WindowHandle, identity.ProcessId, condition)
                .Where(item => item.HasArea && item.Bounds.Contains(x, y)
                    && item.Bounds.Top <= rowBounds.Bottom && item.Bounds.Bottom >= rowBounds.Top)
                .OrderBy(item => item.Bounds.Width * item.Bounds.Height)
                .Select(item => item.Element)
                .ToList();
            var focused = automation.FocusedElement();
            if (focused is not null && TrySnapshot(focused) is { } focusedSnapshot
                && focusedSnapshot.ProcessId == identity.ProcessId
                && focusedSnapshot.HasArea && focusedSnapshot.Bounds.Contains(x, y)
                && focusedSnapshot.Bounds.Top <= rowBounds.Bottom && focusedSnapshot.Bounds.Bottom >= rowBounds.Top)
                found.Insert(0, focused);
            return found.Distinct().ToArray();
        }
    }

    private static (string Value, string Source) ReadPointText(AutomationElement element)
    {
        if (element.ControlType == ControlType.Custom
            && !string.IsNullOrWhiteSpace(element.Properties.Name.ValueOrDefault))
            return (element.Properties.Name.ValueOrDefault!, "UIA3.Name");
        return ReadNativeText(element);
    }
}
