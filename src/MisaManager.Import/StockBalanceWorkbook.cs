using System.Collections.Immutable;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MisaManager.Core;

namespace MisaManager.Import;

/// <summary>
/// Output of <see cref="StockBalancer"/>: the sales workbook with "Mã hàng" filled, imported instead of the original so the tool
/// enters exactly the balanced codes, and a report of stock per code, changed lines, same-goods groups and warnings.
/// </summary>
public static class StockBalanceWorkbook
{
    public const string CodeHeader = "Mã hàng";
    // None of these headings is an importer alias, so the copy maps exactly like the original sheet.
    public static readonly string[] NoteHeaders = ["Tên hàng trên hóa đơn", "ĐVT trên hóa đơn", "Số lượng trên hóa đơn", "Cân đối kho"];

    /// <summary>
    /// Copies the imported sheet row by row. Each balanced line gets its code in "Mã hàng" (a new last column when the sheet has none);
    /// a line moved to a code with another name or unit gets that code's name, unit, quantity and price, and keeps the invoice's own
    /// values in the note columns. Returns the number of lines given a code.
    /// </summary>
    public static int WriteSales(WorkbookData source, ImportResult imported, StockBalancePlan plan, string path)
    {
        var sheet = source.Sheets.SingleOrDefault(item => item.Name == imported.Source.Worksheet)
            ?? throw new WorkbookImportException("WORKSHEET_NOT_FOUND", $"Không tìm thấy trang tính '{imported.Source.Worksheet}' trong file nguồn.");
        if (!string.Equals(source.Sha256, imported.Source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new WorkbookImportException("SOURCE_CHANGED", "File Excel bán ra đã thay đổi sau khi nạp; nạp lại file rồi cân đối lại.");
        // The copy holds plain values, which would lift these blocks of the importer: the source must be fixed first.
        var unconfirmed = imported.Issues.Concat(imported.Invoices.SelectMany(invoice => invoice.Issues.IsDefault ? [] : invoice.Issues))
            .Where(issue => issue.Code is "FORMULA_UNCONFIRMED" or "CELL_TYPE_INVALID").ToArray();
        if (unconfirmed.Length > 0)
        {
            var cells = unconfirmed.Select(issue => issue.Cell).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            throw new WorkbookImportException("SOURCE_CELLS_UNCONFIRMED", "File bán ra có ô công thức hoặc ô lỗi"
                + (cells.Length > 0 ? $" ({string.Join(", ", cells.Take(5))}{(cells.Length > 5 ? ", …" : "")})" : "")
                + ": xuất giá trị tĩnh, mở lại file rồi cân đối.");
        }
        var byLine = plan.Sales.ToDictionary(line => (line.InvoiceId, line.LineId));
        var lines = imported.Invoices.SelectMany(invoice => invoice.Lines.Select(line => (Line: line,
                Assignment: byLine.GetValueOrDefault((invoice.InvoiceId, line.LineId)))))
            .Where(entry => entry.Assignment is not null && entry.Line.Cells.ContainsKey("name"))
            .ToDictionary(entry => entry.Line.Cells["name"].Row, entry => (entry.Line, Assignment: entry.Assignment!));
        var skippedRows = lines.Values.Where(entry => entry.Assignment.Kind == StockAllocationKind.Skipped)
            .Select(entry => entry.Line.Cells["name"].Row).ToHashSet();
        int? Column(string field) => imported.Invoices.SelectMany(invoice => invoice.Lines)
            .Select(line => line.Cells.GetValueOrDefault(field)?.Column).FirstOrDefault(column => column is not null);
        var last = sheet.Rows.SelectMany(row => row.Cells.Keys).DefaultIfEmpty(0).Max();
        var codeColumn = Column("itemCode") ?? ++last;
        var notes = Enumerable.Range(last + 1, NoteHeaders.Length).ToArray();
        var (name, unit, quantity, price) = (Column("name"), Column("unit"), Column("quantity"), Column("unitPrice"));
        var written = 0;
        var rows = sheet.Rows.Where(row => row.Number == imported.Source.FirstRow || !skippedRows.Contains(row.Number)).Select(row =>
        {
            var cells = row.Cells.Values.ToDictionary(cell => cell.Column, cell => Value(cell, source.Date1904));
            if (row.Number == imported.Source.FirstRow)
            {
                cells[codeColumn] = CodeHeader;
                for (var index = 0; index < notes.Length; index++) cells[notes[index]] = NoteHeaders[index];
            }
            else if (lines.TryGetValue(row.Number, out var entry))
            {
                var (line, assignment) = entry;
                cells[codeColumn] = assignment.NewItem ? "" : assignment.Code ?? "";
                if (assignment.Code is not null && !assignment.NewItem) written++;
                if (assignment.Name != assignment.SourceName && name is { } nameColumn)
                {
                    cells[nameColumn] = assignment.Name;
                    cells[notes[0]] = assignment.SourceName;
                }
                if (assignment.Converted)
                {
                    if (unit is { } unitColumn) cells[unitColumn] = assignment.Unit;
                    if (quantity is { } quantityColumn) cells[quantityColumn] = assignment.Quantity;
                    if (price is { } priceColumn && assignment.UnitPrice is { } unitPrice) cells[priceColumn] = unitPrice;
                    cells[notes[1]] = assignment.SourceUnit;
                    cells[notes[2]] = assignment.SourceQuantity;
                }
                cells[notes[3]] = Note(assignment);
            }
            return (row.Number, Cells: (IReadOnlyDictionary<int, object?>)cells);
        }).ToArray();
        Write(path, [new(sheet.Name, rows, imported.Source.FirstRow, sheet.Rows.Where(row => row.Hidden).Select(row => row.Number).ToHashSet())]);
        return written;
    }

    /// <summary>Stock per code, changed sales lines, same-goods groups and warnings, one sheet each.</summary>
    public static void WriteReport(StockBalancePlan plan, string path)
    {
        var opening = plan.Codes.ToDictionary(code => code.Code, code => code.Opening, StringComparer.OrdinalIgnoreCase);
        Sheet Table(string title, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> data)
            => new(title, [.. new[] { (IReadOnlyList<object?>)[.. headers] }.Concat(data).Select((values, index) => (index + 1,
                (IReadOnlyDictionary<int, object?>)values.Select((value, column) => (Column: column + 1, Value: value))
                    .ToDictionary(pair => pair.Column, pair => pair.Value)))], 1);
        Write(path,
        [
            Table("Tồn kho theo mã", ["Mã hàng", "Tên hàng", "ĐVT", "Tồn đầu kỳ", "Nhập (mua vào)", "Xuất (bán ra)", "Tồn cuối", "Tồn thấp nhất",
                    "Ngày thấp nhất", "Tình trạng"],
                plan.Codes.Select(code => (IReadOnlyList<object?>)[code.Code, code.Name, code.Unit, code.Opening, code.Received, code.Issued,
                    code.Closing, code.Lowest, code.LowestOn?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    code.Lowest < 0 ? "Âm kho" : ""])),
            Table("Dòng bán ra đổi mã", ["Ngày", "Số hóa đơn", "Tên hàng trên hóa đơn", "ĐVT", "Số lượng", "Mã theo tên", "Mã dùng", "Tên hàng nhập",
                    "ĐVT nhập", "Số lượng nhập", "Cân đối kho", "Thiếu tồn"],
                plan.Sales.Where(line => line.CodeChanged || line.Converted || line.Name != line.SourceName
                        || line.Kind is StockAllocationKind.Borrowed or StockAllocationKind.Shortage or StockAllocationKind.Unmatched or StockAllocationKind.Skipped)
                    .Select(line => (IReadOnlyList<object?>)[line.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), line.InvoiceNumber,
                        line.SourceName, line.SourceUnit, line.SourceQuantity, line.MatchedCode, line.Code, line.Name, line.Unit, line.Quantity,
                        Note(line), line.Missing == 0 ? null : line.Missing])),
            Table("Nhóm cùng mặt hàng", ["Nhóm", "Mã hàng", "Tên hàng", "ĐVT", "Tồn đầu kỳ"],
                plan.Families.SelectMany((group, index) => group.Select(item => (IReadOnlyList<object?>)[(decimal)(index + 1), item.Code, item.Name,
                    item.Unit, opening.GetValueOrDefault(item.Code)]))),
            Table("Cảnh báo", ["Nội dung"], plan.Warnings.Select(warning => (IReadOnlyList<object?>)[warning]))
        ]);
    }

    public static string Note(StockLineAssignment line) => line.NewItem && line.Kind is not StockAllocationKind.Shortage
        ? $"Hàng mua vào chưa có mã: dùng tồn {line.Code} '{line.Name}' (mã tool tạo khi nhập mua vào)"
        : line.Kind switch
    {
        StockAllocationKind.Matched => "Giữ mã theo tên",
        StockAllocationKind.Fixed => "Mã ghi sẵn trong Excel",
        StockAllocationKind.Family => $"Cùng mặt hàng: dùng tồn của {line.Code} (thay {line.MatchedCode ?? "mã mới"})",
        StockAllocationKind.Similar => $"Tên gần giống: dùng tồn của {line.Code} (thay {line.MatchedCode ?? "mã mới"})",
        StockAllocationKind.Borrowed => $"Âm tạm thời {line.Missing.ToString("0.###", CultureInfo.InvariantCulture)} đến khi nhập mua {line.Code}",
        StockAllocationKind.Shortage => $"Thiếu tồn {line.Missing.ToString("0.###", CultureInfo.InvariantCulture)}: không mã cùng loại nào đủ hàng",
        StockAllocationKind.Skipped => $"Bỏ qua dòng: thiếu {line.Missing.ToString("0.###", CultureInfo.InvariantCulture)} hoặc không quy đổi được, không nhập để tránh âm kho",
        _ => "Chưa có mã trong danh mục: tool sẽ tạo mã mới không có tồn"
    };

    // Hidden rows stay hidden so the importer still warns that they are included.
    private sealed record Sheet(string Name, IReadOnlyList<(int Number, IReadOnlyDictionary<int, object?> Cells)> Rows, int HeaderRow,
        IReadOnlySet<int>? HiddenRows = null);

    private static void Write(string path, IReadOnlyList<Sheet> sheets)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var document = SpreadsheetDocument.Create(temporary, SpreadsheetDocumentType.Workbook))
            {
                var workbook = document.AddWorkbookPart();
                workbook.Workbook = new Workbook();
                var list = new Sheets();
                foreach (var (sheet, index) in sheets.Select((sheet, index) => (sheet, index)))
                {
                    var part = workbook.AddNewPart<WorksheetPart>();
                    var data = new SheetData();
                    foreach (var (number, cells) in sheet.Rows) data.Append(Row((uint)number, cells, sheet.HiddenRows?.Contains(number) == true));
                    part.Worksheet = new Worksheet(new SheetViews(new SheetView(new Pane
                    {
                        VerticalSplit = sheet.HeaderRow, TopLeftCell = "A" + (sheet.HeaderRow + 1).ToString(CultureInfo.InvariantCulture),
                        ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen
                    }) { WorkbookViewId = 0 }), data);
                    list.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet
                    {
                        Id = workbook.GetIdOfPart(part), SheetId = (uint)(index + 1), Name = sheet.Name.Length > 31 ? sheet.Name[..31] : sheet.Name
                    });
                }
                workbook.Workbook.AppendChild(list);
                workbook.Workbook.Save();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Row Row(uint number, IReadOnlyDictionary<int, object?> cells, bool hidden)
    {
        var row = new Row { RowIndex = number };
        if (hidden) row.Hidden = true;
        foreach (var (column, value) in cells.OrderBy(pair => pair.Key))
        {
            var reference = CellAddress.Format(column, (int)number);
            switch (value)
            {
                case decimal amount:
                    row.Append(new Cell { CellReference = reference, CellValue = new CellValue(amount.ToString(CultureInfo.InvariantCulture)) });
                    break;
                case string text when text.Length > 0:
                    row.Append(new Cell { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(text)) });
                    break;
            }
        }
        return row;
    }

    // Numbers stay numbers; a date-formatted serial becomes dd/MM/yyyy text, which the importer reads the same way.
    private static object? Value(SourceCell cell, bool date1904)
    {
        if (cell.CellType == "number" && MisaManager.Core.DecimalValue.TryParseInvariant(cell.RawValue.Trim(), out var number))
        {
            if (!IsDateFormat(cell.NumberFormat) || number != decimal.Truncate(number) || number < 1 || number > 2_958_465) return number;
            var basis = date1904 ? new DateOnly(1904, 1, 1) : number < 60 ? new DateOnly(1899, 12, 31) : new DateOnly(1899, 12, 30);
            return basis.AddDays((int)number).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }
        return cell.ParsedValue ?? cell.RawValue;
    }

    private static bool IsDateFormat(string format)
    {
        if (format.StartsWith("builtin:", StringComparison.Ordinal))
            return int.TryParse(format.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id is >= 14 and <= 22 or >= 45 and <= 47;
        var plain = System.Text.RegularExpressions.Regex.Replace(format, "\"[^\"]*\"|\\[[^\\]]*\\]", "").ToLowerInvariant();
        return plain.Contains("yy", StringComparison.Ordinal) || plain.Contains("dd", StringComparison.Ordinal);
    }
}
