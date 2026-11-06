using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
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

        var scroll = await dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame);
            return CustomScrollState(ResolveGrid(gridId));
        }, token);
        return scroll.Scrollable
            ? await ReadCustomScrollingAsync(frame, gridId, columns, scroll.Percent, token)
            : await ReadCustomViewportAsync(frame, gridId, columns, scroll.HasPattern, token);
    }

    // Everything fits (or no scroll provider): the viewport is the table only if row
    // count, ordinals and the footer / add-new row prove nothing lies outside it.
    private async Task<NativeTable> ReadCustomViewportAsync(NativeFrame frame, string gridId,
        IReadOnlyList<string> columns, bool scrollPatternProvesFit, CancellationToken token)
    {
        string? previous = null;
        var read = await NativeWait.UntilAsync(async () =>
        {
            var page = await ReadCustomPageAsync(frame, gridId, columns, scrollable: false, token);
            frame = page.Table.Frame;
            return page;
        }, page =>
        {
            var fingerprint = JsonSerializer.Serialize(new { page.Indices, page.Table.Rows, page.Footer, page.Hidden });
            var stable = previous == fingerprint;
            previous = fingerprint;
            return stable;
        }, "GRID_NOT_STABLE", "Bảng chưa ổn định; chờ MISA tải xong dữ liệu rồi đối chiếu lại.", token);

        var count = read.Table.Rows.Count;
        if (read.VisibleDataRows != count)
            throw new AutomationException("GRID_INCOMPLETE",
                $"Bảng có {read.VisibleDataRows} dòng dữ liệu hiển thị nhưng chỉ đọc được {count} dòng (có dòng bị vô hiệu hóa hoặc đang chỉnh sửa). Hoàn tất thao tác trên lưới rồi đối chiếu lại.");
        RequireAscendingOrdinals(read.Indices);
        var hiddenOnly = read.Hidden.Except(read.Indices).Count();
        if (read.Footer is { } footer)
        {
            if (footer != count && footer != count + hiddenOnly)
                throw FooterMismatch(footer, count, hiddenOnly);
        }
        else
        {
            var missing = MissingOrdinals(read.Indices, read.Hidden);
            if (missing.Length > 0)
                throw new AutomationException("GRID_INCOMPLETE",
                    $"Số thứ tự dòng native bị thiếu ({FormatOrdinals(missing)}) trong {count} dòng đọc được; chưa chứng minh được bảng đầy đủ.");
            if (!scrollPatternProvesFit && !(read.InsertionVisible && read.AllInBounds))
                throw new AutomationException("GRID_COMPLETENESS_UNAVAILABLE",
                    $"Không có dòng 'Số dòng = N' hoặc dòng thêm mới để chứng minh {count} dòng đọc được là toàn bộ bảng và bảng không cuộn được. Mở rộng bảng hoặc kiểm tra lại cột/footer rồi đối chiếu lại.");
        }
        return read.Table with { Sources = read.Table.Sources.Select(row => (IReadOnlyDictionary<string, string>)
            row.ToDictionary(value => value.Key, value => value.Value + "+CompleteViewport")).ToArray() };
    }

    private async Task<NativeTable> ReadCustomScrollingAsync(NativeFrame frame, string gridId,
        IReadOnlyList<string> columns, double originalPercent, CancellationToken token)
    {
        var rows = new SortedDictionary<int, IReadOnlyDictionary<string, string>>();
        var sources = new SortedDictionary<int, IReadOnlyDictionary<string, string>>();
        var hidden = new HashSet<int>();
        int? footer = null;
        NativeTable? result = null;
        try
        {
            frame = await ScrollTableAsync(frame, gridId, top: true, token);
            var previousPosition = -1d;
            var endProven = false;
            var finished = false;
            for (var page = 0; page < 256 && !finished; page++)
            {
                token.ThrowIfCancellationRequested();
                string? previousRead = null;
                var first = page == 0;
                var floor = previousPosition;
                var read = await NativeWait.UntilAsync(async () =>
                {
                    var current = await ReadCustomPageAsync(frame, gridId, columns, scrollable: true, token);
                    frame = current.Table.Frame;
                    return current;
                }, current =>
                {
                    var fingerprint = JsonSerializer.Serialize(new { current.Position, current.Indices, current.Table.Rows, current.Footer });
                    var same = previousRead == fingerprint;
                    previousRead = fingerprint;
                    return same && (first ? current.Position <= 0.01 : current.Position > floor + 1e-6);
                }, "GRID_NOT_STABLE", "Bảng không hiển thị trang ổn định sau khi cuộn; đối chiếu lại.", token,
                    timeoutMilliseconds: first ? 5000 : 3000, returnLastMismatch: !first);

                for (var index = 0; index < read.Indices.Count; index++)
                {
                    var ordinal = read.Indices[index];
                    var row = read.Table.Rows[index];
                    if (rows.TryGetValue(ordinal, out var seen) && columns.Any(column => seen[column] != row[column]))
                        throw new AutomationException("GRID_CHANGED", $"Dòng native #{ordinal} đổi giá trị trong khi cuộn đọc bảng; đối chiếu lại.");
                    rows[ordinal] = row;
                    sources[ordinal] = read.Table.Sources[index];
                }
                hidden.UnionWith(read.Hidden);
                if (read.Footer is { } seenFooter)
                {
                    if (footer is { } known && known != seenFooter)
                        throw new AutomationException("GRID_CHANGED", $"'Số dòng' đổi từ {known} thành {seenFooter} trong khi đọc bảng; đối chiếu lại.");
                    footer = seenFooter;
                }
                if (rows.Count > 5000) throw new AutomationException("GRID_LIMIT", "Bảng vượt 5000 dòng; cần lọc đúng phạm vi.");

                var moved = first || read.Position > previousPosition + 1e-6;
                previousPosition = read.Position;
                if (read.Position >= 99.99 || read.InsertionVisible) { endProven = true; finished = true; }
                else if (!moved) finished = true;
                else frame = await ScrollTableAsync(frame, gridId, top: false, token);
            }
            if (!finished) throw new AutomationException("GRID_LIMIT", "Duyệt bảng vượt 256 trang.");

            var hiddenOnly = hidden.Except(rows.Keys).Count();
            if (footer is { } total)
            {
                if (total != rows.Count && total != rows.Count + hiddenOnly)
                    throw FooterMismatch(total, rows.Count, hiddenOnly);
            }
            else
            {
                var missing = MissingOrdinals(rows.Keys, hidden);
                if (missing.Length > 0)
                    throw new AutomationException("GRID_INCOMPLETE",
                        $"Số thứ tự dòng native bị thiếu ({FormatOrdinals(missing)}) trong {rows.Count} dòng đọc được; chưa chứng minh được bảng đầy đủ.");
                if (!endProven)
                    throw new AutomationException("GRID_COMPLETENESS_UNAVAILABLE",
                        $"Cuộn bảng dừng trước cuối danh sách (đã đọc {rows.Count} dòng) và không có 'Số dòng = N' để xác nhận. Đối chiếu lại.");
            }
            result = new NativeTable(rows.Values.ToArray(), frame, sources.Values.ToArray());
        }
        finally
        {
            try { frame = await RestoreCustomScrollAsync(frame, gridId, originalPercent); }
            catch (Exception error) when (error is AutomationException or InvalidOperationException
                or System.Runtime.InteropServices.COMException) { }
        }
        return result with { Frame = frame };
    }

    private static AutomationException FooterMismatch(int footer, int read, int hidden)
        => new("GRID_INCOMPLETE", $"Footer báo 'Số dòng = {footer}' nhưng chỉ đọc được {read} dòng"
            + (hidden > 0 ? $" (+{hidden} dòng ẩn)" : "") + ". Chờ bảng tải xong hoặc đối chiếu lại.");

    private static int[] MissingOrdinals(IEnumerable<int> present, IEnumerable<int> hidden)
    {
        var all = present.Concat(hidden).ToHashSet();
        return all.Count == 0 ? [] : Enumerable.Range(0, all.Max() + 1).Where(ordinal => !all.Contains(ordinal)).ToArray();
    }

    private static string FormatOrdinals(int[] ordinals)
        => string.Join(", ", ordinals.Take(8)) + (ordinals.Length > 8 ? ", …" : "");

    private static void RequireAscendingOrdinals(IReadOnlyList<int> ordinals)
    {
        for (var index = 1; index < ordinals.Count; index++)
            if (ordinals[index] <= ordinals[index - 1])
                throw new AutomationException("GRID_INCOMPLETE", "Thứ tự dòng hiển thị không khớp số thứ tự native; không thể xác nhận thứ tự dòng.");
    }

    private static (bool HasPattern, bool Scrollable, double Percent) CustomScrollState(AutomationElement grid)
    {
        if (!grid.Patterns.Scroll.IsSupported) return (false, false, -1);
        var scroll = grid.Patterns.Scroll.Pattern;
        if (!scroll.VerticallyScrollable.Value) return (true, false, -1);
        var percent = scroll.VerticalScrollPercent.Value;
        // A scrollable provider reporting NoScroll (-1) is not trustworthy; fall back to footer/add-new proof.
        return double.IsFinite(percent) && percent is >= 0 and <= 100 ? (true, true, percent) : (false, false, -1);
    }


    private int? TryCustomDataRowCount(FlaUI.Core.AutomationElements.AutomationElement grid,
        CancellationToken token)
    {
        if (grid.Patterns.Grid.IsSupported || grid.Patterns.Table.IsSupported || grid.Patterns.Scroll.IsSupported)
            return null;
        var exposed = Snapshots(grid, TrueCondition.Default);
        var declared = exposed.Select(item => TryParseDataRowCount(TryLive(() => item.Element.Properties.ItemStatus.ValueOrDefault)))
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
            if (TryParseDataRowCount(TryLive(() => region.Element.Properties.ItemStatus.ValueOrDefault)) is not { } count)
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
        => NumericRowSnapshots(scope).Select(row => (row.Ordinal, row.Row.Element)).ToArray();

    private (int Ordinal, ElementSnapshot Row)[] NumericRowSnapshots(FlaUI.Core.AutomationElements.AutomationElement scope)
        => Snapshots(scope, Live(automation.ConditionFactory.ByControlType(ControlType.DataItem),
                identity!.ProcessId, enabled: false, onscreen: false))
            .Where(row => int.TryParse(row.AutomationId, out var ordinal) && ordinal >= 0)
            .Select(row => (Ordinal: int.Parse(row.AutomationId, System.Globalization.CultureInfo.InvariantCulture), Row: row))
            .GroupBy(row => row.Ordinal).Where(group => group.Count() == 1)
            .Select(group => group.Single()).OrderBy(row => row.Ordinal).ToArray();

    // UIA_E_ELEMENTNOTAVAILABLE, raw or wrapped by FlaUI.
    private static bool Vanished(Exception error)
        => error is FlaUI.Core.Exceptions.ElementNotAvailableException
            || error is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80040201) };

    // A vanished candidate (row region) is simply not a candidate; never used for a value that is compared.
    private static T? TryLive<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (Vanished(error)) { return default; }
    }

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

        (int Ordinal, ElementSnapshot Row)[] CompleteRows()
        {
            var rowScope = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(ControlType.Tree),
                    identity!.ProcessId, enabled: false, onscreen: false))
                .Where(region => TryParseDataRowCount(TryLive(() => region.Element.Properties.ItemStatus.ValueOrDefault)) == expectedRowCount)
                .Select(region => (Scope: region.Element, Rows: NumericRowSnapshots(region.Element)))
                .FirstOrDefault(candidate => candidate.Rows.Length == expectedRowCount
                    && candidate.Rows.Select(row => row.Ordinal).SequenceEqual(Enumerable.Range(0, expectedRowCount)));
            var found = rowScope.Scope is not null ? rowScope.Rows : NumericRowSnapshots(grid);
            if (found.Length != expectedRowCount
                || !found.Select(row => row.Ordinal).SequenceEqual(Enumerable.Range(0, expectedRowCount)))
                throw new AutomationException("GRID_INCOMPLETE", "Native row ordinals do not cover the complete register.");
            return found;
        }
        var rows = CompleteRows();

        // Hidden (zero-area) rows keep their ordinal for the proof but have no cells to read.
        // Bounds, names and cell mapping come from the search snapshot: R20 recreates a row's cell accessibles
        // while one of them is read, and a live read of every sibling then failed on every pass (VPS, 0.2.160).
        var shownRows = rows.Where(row => row.Row.HasArea).ToArray();
        if (TryReadCustomFooterCount(grid) is { } footer && footer != shownRows.Length && footer != rows.Length)
            throw FooterMismatch(footer, shownRows.Length, rows.Length - shownRows.Length);
        ElementSnapshot[] RowCells(AutomationElement row)
            => Snapshots(row, Live(AnyControlType(ControlType.Edit, ControlType.ComboBox, ControlType.Text),
                identity!.ProcessId, enabled: false, onscreen: false));
        AutomationElement MapCell(ElementSnapshot[] cells, CustomCompleteColumn column)
        {
            var visible = cells.Where(item => item.HasArea)
                .Select(item => (item.Element, Overlap: HorizontalOverlap(item.Bounds.X, item.Bounds.Right,
                    column.Left, column.Right)))
                .Where(item => item.Overlap > 0)
                .OrderByDescending(item => item.Overlap).FirstOrDefault().Element;
            return visible ?? cells.Where(cell => string.Equals(cell.Name, column.Name, StringComparison.Ordinal)).ToArray() switch
            {
                { Length: 1 } match => match[0].Element,
                _ => throw new AutomationException("GRID_COLUMNS_INVALID",
                    "Không ánh xạ được ô native cho cột " + column.Name)
            };
        }
        var values = new List<IReadOnlyDictionary<string, string>>(shownRows.Length);
        var sources = new List<IReadOnlyDictionary<string, string>>(shownRows.Length);
        foreach (var row in shownRows)
        {
            token.ThrowIfCancellationRequested();
            // Reading a cell can make R20 recreate every row, so a later row found before the reads is stale too
            // (VPS 0.2.161): the row and its cells are looked up again inside the retry, never outside it.
            AutomationElement? rowElement = row.Row.Element;
            ElementSnapshot[]? cells = null;
            var rowValues = new Dictionary<string, string>(StringComparer.Ordinal);
            var rowSources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var column in headers)
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (rowElement is null)
                        {
                            var fresh = CompleteRows().Single(item => item.Ordinal == row.Ordinal).Row;
                            if (!fresh.HasArea)
                                throw new AutomationException("GRID_CHANGED", "Dòng native bị ẩn trong khi đọc.");
                            rowElement = fresh.Element;
                        }
                        cells ??= RowCells(rowElement);
                        var text = ReadCustomCellText(MapCell(cells, column), column.Name);
                        rowValues[column.Name] = text.Value;
                        rowSources[column.Name] = text.Source + "+UIA3.CustomGrid.DataItem";
                        break;
                    }
                    catch (Exception error) when (Vanished(error) && attempt < 5)
                    {
                        // Only reads happen here, so reading again from the row resolved anew by ordinal is safe.
                        token.ThrowIfCancellationRequested();
                        (rowElement, cells) = (null, null);
                    }
                    catch (Exception error) when (Vanished(error))
                    {
                        throw new AutomationException("NATIVE_ELEMENT_UNAVAILABLE",
                            $"Ô lưới MISA biến mất khi đọc (dòng {row.Ordinal + 1}, cột {column.Name}); cần đọc lại.", false, error);
                    }
                }
            }
            values.Add(rowValues);
            sources.Add(rowSources);
        }
        return new NativeTable(values, Capture(), sources);
    }

    private sealed record CustomCompleteColumn(string Name, double Left, double Right,
        FlaUI.Core.AutomationElements.AutomationElement Header);

    private static readonly Regex FooterRowCountPattern = new(@"^\s*Số\s+dòng\s*=\s*(\d[\d.,\u00A0\s]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Footer label "Số dòng = N" counts data rows only (not the add-new row). It is extra evidence: when it cannot be
    // read, null means "no footer proof", never a failed read. The grid's own properties are never read live here: on
    // the VPS (0.2.162) R20's grdDetail answered its live BoundingRectangle with "element not available" on every pass
    // while searches through it worked, which failed reconciliation of a correct voucher.
    private int? TryReadCustomFooterCount(AutomationElement grid)
    {
        var condition = Live(automation.ConditionFactory.ByName("Số dòng", PropertyConditionFlags.MatchSubstring),
            identity!.ProcessId, enabled: false, onscreen: false);
        ElementSnapshot[] found;
        try
        {
            found = Snapshots(grid, condition).Where(item => item.HasArea).ToArray();
            if (found.Length == 0)
            {
                if (TrySnapshot(grid) is not { HasArea: true } snapshot || grid.Parent is not { } parent) return null;
                var bounds = snapshot.Bounds;
                found = Snapshots(parent, condition).Where(item => item.HasArea
                    && bounds.Contains(item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2)).ToArray();
            }
        }
        catch (Exception error) when (Vanished(error)) { return null; }
        var counts = found.Select(item => FooterRowCountPattern.Match(item.Name.Normalize(NormalizationForm.FormC)))
            .Where(match => match.Success)
            .Select(match => int.TryParse(new string(match.Groups[1].Value.Where(char.IsDigit).ToArray()),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
                ? (int?)count : null)
            .Where(count => count is not null).Select(count => count!.Value).Distinct().ToArray();
        return counts.Length == 1 ? counts[0] : null;
    }

    private Task<NativeFrame> ScrollTableAsync(NativeFrame frame, string gridId, bool top, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            GuardWorkflow(frame);
            var grid = ResolveGrid(gridId);
            if (!grid.Patterns.Scroll.IsSupported)
                throw new AutomationException("GRID_COMPLETENESS_UNAVAILABLE",
                    "Bảng không cung cấp ScrollPattern nên không thể cuộn để xác nhận toàn bộ dòng.");
            var scroll = grid.Patterns.Scroll.Pattern;
            if (scroll.VerticallyScrollable.Value)
            {
                if (top) scroll.SetScrollPercent(-1, 0);
                else scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                epoch++;
            }
            return Capture();
        }, token);

    // Best-effort: put the user's viewport back after the observational scroll.
    private Task<NativeFrame> RestoreCustomScrollAsync(NativeFrame frame, string gridId, double percent)
        => dispatcher.RunAsync(() =>
        {
            GuardWorkflow(frame);
            var grid = ResolveGrid(gridId);
            if (percent >= 0 && grid.Patterns.Scroll.IsSupported && grid.Patterns.Scroll.Pattern.VerticallyScrollable.Value)
            {
                grid.Patterns.Scroll.Pattern.SetScrollPercent(-1, percent);
                epoch++;
            }
            return Capture();
        }, CancellationToken.None);

    private sealed record NativeTablePage(NativeTable Table, IReadOnlyList<int> Indices, double Position,
        IReadOnlyList<int> Hidden, int? Footer, bool InsertionVisible, int VisibleDataRows, bool AllInBounds);

    private Task<NativeTablePage> ReadCustomPageAsync(NativeFrame frame, string gridId,
        IReadOnlyList<string> columns, bool scrollable, CancellationToken token)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            GuardWorkflow(frame);
            var grid = ResolveGrid(gridId);
            var state = CustomScrollState(grid);
            if (state.Scrollable != scrollable)
                throw new AutomationException("GRID_CHANGED", "Khả năng cuộn của bảng đổi trong khi đọc; đối chiếu lại.");
            var position = state.Percent;

            // Zero-area rows are hidden: not shown, so not read, but their ordinals fill gaps.
            var items = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(ControlType.DataItem),
                    identity!.ProcessId, enabled: false, onscreen: false))
                .Where(item => item.AutomationId != "-1").ToArray();
            var shown = items.Where(item => item.HasArea).ToArray();
            var hidden = items.Where(item => !item.HasArea && int.TryParse(item.AutomationId, out var id) && id >= 0)
                .Select(item => int.Parse(item.AutomationId, System.Globalization.CultureInfo.InvariantCulture))
                .Distinct().OrderBy(id => id).ToArray();
            var bounds = grid.BoundingRectangle;
            var allInBounds = shown.All(item => !item.Offscreen && item.Bounds.Y >= bounds.Y && item.Bounds.Bottom <= bounds.Bottom);
            var lastBottom = shown.Length == 0 ? int.MinValue : shown.Max(item => item.Bounds.Bottom);
            var insertion = Snapshots(grid, Live(automation.ConditionFactory.ByControlType(ControlType.DataItem),
                    identity!.ProcessId, onscreen: true))
                .Any(item => item.AutomationId == "-1" && item.HasArea && item.Bounds.Y >= lastBottom - 1);

            var layout = ReadCustomGridLayout(grid, token, columns, dataRowsOnly: true,
                onscreenOnly: scrollable, allowEmpty: true);
            if (layout.Rows.Count > 5000) throw new AutomationException("GRID_LIMIT", "Bảng vượt 5000 dòng; cần lọc đúng phạm vi.");
            var rows = new List<IReadOnlyDictionary<string, string>>();
            var sources = new List<IReadOnlyDictionary<string, string>>();
            var indices = new List<int>();
            foreach (var row in layout.Rows)
            {
                token.ThrowIfCancellationRequested();
                var ordinal = CustomRowOrdinal(row, scrollable ? null : rows.Count);
                if (indices.Contains(ordinal))
                    throw new AutomationException("GRID_ROW_AMBIGUOUS", $"Số thứ tự dòng native #{ordinal} bị trùng trong cùng một trang.");
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
            var after = CustomScrollState(grid);
            if (after.Scrollable != scrollable || scrollable && after.Percent != position)
                throw new AutomationException("GRID_CHANGED", "Vị trí cuộn của bảng đổi trong khi đọc; đối chiếu lại.");
            var footer = TryReadCustomFooterCount(grid);
            GuardWorkflow(frame);
            return new NativeTablePage(new NativeTable(rows, Capture(), sources), indices, position, hidden, footer,
                insertion, shown.Length, allInBounds);
        }, token);

    // Merging pages needs a stable ordinal: the row's numeric AutomationId, else its cells' GridItem row.
    private static int CustomRowOrdinal(CustomRow row, int? positional)
    {
        if (int.TryParse(row.Element.Properties.AutomationId.ValueOrDefault, out var id) && id is >= 0 and < 5000) return id;
        var cellRows = row.Cells.Where(cell => cell.Patterns.GridItem.IsSupported)
            .Select(cell => cell.Patterns.GridItem.Pattern.Row.Value).Distinct().ToArray();
        if (cellRows.Length == 1 && cellRows[0] is >= 0 and < 5000) return cellRows[0];
        if (positional is { } index) return index;
        throw new AutomationException("GRID_COMPLETENESS_UNAVAILABLE",
            "Một dòng trong bảng cuộn không có số thứ tự native duy nhất nên không thể ghép các trang mà không đoán; đối chiếu lại.");
    }
}
