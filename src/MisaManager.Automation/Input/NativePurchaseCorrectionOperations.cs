using FlaUI.Core.AutomationElements;
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
            var editors = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(FlaUI.Core.Definitions.ControlType.Edit),
                    identity!.ProcessId, enabled: false, onscreen: false))
                .Where(item => item.HasArea && item.Bounds.Left < header.Right && item.Bounds.Right > header.Left
                    && item.Bounds.Top >= header.Bottom - 2)
                .OrderBy(item => item.Bounds.Top).ToArray();
            if (editors.Length == 0)
                throw new AutomationException("TARGET_NOT_FOUND", $"Chưa thấy ô lọc dưới cột '{column}'.");
            var target = editors[0].Element;
            var sent = false;
            try
            {
                SetNativeValue(target, text, token, ref sent);
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
            var layout = ReadCustomGridLayout(grid, token, [column], dataRowsOnly: true, allowEmpty: true);
            var matches = layout.Rows.Where(row =>
            {
                var cell = row.Cells[FindCustomColumn(layout.Columns, column)];
                return string.Equals(ReadCustomCellText(cell, column).Value?.Trim(), expected.Trim(), StringComparison.Ordinal);
            }).ToArray();
            if (matches.Length != 1)
                throw new AutomationException(matches.Length == 0 ? "TARGET_NOT_FOUND" : "TARGET_AMBIGUOUS",
                    matches.Length == 0 ? $"Không thấy dòng có {column} = '{expected}'." : $"Có nhiều dòng có {column} = '{expected}'.");
            var bounds = matches[0].Element.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new AutomationException("BOUNDS_UNREADABLE", "Dòng hóa đơn không có bounds để double-click.");
            try
            {
                MarkWorkflowInput();
                NativeKeyboard.DoubleClick(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                    identity!.WindowHandle, identity.ProcessId, KeyboardHandle(matches[0].Element));
                epoch++;
                return new Readback("sent_unverified", expected, "Win32.DoubleClick", DateTimeOffset.UtcNow,
                    "Đã mở dòng; chờ editor MISA xuất hiện.", Capture());
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được double-click dòng hóa đơn.", true, error);
            }
        }, token, maySendInput: true);
}
