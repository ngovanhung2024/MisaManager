using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MisaManager.Core;

namespace MisaManager.Import;

public sealed record WorkbookLimits(long CompressedBytes = 32 * 1024 * 1024,
    long ExpandedBytes = 128 * 1024 * 1024, int Sheets = 32, int Rows = 100_000,
    int Cells = 500_000, int Entries = 4096, int Seconds = 60);
public sealed record WorkbookRow(int Number, bool Hidden, ImmutableDictionary<int, SourceCell> Cells);
public sealed record WorkbookSheet(string Name, string Visibility, bool HasFilter, ImmutableArray<WorkbookRow> Rows);
public sealed record WorkbookData(string FilePath, string Sha256, bool Date1904,
    ImmutableArray<WorkbookSheet> Sheets, TimeSpan Duration)
{
    public int RowCount => Sheets.Sum(sheet => sheet.Rows.Length);
    public int HiddenRowCount => Sheets.Sum(sheet => sheet.Rows.Count(row => row.Hidden));
    public int FilteredSheetCount => Sheets.Count(sheet => sheet.HasFilter || !sheet.Visibility.Equals("visible", StringComparison.OrdinalIgnoreCase));
}

public sealed class WorkbookImportException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class WorkbookReader
{
    public Task<WorkbookData> ReadAsync(string path, CancellationToken token = default)
        => Task.Run(() => Read(path, new WorkbookLimits(), token), token);

    private static WorkbookData Read(string path, WorkbookLimits limits, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed.TotalSeconds > limits.Seconds)
                throw Fault("IMPORT_TIMEOUT", "Đọc workbook quá thời hạn. Hãy tách workbook nhỏ hơn.");
        }
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw Fault("UNSUPPORTED_EXTENSION", "Chỉ nhận file Excel .xlsx.");
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limits.CompressedBytes) throw Fault("WORKBOOK_TOO_LARGE", "Workbook vượt 32 MiB.");
        ValidatePackage(stream, limits, Check);
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false,
            new OpenSettings { AutoSave = false, MaxCharactersInPart = limits.ExpandedBytes });
        if (document.DocumentType != DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook)
            throw Fault("WORKBOOK_TYPE", "Chỉ hỗ trợ workbook .xlsx không chứa macro.");
        var part = document.WorkbookPart ?? throw Fault("WORKBOOK_INVALID", "Thiếu nội dung workbook.");
        var book = part.Workbook ?? throw Fault("WORKBOOK_INVALID", "Thiếu workbook.");
        if (part.ExternalWorkbookParts.Any() || part.VbaProjectPart is not null)
            throw Fault("EXTERNAL_CONTENT", "Workbook có macro hoặc liên kết ngoài. Hãy xuất dữ liệu thành giá trị tĩnh.");
        var sheets = book.Sheets?.Elements<Sheet>().ToArray() ?? [];
        if (sheets.Length is 0 || sheets.Length > limits.Sheets)
            throw Fault("SHEET_LIMIT", "Workbook cần có từ 1 đến 32 worksheet.");
        var shared = part.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>()
            .Select(s => s.Text?.Text ?? string.Concat(s.Elements<Run>().Select(r => r.Text?.Text))).ToArray() ?? [];
        var formats = part.WorkbookStylesPart?.Stylesheet?.CellFormats?.Elements<CellFormat>().ToArray() ?? [];
        var custom = part.WorkbookStylesPart?.Stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
            .ToDictionary(f => f.NumberFormatId!.Value, f => f.FormatCode?.Value ?? "") ?? [];
        var result = ImmutableArray.CreateBuilder<WorkbookSheet>();
        var totalCells = 0;
        var totalRows = 0;
        foreach (var sheet in sheets)
        {
            Check();
            var name = sheet.Name?.Value ?? throw Fault("SHEET_NAME", "Worksheet thiếu tên.");
            if (sheet.Id?.Value is not string id || part.GetPartById(id) is not WorksheetPart sheetPart)
                throw Fault("SHEET_TYPE", $"{name}: chỉ hỗ trợ worksheet dạng bảng dữ liệu.");
            var worksheet = sheetPart.Worksheet ?? throw Fault("SHEET_INVALID", $"{name}: thiếu worksheet.");
            var rows = new SortedDictionary<int, (bool Hidden, Dictionary<int, SourceCell> Cells)>();
            foreach (var row in worksheet.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
            {
                Check();
                if (++totalRows > limits.Rows) throw Fault("ROW_LIMIT", "Workbook vượt 100.000 dòng.");
                var number = checked((int)(row.RowIndex?.Value ?? 0));
                if (number is < 1 or > 1048576 || rows.ContainsKey(number))
                    throw Fault("ROW_INVALID", $"{name}: dòng trùng hoặc thiếu định danh.");
                var cells = new Dictionary<int, SourceCell>();
                foreach (var cell in row.Elements<Cell>())
                {
                    Check();
                    if (++totalCells > limits.Cells) throw Fault("CELL_LIMIT", "Workbook vượt 500.000 ô.");
                    var address = cell.CellReference?.Value ?? "";
                    var (column, cellRow) = CellAddress.Parse(address);
                    if (cellRow != number || cells.ContainsKey(column)) throw Fault("CELL_INVALID", $"{name}!{address}: ô trùng/sai dòng.");
                    var raw = cell.CellValue?.Text ?? "";
                    var kind = "number";
                    var value = raw;
                    if (cell.DataType?.Value == CellValues.SharedString)
                    {
                        kind = "text";
                        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= shared.Length)
                            throw Fault("SHARED_STRING_INVALID", $"{name}!{address}: shared string không hợp lệ.");
                        value = shared[index];
                    }
                    else if (cell.DataType?.Value == CellValues.InlineString)
                    {
                        kind = "text";
                        value = cell.InlineString?.Text?.Text ?? string.Concat(cell.InlineString?.Elements<Run>().Select(r => r.Text?.Text) ?? []);
                        raw = value;
                    }
                    else if (cell.DataType?.Value == CellValues.String) kind = "text";
                    else if (cell.DataType?.Value == CellValues.Date) kind = "date";
                    else if (cell.DataType?.Value == CellValues.Boolean) kind = "boolean";
                    else if (cell.DataType?.Value == CellValues.Error) kind = "error";
                    if (raw.Length > 32768 || value.Length > 32768) throw Fault("CELL_TEXT_LIMIT", $"{name}!{address}: giá trị ô quá dài.");
                    var style = cell.StyleIndex?.Value ?? 0;
                    if (style >= formats.Length && style != 0) throw Fault("STYLE_INVALID", $"{name}!{address}: style không hợp lệ.");
                    var formatId = style < formats.Length ? formats[style].NumberFormatId?.Value ?? 0 : 0;
                    var format = custom.GetValueOrDefault(formatId) ?? "builtin:" + formatId.ToString(CultureInfo.InvariantCulture);
                    cells[column] = new(name, number, column, address, raw, kind, format,
                        cell.CellFormula is null ? null : cell.CellFormula.Text.Length == 0 ? "[shared formula]" : cell.CellFormula.Text, value);
                }
                rows.Add(number, (row.Hidden?.Value == true, cells));
            }
            var mergedDestinations = new HashSet<(int Row, int Column)>();
            foreach (var merge in worksheet.Elements<MergeCells>().SelectMany(m => m.Elements<MergeCell>()))
            {
                Check();
                var range = (merge.Reference?.Value ?? "").Split(':');
                if (range.Length != 2) throw Fault("MERGE_INVALID", $"{name}: vùng gộp không hợp lệ.");
                var (left, top) = CellAddress.Parse(range[0]);
                var (right, bottom) = CellAddress.Parse(range[1]);
                if (right < left || bottom < top || (long)(right - left + 1) * (bottom - top + 1) > limits.Cells)
                    throw Fault("MERGE_LIMIT", $"{name}: vùng gộp vượt giới hạn.");
                var anchor = rows.TryGetValue(top, out var anchorRow) ? anchorRow.Cells.GetValueOrDefault(left) : null;
                for (var r = top; r <= bottom; r++)
                {
                    Check();
                    if (!rows.TryGetValue(r, out var entry))
                    {
                        if (++totalRows > limits.Rows) throw Fault("ROW_LIMIT", "Vùng gộp vượt giới hạn dòng.");
                        rows[r] = entry = (false, []);
                    }
                    for (var c = left; c <= right; c++)
                    {
                        if (!mergedDestinations.Add((r, c))) throw Fault("MERGE_OVERLAP", $"{name}: vùng gộp chồng nhau.");
                        if (r == top && c == left) continue;
                        if (entry.Cells.TryGetValue(c, out var previous) && !string.IsNullOrEmpty(previous.ParsedValue))
                            throw Fault("MERGE_CONFLICT", $"{name}!{previous.Address}: ô gộp có nhiều giá trị.");
                        if (++totalCells > limits.Cells) throw Fault("CELL_LIMIT", "Vùng gộp vượt giới hạn ô.");
                        if (anchor is not null) entry.Cells[c] = anchor with
                        { Row = r, Column = c, Address = CellAddress.Format(c, r), InheritedFrom = anchor.Address };
                    }
                }
            }
            result.Add(new(name, sheet.State?.Value.ToString() ?? "visible", worksheet.Elements<AutoFilter>().Any(),
                rows.Select(r => new WorkbookRow(r.Key, r.Value.Hidden, r.Value.Cells.ToImmutableDictionary())).ToImmutableArray()));
        }
        return new(full, hash, book.WorkbookProperties?.Date1904?.Value == true, result.ToImmutable(), timer.Elapsed);
    }

    private static void ValidatePackage(Stream stream, WorkbookLimits limits, Action check)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > limits.Entries) throw Fault("PACKAGE_LIMIT", "Workbook có quá nhiều phần.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in zip.Entries)
        {
            check();
            size = checked(size + entry.Length);
            if (size > limits.ExpandedBytes) throw Fault("EXPANSION_LIMIT", "Workbook giải nén vượt 128 MiB.");
            if (!names.Add(entry.FullName)) throw Fault("PACKAGE_DUPLICATE", "Workbook có phần trùng tên.");
            if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                using var content = entry.Open();
                using var reader = XmlReader.Create(content, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limits.ExpandedBytes });
                while (reader.Read()) check();
            }
        }
    }

    private static WorkbookImportException Fault(string code, string message) => new(code, message);

}

public static class CellAddress
{
    public static (int Column, int Row) Parse(string text)
    {
        var column = 0;
        var index = 0;
        while (index < text.Length && text[index] is >= 'A' and <= 'Z')
        {
            column = checked(column * 26 + text[index++] - 'A' + 1);
            if (column > 16384) throw new WorkbookImportException("CELL_ADDRESS", "Địa chỉ ô vượt cột XFD.");
        }
        if (column == 0 || !int.TryParse(text.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row is < 1 or > 1048576)
            throw new WorkbookImportException("CELL_ADDRESS", $"Địa chỉ ô không hợp lệ: {text}.");
        return (column, row);
    }
    public static string Format(int column, int row)
    {
        var text = "";
        while (column > 0) { column--; text = (char)('A' + column % 26) + text; column /= 26; }
        return text + row.ToString(CultureInfo.InvariantCulture);
    }
}
