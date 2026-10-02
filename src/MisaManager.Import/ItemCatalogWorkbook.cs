using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MisaManager.Core;

namespace MisaManager.Import;

/// <summary>Reads the MISA "Vật tư, hàng hóa, dịch vụ" export (Mã, Tên, ĐVT chính, Đơn vị chuyển đổi).</summary>
public static class ItemCatalogWorkbook
{
    private static readonly string[] CodeHeaders = ["ma", "ma hang", "ma hang hoa", "ma vat tu", "ma vthh"];
    private static readonly string[] NameHeaders = ["ten", "ten hang", "ten hang hoa", "ten vat tu", "ten vat tu hang hoa", "ten vthh"];
    private static readonly string[] UnitHeaders = ["dvt chinh", "dvt", "don vi tinh", "don vi tinh chinh"];
    private static readonly string[] ConversionHeaders = ["don vi chuyen doi", "dvt chuyen doi"];

    public static async Task<IReadOnlyList<ItemCatalogEntry>> ReadAsync(WorkbookReader reader, string path, CancellationToken token = default)
    {
        var workbook = await reader.ReadAsync(path, token);
        foreach (var sheet in workbook.Sheets)
        {
            foreach (var header in sheet.Rows.Take(30))
            {
                int? Column(string[] names) => header.Cells.Values
                    .Where(cell => names.Contains(Normalize(cell.ParsedValue))).Select(cell => (int?)cell.Column).FirstOrDefault();
                if (Column(CodeHeaders) is not { } code || Column(NameHeaders) is not { } name) continue;
                var unit = Column(UnitHeaders);
                var conversion = Column(ConversionHeaders);
                string Text(WorkbookRow row, int? column)
                    => column is { } index && row.Cells.TryGetValue(index, out var cell) ? cell.ParsedValue?.Trim() ?? "" : "";
                var entries = sheet.Rows.Where(row => row.Number > header.Number)
                    .Select(row => new ItemCatalogEntry(Text(row, code), Text(row, name), Text(row, unit), Text(row, conversion)))
                    // The export ends with a "Số dòng = N" footer in the code column.
                    .Where(entry => entry.Code.Length > 0 && entry.Name.Length > 0
                        && !Normalize(entry.Code).StartsWith("so dong", StringComparison.Ordinal))
                    .ToArray();
                if (entries.Length > 0) return entries;
            }
        }
        throw new WorkbookImportException("ITEM_CATALOG_HEADER_MISSING",
            "Không tìm thấy cột 'Mã' và 'Tên' trong file mã hàng (xuất từ danh mục Vật tư, hàng hóa của MISA).");
    }

    private static string Normalize(string? value) => Regex.Replace(new string((value ?? "").Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray())
        .Replace('đ', 'd'), @"\s+", " ").Trim();
}
