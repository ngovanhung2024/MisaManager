using System.Text.Json;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed partial class NativeInputService
{
    // Complete reconciliation must not equate a viewport with the whole table.
    public async Task<NativeTable> ReadCompleteTableAsync(NativeFrame frame, string gridId,
        IReadOnlyList<string> columns, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        token = deadline.Token;
        var indexed = await dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame);
            var grid = ResolveGrid(gridId);
            return grid.Patterns.Grid.IsSupported && grid.Patterns.Table.IsSupported;
        }, token);
        if (indexed) return await ReadTableAsync(frame, gridId, columns, token);

        // R20's Infragistics register exposes every row as a UIA DataItem but
        // omits GridPattern and ScrollPattern. Validate the native row ordinal
        // range and the embedded DataRowCount before treating that tree as a
        // complete table.
        var exposedRowCount = await dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame);
            var grid = ResolveGrid(gridId);
            return TryCustomDataRowCount(grid, token);
        }, token);
        if (exposedRowCount is { } count)
        {
            string? previous = null;
            return await NativeWait.UntilAsync(() => dispatcher.RunAsync(() =>
            {
                GuardWorkflow(frame);
                var grid = ResolveGrid(gridId);
                var table = ReadCustomCompleteTable(grid, columns, count, token);
                return table with { Sources = table.Sources.Select(row => (IReadOnlyDictionary<string, string>)
                    row.ToDictionary(value => value.Key, value => value.Value + "+CompleteCustomTree")).ToArray() };
            }, token), table =>
            {
                var fingerprint = JsonSerializer.Serialize(table.Rows);
                var stable = previous == fingerprint;
                previous = fingerprint;
                return stable;
            }, "GRID_NOT_STABLE", "Native register rows did not settle.", token);
        }

        var terminal = await dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame);
            var grid = ResolveGrid(gridId);
            return !grid.Patterns.Scroll.IsSupported && HasVisibleTerminalRows(grid, token);
        }, token);
        if (terminal)
        {
            string? previous = null;
            return await NativeWait.UntilAsync(() => dispatcher.RunAsync(() =>
            {
                GuardWorkflow(frame);
                var grid = ResolveGrid(gridId);
                if (!HasVisibleTerminalRows(grid, token))
                    throw new AutomationException("GRID_INCOMPLETE", "Native terminal row proof changed during reconciliation.");
                var table = ReadCustomTable(grid, columns, token);
                return table with { Sources = table.Sources.Select(row => (IReadOnlyDictionary<string, string>)
                    row.ToDictionary(value => value.Key, value => value.Value + "+VisibleOrdinalTerminal")).ToArray() };
            }, token), table =>
            {
                var fingerprint = JsonSerializer.Serialize(table.Rows);
                var stable = previous == fingerprint;
                previous = fingerprint;
                return stable;
            }, "GRID_NOT_STABLE", "Native terminal rows did not settle.", token);
        }

        frame = await ScrollTableAsync(frame, gridId, top: true, token);
        var rows = new SortedDictionary<int, IReadOnlyDictionary<string, string>>();
        var sources = new SortedDictionary<int, IReadOnlyDictionary<string, string>>();
        var previousPosition = -1d;
        for (var page = 0; page < 128; page++)
        {
            token.ThrowIfCancellationRequested();
            string? previousRead = null;
            var stable = await NativeWait.UntilAsync(async () =>
            {
                var read = await ReadTablePageAsync(frame, gridId, columns, token);
                frame = read.Table.Frame;
                return read;
            }, read =>
            {
                var fingerprint = JsonSerializer.Serialize(new { read.Position, read.Indices, read.Table.Rows });
                var same = previousRead == fingerprint;
                previousRead = fingerprint;
                return same && (page == 0 ? read.Position is 0 or -1 : read.Position > previousPosition);
            }, "GRID_NOT_STABLE", "Native grid did not expose a stable page after scrolling.", token);

            if (stable.Position == -1)
            {
                if (page != 0) throw new AutomationException("GRID_CHANGED", "Native scroll range changed during traversal.");
                return stable.Table;
            }
            for (var index = 0; index < stable.Indices.Count; index++)
            {
                var ordinal = stable.Indices[index];
                var row = stable.Table.Rows[index];
                if (rows.TryGetValue(ordinal, out var seen)
                    && columns.Any(column => seen[column] != row[column]))
                    throw new AutomationException("GRID_CHANGED", "A native row changed during table traversal.");
                rows[ordinal] = row;
                sources[ordinal] = stable.Table.Sources[index];
            }
            if (rows.Count > 5000) throw new AutomationException("GRID_LIMIT", "Native table exceeds 5000 rows.");
            if (stable.Position >= 100)
            {
                if (rows.Count == 0 || !rows.Keys.SequenceEqual(Enumerable.Range(0, rows.Count)))
                    throw new AutomationException("GRID_INCOMPLETE", "Native row ordinals contain gaps; reconciliation is incomplete.");
                return new NativeTable(rows.Values.ToArray(), frame, sources.Values.ToArray());
            }
            previousPosition = stable.Position;
            frame = await ScrollTableAsync(frame, gridId, top: false, token);
        }
        throw new AutomationException("GRID_LIMIT", "Native table traversal exceeded 128 pages.");
    }

    private int? TryCustomDataRowCount(FlaUI.Core.AutomationElements.AutomationElement grid,
        CancellationToken token)
    {
        if (grid.Patterns.Grid.IsSupported || grid.Patterns.Table.IsSupported || grid.Patterns.Scroll.IsSupported)
            return null;
        var exposed = Snapshots(grid, TrueCondition.Default);
        var declared = exposed.Select(item => TryParseDataRowCount(item.Element.Properties.ItemStatus.ValueOrDefault))
            .FirstOrDefault(value => value is not null);
        if (declared is { } declaredCount)
        {
            var ids = exposed.Where(item => item.ControlType == ControlType.DataItem
                    && int.TryParse(item.AutomationId, out var ordinal) && ordinal >= 0)
                .Select(item => int.Parse(item.AutomationId, System.Globalization.CultureInfo.InvariantCulture))
                .Distinct().OrderBy(value => value).ToArray();
            if (ids.Length == declaredCount && ids.SequenceEqual(Enumerable.Range(0, declaredCount)))
                return declaredCount;
        }
        var regions = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(ControlType.Tree),
            identity!.ProcessId, enabled: false, onscreen: false));
        foreach (var region in regions)
        {
            token.ThrowIfCancellationRequested();
            if (TryParseDataRowCount(region.Element.Properties.ItemStatus.ValueOrDefault) is not { } count)
                continue;
            var rows = NumericRows(region.Element);
            if (rows.Length == count && rows.Select(row => row.Ordinal)
                    .SequenceEqual(Enumerable.Range(0, count))) return count;
        }
        var numericRows = NumericRows(grid);
        if (numericRows.Length > 0 && numericRows.Select(row => row.Ordinal)
                .SequenceEqual(Enumerable.Range(0, numericRows[^1].Ordinal + 1)))
            return numericRows.Length;
        return null;
    }

    private static int? TryParseDataRowCount(string? status)
    {
        const string marker = "DataRowCount^IG_TOKEN_SEPERATOR^";
        var start = (status ?? "").IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = status!.IndexOf("^IG_TOKEN_SEPERATOR^", start, StringComparison.Ordinal);
        if (end < 0) end = status.IndexOf("^IG_TOKEN_DELIMETER^", start, StringComparison.Ordinal);
        var value = end < 0 ? status[start..] : status[start..end];
        return int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var count) && count is >= 0 and <= 5000
            ? count : null;
    }

    private (int Ordinal, FlaUI.Core.AutomationElements.AutomationElement Element)[] NumericRows(
        FlaUI.Core.AutomationElements.AutomationElement scope)
        => Snapshots(scope, Live(automation.ConditionFactory.ByControlType(ControlType.DataItem),
                identity!.ProcessId, enabled: false, onscreen: false))
            .Where(row => int.TryParse(row.AutomationId, out var ordinal) && ordinal >= 0)
            .Select(row => (Ordinal: int.Parse(row.AutomationId, System.Globalization.CultureInfo.InvariantCulture), row.Element))
            .GroupBy(row => row.Ordinal).Where(group => group.Count() == 1)
            .Select(group => group.Single()).OrderBy(row => row.Ordinal).ToArray();

    private NativeTable ReadCustomCompleteTable(FlaUI.Core.AutomationElements.AutomationElement grid,
        IReadOnlyList<string> requestedColumns, int expectedRowCount, CancellationToken token)
    {
        var headers = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(ControlType.HeaderItem),
                identity!.ProcessId, enabled: false, onscreen: false))
            .Select(element => new CustomCompleteColumn(element.Name,
                element.Bounds.X, element.Bounds.X + element.Bounds.Width, element.Element))
            .Where(column => !string.IsNullOrWhiteSpace(column.Name) && column.Right > column.Left)
            .GroupBy(column => column.Name, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(column => column.Right - column.Left).First())
            .OrderBy(column => column.Left).ToArray();
        if (headers.Length == 0)
            throw new AutomationException("GRID_COLUMNS_INVALID", "Không có tiêu đề cột native của bảng.");
        var requested = requestedColumns.ToHashSet(StringComparer.Ordinal);
        var missing = requested.Where(name => headers.All(column => column.Name != name)).ToArray();
        if (missing.Length > 0)
            throw new AutomationException("GRID_COLUMN_AMBIGUOUS", "Native grid is missing requested column(s): " + string.Join(", ", missing));
        headers = headers.Where(column => requested.Count == 0 || requested.Contains(column.Name)).ToArray();

        var rowScope = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(ControlType.Tree),
                identity!.ProcessId, enabled: false, onscreen: false))
            .Where(region => TryParseDataRowCount(region.Element.Properties.ItemStatus.ValueOrDefault) == expectedRowCount)
            .Select(region => (Scope: region.Element, Rows: NumericRows(region.Element)))
            .FirstOrDefault(candidate => candidate.Rows.Length == expectedRowCount
                && candidate.Rows.Select(row => row.Ordinal).SequenceEqual(Enumerable.Range(0, expectedRowCount)));
        var rows = rowScope.Scope is not null ? rowScope.Rows : NumericRows(grid);
        if (rows.Length != expectedRowCount
            || !rows.Select(row => row.Ordinal).SequenceEqual(Enumerable.Range(0, expectedRowCount)))
            throw new AutomationException("GRID_INCOMPLETE", "Native row ordinals do not cover the complete register.");

        var values = new List<IReadOnlyDictionary<string, string>>(rows.Length);
        var sources = new List<IReadOnlyDictionary<string, string>>(rows.Length);
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            var cells = Snapshots(row.Element, Live(AnyControlType(ControlType.Edit, ControlType.ComboBox, ControlType.Text),
                    identity!.ProcessId, enabled: false, onscreen: false))
                .Select(item => item.Element).ToArray();
            var rowValues = new Dictionary<string, string>(StringComparer.Ordinal);
            var rowSources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var column in headers)
            {
                var visible = cells.Select(cell => (Cell: cell, Bounds: cell.BoundingRectangle))
                    .Where(item => item.Bounds.Width > 0 && item.Bounds.Height > 0)
                    .Select(item => (item.Cell, Overlap: HorizontalOverlap(item.Bounds.X, item.Bounds.Right,
                        column.Left, column.Right)))
                    .Where(item => item.Overlap > 0)
                    .OrderByDescending(item => item.Overlap).FirstOrDefault().Cell;
                var target = visible ?? cells.Where(cell => string.Equals(cell.Properties.Name.ValueOrDefault,
                    column.Name, StringComparison.Ordinal)).ToArray() switch
                {
                    { Length: 1 } match => match[0],
                    _ => throw new AutomationException("GRID_COLUMNS_INVALID",
                        "Không ánh xạ được ô native cho cột " + column.Name)
                };
                var text = ReadCustomCellText(target, column.Name);
                rowValues[column.Name] = text.Value;
                rowSources[column.Name] = text.Source + "+UIA3.CustomGrid.DataItem";
            }
            values.Add(rowValues);
            sources.Add(rowSources);
        }
        return new NativeTable(values, Capture(), sources);
    }

    private sealed record CustomCompleteColumn(string Name, double Left, double Right,
        FlaUI.Core.AutomationElements.AutomationElement Header);

    private Task<NativeFrame> ScrollTableAsync(NativeFrame frame, string gridId, bool top, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            GuardWorkflow(frame);
            var grid = ResolveGrid(gridId);
            if (!grid.Patterns.Scroll.IsSupported)
                throw new AutomationException("GRID_COMPLETENESS_UNAVAILABLE",
                    "Custom grid exposes neither indexed rows nor ScrollPattern completeness; cannot verify the entire voucher.");
            var scroll = grid.Patterns.Scroll.Pattern;
            if (scroll.VerticallyScrollable.Value)
            {
                if (top) scroll.SetScrollPercent(-1, 0);
                else scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                epoch++;
            }
            return Capture();
        }, token);

    private sealed record NativeTablePage(NativeTable Table, IReadOnlyList<int> Indices, double Position);

    private bool HasVisibleTerminalRows(FlaUI.Core.AutomationElements.AutomationElement grid, CancellationToken token)
    {
        var rows = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(ControlType.DataItem), identity!.ProcessId, onscreen: false))
            .OrderBy(row => row.Bounds.Y).Select(row => row.Element).ToArray();
        if (rows.Length is < 2 or > 5001) return false;
        var bounds = grid.BoundingRectangle;
        for (var index = 0; index < rows.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var row = rows[index];
            var rowBounds = row.BoundingRectangle;
            // Infragistics supplies zero-based committed row ordinals and an
            // enabled -1 insertion sentinel after the last row. Require both
            // ends and every row fully visible vertically; long grids still
            // require indexed/scroll traversal.
            var expectedId = index == rows.Length - 1 ? "-1" : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (row.Properties.AutomationId.ValueOrDefault != expectedId
                || row.Properties.IsOffscreen.ValueOrDefault || rowBounds.Height <= 0
                || rowBounds.Y < bounds.Y || rowBounds.Bottom > bounds.Bottom
                || !(row.Properties.ItemStatus.ValueOrDefault ?? "").Contains("RowUiaProviderStub", StringComparison.Ordinal)
                || !row.Parent.Equals(rows[0].Parent)) return false;
        }
        return string.IsNullOrWhiteSpace(rows[^1].Properties.Name.ValueOrDefault);
    }

    private Task<NativeTablePage> ReadTablePageAsync(NativeFrame frame, string gridId,
        IReadOnlyList<string> columns, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            GuardWorkflow(frame);
            var grid = ResolveGrid(gridId);
            if (!grid.Patterns.Scroll.IsSupported)
                throw new AutomationException("GRID_COMPLETENESS_UNAVAILABLE", "Native scroll capability disappeared.");
            var scroll = grid.Patterns.Scroll.Pattern;
            var scrollable = scroll.VerticallyScrollable.Value;
            var position = scrollable ? scroll.VerticalScrollPercent.Value : -1;
            if (scrollable && (!double.IsFinite(position) || position < 0 || position > 100))
                throw new AutomationException("GRID_COMPLETENESS_UNAVAILABLE", "Native scroll position is invalid.");
            var layout = ReadCustomGridLayout(grid, token, columns);
            if (layout.Rows.Count > 5000) throw new AutomationException("GRID_LIMIT", "Native page exceeds 5000 rows.");
            var rows = new List<IReadOnlyDictionary<string, string>>();
            var sources = new List<IReadOnlyDictionary<string, string>>();
            var indices = new List<int>();
            foreach (var row in layout.Rows)
            {
                token.ThrowIfCancellationRequested();
                if (position >= 0 && row.Element.Properties.IsOffscreen.ValueOrDefault) continue;
                var ordinal = rows.Count;
                if (position >= 0)
                {
                    var cellIndices = row.Cells.Where(cell => cell.Patterns.GridItem.IsSupported)
                        .Select(cell => cell.Patterns.GridItem.Pattern.Row.Value).Distinct().ToArray();
                    if (cellIndices.Length != 1 || cellIndices[0] < 0 || cellIndices[0] >= 5000)
                        throw new AutomationException("GRID_COMPLETENESS_UNAVAILABLE",
                            "Scrollable custom rows lack a unique native row ordinal; cannot merge pages without guessing.");
                    ordinal = cellIndices[0];
                }
                if (indices.Contains(ordinal))
                    throw new AutomationException("GRID_ROW_AMBIGUOUS", "Duplicate row ordinal in native page.");
                var values = new Dictionary<string, string>();
                var rowSources = new Dictionary<string, string>();
                foreach (var column in columns)
                {
                    var value = ReadCustomCellText(row.Cells[FindCustomColumn(layout.Columns, column)], column);
                    values.Add(column, value.Value);
                    rowSources.Add(column, value.Source + "+UIA3.CustomGrid.CompleteTraversal");
                }
                rows.Add(values);
                sources.Add(rowSources);
                indices.Add(ordinal);
            }
            var after = scroll.VerticallyScrollable.Value ? scroll.VerticalScrollPercent.Value : -1;
            if (after != position) throw new AutomationException("GRID_CHANGED", "Native scroll position changed during readback.");
            GuardWorkflow(frame);
            return new NativeTablePage(new NativeTable(rows, Capture(), sources), indices, position);
        }, token);

}
