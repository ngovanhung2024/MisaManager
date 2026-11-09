using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MisaManager.Core;

namespace MisaManager.Import;

/// <summary>
/// Reads MISA's "Tổng hợp tồn kho" report: title rows with "Từ ngày dd/MM/yyyy", a two-row header (Mã hàng, Tên hàng, ĐVT,
/// Đầu kỳ → Số lượng/Giá trị, Nhập kho, Xuất kho, Cuối kỳ), "Tên kho : …" group rows and a "Số dòng = N" footer.
/// </summary>
public static partial class OpeningStockWorkbook
{
    public static async Task<(IReadOnlyList<OpeningStockEntry> Items, DateOnly? AsOf)> ReadAsync(WorkbookReader reader, string path,
        CancellationToken token = default)
    {
        var workbook = await reader.ReadAsync(path, token);
        foreach (var sheet in workbook.Sheets)
        {
            foreach (var header in sheet.Rows.Take(30))
            {
                int[] Columns(WorkbookRow? row, params string[] names) => row is null ? [] : row.Cells.Values
                    .Where(cell => names.Contains(Normalize(cell.ParsedValue))).Select(cell => cell.Column).Order().ToArray();
                if (Columns(header, "ma hang", "ma vat tu", "ma") is not [var code, ..] || Columns(header, "dau ky") is not [var opening, ..])
                    continue;
                var name = Columns(header, "ten hang", "ten vat tu").FirstOrDefault();
                var unit = Columns(header, "dvt", "don vi tinh").FirstOrDefault();
                // "Đầu kỳ" spans Số lượng/Giá trị on the next row; the first column after it with another heading ends the span.
                var sub = sheet.Rows.FirstOrDefault(row => row.Number == header.Number + 1);
                var end = header.Cells.Values.Where(cell => cell.Column > opening && cell.InheritedFrom is null
                    && Normalize(cell.ParsedValue) is { Length: > 0 } text && text != "dau ky").Select(cell => cell.Column).DefaultIfEmpty(int.MaxValue).Min();
                var quantity = Columns(sub, "so luong").FirstOrDefault(column => column >= opening && column < end, opening);
                var value = Columns(sub, "gia tri").FirstOrDefault(column => column >= opening && column < end, 0);
                var first = sub is not null && Columns(sub, "so luong").Length > 0 ? sub.Number : header.Number;
                string Text(WorkbookRow row, int column)
                    => column > 0 && row.Cells.TryGetValue(column, out var cell) ? cell.ParsedValue?.Trim() ?? "" : "";
                var entries = sheet.Rows.Where(row => row.Number > first)
                    .Select(row => (Row: row, Code: Text(row, code)))
                    .Where(entry => entry.Code.Length > 0 && !Normalize(entry.Code).StartsWith("so dong", StringComparison.Ordinal)
                        && !Normalize(entry.Code).StartsWith("ten kho", StringComparison.Ordinal))
                    .Select(entry => new OpeningStockEntry(entry.Code, Text(entry.Row, name), Text(entry.Row, unit),
                        Number(Text(entry.Row, quantity)), Number(Text(entry.Row, value))))
                    .ToArray();
                if (entries.Length == 0) continue;
                var asOf = sheet.Rows.Where(row => row.Number < header.Number).SelectMany(row => row.Cells.Values)
                    .Select(cell => FromDate().Match(Normalize(cell.ParsedValue))).Where(match => match.Success)
                    .Select(match => DateOnly.TryParseExact($"{match.Groups[1].Value.PadLeft(2, '0')}/{match.Groups[2].Value.PadLeft(2, '0')}/{match.Groups[3].Value}",
                        "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : (DateOnly?)null)
                    .FirstOrDefault(date => date is not null);
                return (entries, asOf);
            }
        }
        throw new WorkbookImportException("OPENING_STOCK_HEADER_MISSING",
            "Không tìm thấy cột 'Mã hàng' và 'Đầu kỳ' trong file (báo cáo Tổng hợp tồn kho xuất từ MISA).");
    }

    // Number cells come as invariant text; a report saved as text uses Vietnamese separators ("15.900,000").
    private static decimal Number(string text)
    {
        if (text.Length == 0) return 0;
        if (DecimalValue.TryParseInvariant(text, out var value)) return value;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("vi-VN"), out value) ? value : 0;
    }

    private static string Normalize(string? value) => Regex.Replace(new string((value ?? "").Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray())
        .Replace('đ', 'd'), @"\s+", " ").Trim();

    [GeneratedRegex(@"tu ngay (\d{1,2})/(\d{1,2})/(\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex FromDate();
}
