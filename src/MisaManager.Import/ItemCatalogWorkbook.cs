using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MisaManager.Core;

namespace MisaManager.Import;

/// <summary>Reads the MISA "Vật tư, hàng hóa, dịch vụ" export (Mã, Tên, ĐVT chính, Đơn vị chuyển đổi, Tỷ lệ chuyển đổi, Phép tính).</summary>
public static class ItemCatalogWorkbook
{
    private static readonly string[] CodeHeaders = ["ma", "ma hang", "ma hang hoa", "ma vat tu", "ma vthh"];
    private static readonly string[] NameHeaders = ["ten", "ten hang", "ten hang hoa", "ten vat tu", "ten vat tu hang hoa", "ten vthh"];
    private static readonly string[] UnitHeaders = ["dvt chinh", "dvt", "don vi tinh", "don vi tinh chinh"];
    private static readonly string[] ConversionHeaders = ["don vi chuyen doi", "dvt chuyen doi"];
    private static readonly string[] RateHeaders = ["ty le chuyen doi ve don vi chinh", "ty le chuyen doi"];
    private static readonly string[] OperationHeaders = ["phep tinh", "phep tinh chuyen doi"];

    public static async Task EnsureImportedProvenanceAsync(WorkbookReader reader, ItemCatalogStore catalog,
        CancellationToken token = default)
    {
        if (catalog.HasImportedProvenance || catalog.SourceFile is not { Length: > 0 } source) return;
        try
        {
            // Older databases mixed export rows and generated codes without recording their source.
            var exported = await ReadAsync(reader, source, token).ConfigureAwait(false);
            catalog.Merge(exported, source, reconcilePending: false);
        }
        catch (Exception error) when (error is WorkbookImportException or IOException or UnauthorizedAccessException)
        {
            throw new AutomationException("ITEM_CATALOG_SOURCE_UNAVAILABLE",
                "Chưa xác định được mã nào thuộc file danh mục gốc. Nạp lại file mã hàng trước khi nhập; không dùng mã giữ lại trong cache. "
                + error.Message, false, error);
        }
    }

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
                var rate = Column(RateHeaders);
                var operation = Column(OperationHeaders);
                string Text(WorkbookRow row, int? column)
                    => column is { } index && row.Cells.TryGetValue(index, out var cell) ? cell.ParsedValue?.Trim() ?? "" : "";
                // A ratio only describes a single conversion unit; with several listed it cannot be attributed.
                decimal Rate(WorkbookRow row)
                    => Text(row, conversion).IndexOfAny([',', ';', '|']) < 0 && DecimalValue.TryParseInvariant(Text(row, rate), out var value)
                        && value > 0 ? value : 0;
                var entries = sheet.Rows.Where(row => row.Number > header.Number)
                    .Select(row => new ItemCatalogEntry(Text(row, code), Text(row, name), Text(row, unit), Text(row, conversion),
                        ConversionRate: Rate(row), ConversionOperation: Text(row, operation)))
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
