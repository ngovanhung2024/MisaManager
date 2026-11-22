using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed partial class NativeInputService
{
    /// <summary>Writes a register filter editor below a grid column header and commits it with Enter.</summary>
    public Task<Readback> WriteGridFilterAsync(NativeFrame frame, string gridId, string column, string text,
        CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var grid = ResolveGrid(gridId);
            var headers = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(FlaUI.Core.Definitions.ControlType.HeaderItem),
                    identity!.ProcessId, enabled: false, onscreen: false))
                .Where(item => item.HasArea && string.Equals(item.Name, column, StringComparison.Ordinal))
                .ToArray();
            if (headers.Length != 1)
                throw new AutomationException("GRID_COLUMN_AMBIGUOUS", $"Không xác định duy nhất cột lọc '{column}'.");
            var header = headers[0].Bounds;
            var candidates = Snapshots(grid, Live(AnyControlType(FlaUI.Core.Definitions.ControlType.ComboBox,
                    FlaUI.Core.Definitions.ControlType.Edit), identity!.ProcessId, enabled: false, onscreen: false))
                .Where(item => item.HasArea && item.Bounds.Top >= header.Bottom - 2)
                .ToArray();
            var centered = candidates.Where(item =>
            {
                var center = item.Bounds.Left + item.Bounds.Width / 2;
                return center >= header.Left - 2 && center <= header.Right + 2;
            }).ToArray();
            var editors = (centered.Length > 0 ? centered : candidates)
                // R20 exposes the register filter as a ComboBox with an embedded
                // Edit. Use the parent when available so Enter reaches the grid
                // filter, rather than only the child editor.
                .OrderByDescending(item => item.ControlType == FlaUI.Core.Definitions.ControlType.ComboBox)
                .ThenBy(item => item.Bounds.Top).ToArray();
            if (editors.Length == 0)
                throw new AutomationException("TARGET_NOT_FOUND", $"Chưa thấy ô lọc dưới cột '{column}'.");
            var target = editors[0].Element;
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
                Commit(target, "Enter", token);
                epoch++;
                return new Readback("sent_unverified", text, "UIA3.ValuePattern+Enter", DateTimeOffset.UtcNow,
                    "Đã lọc; sẽ xác minh bằng cách đọc lại dòng hóa đơn.", Capture());
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được ô lọc Số hóa đơn.", sent, error);
            }
        }, token, maySendInput: true);

    /// <summary>Finds a visible register row by a column value and double-clicks that row.</summary>
    public Task<Readback> DoubleClickGridRowAsync(NativeFrame frame, string gridId, string column, string expected,
        CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            GuardWorkflow(frame, forInput: true);
            var grid = ResolveGrid(gridId);
            var wanted = expected.Trim();
            CustomRow? match = null;
            var observedRowCounts = new List<int>();
            // MISA redraws the filtered register asynchronously. Keep polling the
            // rows only. The visible row cells are selected with the mouse because
            // R20 can expose them outside the row's UIA subtree.
            for (var attempt = 0; attempt < 40 && match is null; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var layout = ReadCustomGridLayout(grid, token, dataRowsOnly: true, allowEmpty: true, mapCells: false);
                observedRowCounts.Add(layout.Rows.Count);
                if (layout.Rows.Count == 1)
                {
                    var target = layout.Rows[0];
                    var targetColumn = layout.Columns[FindCustomColumn(layout.Columns, column)];
                    var read = ReadGridCellByMouse(target.Element, targetColumn, token);
                    if (!string.Equals(read.Value.Trim(), wanted, StringComparison.Ordinal))
                        throw new AutomationException("PURCHASE_NOT_FOUND",
                            $"Đã chọn dòng bằng chuột nhưng ô {column} hiện '{read.Value.Trim()}', cần '{wanted}'.");
                    match = target;
                    break;
                }
                Thread.Sleep(50);
            }
            if (match is null)
                throw new AutomationException("TARGET_NOT_FOUND", $"Không thấy duy nhất dòng có {column} = '{wanted}' (số dòng đọc được: {string.Join(",", observedRowCounts.Distinct())}).");
            var bounds = match.Element.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new AutomationException("BOUNDS_UNREADABLE", "Dòng hóa đơn không có bounds để double-click.");
            try
            {
                MarkWorkflowInput();
                NativeKeyboard.DoubleClick(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                    identity!.WindowHandle, identity.ProcessId, KeyboardHandle(match.Element));
                epoch++;
                return new Readback("sent_unverified", wanted, "Win32.DoubleClick", DateTimeOffset.UtcNow,
                    "Đã mở dòng; chờ editor MISA xuất hiện.", Capture());
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được double-click dòng hóa đơn.", true, error);
            }
        }, token, maySendInput: true);

    private (string Value, string Source) ReadGridCellByMouse(AutomationElement row, CustomColumn column,
        CancellationToken token)
    {
        var rowBounds = row.BoundingRectangle;
        if (rowBounds.Width <= 0 || rowBounds.Height <= 0 || column.Right <= column.Left)
            throw new AutomationException("BOUNDS_UNREADABLE", "Dòng hoặc cột hóa đơn không có bounds để chọn bằng chuột.");
        var x = (int)Math.Round((column.Left + column.Right) / 2d);
        var y = (int)Math.Round((double)rowBounds.Top + rowBounds.Height / 2d);
        if (x < rowBounds.Left || x >= rowBounds.Right)
            throw new AutomationException("BOUNDS_UNREADABLE", $"Cột '{column.Name}' nằm ngoài dòng hóa đơn.");
        try
        {
            MarkWorkflowInput();
            NativeKeyboard.Click(x, y, identity!.WindowHandle, identity.ProcessId, KeyboardHandle(row));
            epoch++;
        }
        catch (Exception error)
        {
            throw new AutomationException("INPUT_UNCERTAIN", $"Không chọn được ô '{column.Name}' bằng chuột.", true, error);
        }

        (string Value, string Source) last = ("", "none");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            token.ThrowIfCancellationRequested();
            foreach (var candidate in PointCandidates(x, y))
            {
                try
                {
                    seen.Add($"{candidate.ControlType} '{candidate.Properties.Name.ValueOrDefault}' [{candidate.BoundingRectangle.X},{candidate.BoundingRectangle.Y} {candidate.BoundingRectangle.Width}x{candidate.BoundingRectangle.Height}]");
                    var read = ReadPointText(candidate);
                    if (!string.IsNullOrWhiteSpace(read.Value)) return read;
                    last = read;
                }
                catch (Exception error) when (error is AutomationException or COMException || Vanished(error)) { }
            }
            Thread.Sleep(50);
        }
        throw new AutomationException("GRID_CELL_READBACK",
            $"Đã click ô '{column.Name}' nhưng chưa đọc lại được giá trị native (đọc được '{last.Value}'). Ứng viên: {string.Join(" | ", seen.Take(8))}.");

        AutomationElement[] PointCandidates(int pointX, int pointY)
        {
            var condition = Live(AnyControlType(ControlType.Edit, ControlType.ComboBox, ControlType.Text,
                ControlType.Custom), identity!.ProcessId, enabled: false, onscreen: false);
            var found = ProcessSnapshots(identity.WindowHandle, identity.ProcessId, condition)
                .Where(item => item.HasArea && item.Bounds.Contains(pointX, pointY)
                    && item.Bounds.Top <= rowBounds.Bottom && item.Bounds.Bottom >= rowBounds.Top)
                .OrderBy(item => item.Bounds.Width * item.Bounds.Height)
                .Select(item => item.Element)
                .ToList();
            var focused = automation.FocusedElement();
            if (focused is not null && TrySnapshot(focused) is { } focusedSnapshot
                && focusedSnapshot.ProcessId == identity.ProcessId
                && focusedSnapshot.HasArea && focusedSnapshot.Bounds.Contains(pointX, pointY)
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
