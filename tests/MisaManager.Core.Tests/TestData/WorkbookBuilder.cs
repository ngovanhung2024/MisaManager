using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace MisaManager.Core.Tests.TestData;

/// <summary>Excel formula with its cached result (what a workbook stores next to the formula).</summary>
internal sealed record Formula(string Text, object? Cached);
/// <summary>Text written as an inline string instead of a shared string.</summary>
internal sealed record InlineText(string Text);
internal sealed record ErrorValue(string Code);
/// <summary>A number shown with Excel's 0% format (the stored value is the fraction, 0.08 for 8%).</summary>
internal sealed record Percent(double Fraction);

internal sealed class SheetBuilder(string name)
{
    internal SortedDictionary<int, SortedDictionary<int, object>> Rows { get; } = [];
    internal HashSet<int> HiddenRows { get; } = [];
    internal List<string> Merges { get; } = [];
    internal bool AutoFilter { get; private set; }
    internal SheetStateValues? State { get; private set; }
    public string Name { get; } = name;

    /// <summary>Writes values starting at column A; <c>null</c> leaves the cell absent.</summary>
    public SheetBuilder Row(int row, params object?[] values)
    {
        for (var index = 0; index < values.Length; index++) Cell(row, index + 1, values[index]);
        return this;
    }

    public SheetBuilder Cell(int row, int column, object? value)
    {
        if (!Rows.TryGetValue(row, out var cells)) Rows[row] = cells = [];
        if (value is null) cells.Remove(column); else cells[column] = value;
        return this;
    }

    public SheetBuilder Merge(string range) { Merges.Add(range); return this; }
    public SheetBuilder Hide(int row) { HiddenRows.Add(row); return this; }
    public SheetBuilder Filter() { AutoFilter = true; return this; }
    public SheetBuilder Hidden() { State = SheetStateValues.Hidden; return this; }
    public SheetBuilder Visible() { State = SheetStateValues.Visible; return this; }
}

/// <summary>Writes small, realistic .xlsx files (shared strings, styles, merges, formulas) for importer tests.</summary>
internal sealed class WorkbookBuilder
{
    private const uint General = 0, DateStyle = 1, PercentStyle = 3;
    private readonly List<SheetBuilder> sheets = [];

    public bool Date1904 { get; set; }

    public SheetBuilder Sheet(string name)
    {
        var sheet = new SheetBuilder(name);
        sheets.Add(sheet);
        return sheet;
    }

    public string Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            if (Date1904) workbookPart.Workbook.AppendChild(new WorkbookProperties { Date1904 = true });
            var sheetList = workbookPart.Workbook.AppendChild(new Sheets());
            AddStyles(workbookPart);
            var strings = new List<string>();
            uint sheetId = 1;
            foreach (var sheet in sheets)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                worksheetPart.Worksheet = BuildWorksheet(sheet, strings);
                sheetList.AppendChild(new Sheet { Name = sheet.Name, SheetId = sheetId++, Id = workbookPart.GetIdOfPart(worksheetPart), State = sheet.State });
            }
            var table = workbookPart.AddNewPart<SharedStringTablePart>();
            table.SharedStringTable = new SharedStringTable(strings.Select(text =>
                new SharedStringItem(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
        }
        return path;
    }

    private static void AddStyles(WorkbookPart workbookPart)
    {
        var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new Stylesheet(
            new Fonts(new Font()),
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
            new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(new CellFormat(),
                new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 49, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 9, ApplyNumberFormat = true }));
    }

    private Worksheet BuildWorksheet(SheetBuilder sheet, List<string> strings)
    {
        var data = new SheetData();
        foreach (var rowNumber in sheet.Rows.Keys.Union(sheet.HiddenRows).Order())
        {
            var row = new Row { RowIndex = (uint)rowNumber };
            if (sheet.HiddenRows.Contains(rowNumber)) row.Hidden = true;
            if (sheet.Rows.TryGetValue(rowNumber, out var cells))
                foreach (var (column, value) in cells)
                    row.AppendChild(BuildCell(CellRef(column, rowNumber), value, strings));
            data.AppendChild(row);
        }
        var worksheet = new Worksheet(data);
        if (sheet.AutoFilter) worksheet.AppendChild(new AutoFilter { Reference = "A1:Z1" });
        if (sheet.Merges.Count > 0)
            worksheet.AppendChild(new MergeCells(sheet.Merges.Select(range => new MergeCell { Reference = range })));
        return worksheet;
    }

    private Cell BuildCell(string reference, object value, List<string> strings)
    {
        var cell = new Cell { CellReference = reference };
        switch (value)
        {
            case Formula formula:
                var cached = BuildCell(reference, formula.Cached ?? (object)"", strings);
                if (cached.DataType?.Value == CellValues.SharedString)
                {
                    cell.DataType = CellValues.String;
                    cell.CellValue = new CellValue(formula.Cached!.ToString()!);
                }
                else { cell.DataType = cached.DataType; cell.CellValue = (CellValue?)cached.CellValue?.CloneNode(true); cell.StyleIndex = cached.StyleIndex; }
                cell.CellFormula = new CellFormula(formula.Text);
                break;
            case InlineText inline:
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(inline.Text) { Space = SpaceProcessingModeValues.Preserve });
                break;
            case ErrorValue error:
                cell.DataType = CellValues.Error;
                cell.CellValue = new CellValue(error.Code);
                break;
            case bool flag:
                cell.DataType = CellValues.Boolean;
                cell.CellValue = new CellValue(flag ? "1" : "0");
                break;
            case string text:
                var index = strings.IndexOf(text);
                if (index < 0) { strings.Add(text); index = strings.Count - 1; }
                cell.DataType = CellValues.SharedString;
                cell.CellValue = new CellValue(index.ToString(CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                cell.CellValue = new CellValue(Serial(date).ToString(CultureInfo.InvariantCulture));
                cell.StyleIndex = DateStyle;
                break;
            case Percent percent:
                cell.CellValue = new CellValue(percent.Fraction.ToString("R", CultureInfo.InvariantCulture));
                cell.StyleIndex = PercentStyle;
                break;
            case double number:
                cell.CellValue = new CellValue(number.ToString("R", CultureInfo.InvariantCulture));
                cell.StyleIndex = General;
                break;
            case int or long or decimal:
                cell.CellValue = new CellValue(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                break;
            default:
                throw new ArgumentException("Unsupported cell value " + value.GetType().Name);
        }
        return cell;
    }

    /// <summary>Excel serial number for a date in the workbook's date system.</summary>
    public int Serial(DateOnly date)
        => date.DayNumber - (Date1904 ? new DateOnly(1904, 1, 1) : new DateOnly(1899, 12, 30)).DayNumber;

    private static string CellRef(int column, int row) => MisaManager.Import.CellAddress.Format(column, row);
}

/// <summary>The column layout most Vietnamese sales-invoice exports share, with realistic sample rows.</summary>
internal static class InvoiceSheet
{
    public const int Number = 1, Date = 2, Buyer = 3, BuyerCode = 4, BuyerTax = 5, Name = 6, Unit = 7,
        Quantity = 8, UnitPrice = 9, Amount = 10, TaxRate = 11, TaxAmount = 12, Total = 13;

    public static readonly string[] Headers =
    [
        "Số hóa đơn", "Ngày hóa đơn", "Tên người mua", "Mã khách hàng", "MST người mua", "Tên hàng hóa", "Đơn vị tính",
        "Số lượng", "Đơn giá", "Thành tiền", "Thuế suất", "Tiền thuế", "Tổng tiền có thuế"
    ];

    public static object?[] Line(string number, DateOnly date, string buyer, string? buyerCode, string? taxCode, string name,
        string unit, decimal quantity, decimal unitPrice, int taxPercent = 8)
    {
        var amount = quantity * unitPrice;
        var tax = Math.Round(amount * taxPercent / 100m, MidpointRounding.AwayFromZero);
        return [number, date, buyer, buyerCode, taxCode, name, unit, quantity, unitPrice, amount, taxPercent + "%", tax, amount + tax];
    }

    public static readonly DateOnly Day = new(2026, 10, 4);

    /// <summary>Header on row 1; invoice 1000 (two lines, organisation buyer) and 1001 (one line, consumer buyer).</summary>
    public static (WorkbookBuilder Book, SheetBuilder Sheet) Standard(string sheetName = "Hóa đơn")
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet(sheetName);
        sheet.Row(1, Headers.Cast<object?>().ToArray());
        sheet.Row(2, Line("1000", Day, "CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V", "A&V", "0107286506", "Rông đỏ 200SL - Chai 100ml", "Chai", 22, 10_000m));
        sheet.Row(3, Line("1000", Day, "CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V", "A&V", "0107286506", "Hesman 60WG 18g", "Gói", 38, 16_000m));
        sheet.Row(4, Line("1001", Day, "BÁN CHO NGƯỜI TIÊU DÙNG", null, null, "Phân Ure Đạm Phú Mỹ loại 1 (bao)", "kg", 100, 15_429m));
        return (book, sheet);
    }
}
