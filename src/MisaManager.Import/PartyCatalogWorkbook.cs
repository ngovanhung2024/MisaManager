using MisaManager.Core;

namespace MisaManager.Import;

/// <summary>
/// Reads MISA's "Danh sách khách hàng" / "Danh sách nhà cung cấp" export: a title row, then a header with
/// Mã/Tên/Địa chỉ/Mã số thuế/Ngừng theo dõi and a "Số dòng = N" footer.
/// </summary>
public static class PartyCatalogWorkbook
{
    private static readonly string[] CodeHeaders = ["MA KHACH HANG", "MA NHA CUNG CAP", "MA DOI TUONG", "MA KH", "MA NCC", "MA"];
    private static readonly string[] NameHeaders = ["TEN KHACH HANG", "TEN NHA CUNG CAP", "TEN DOI TUONG", "TEN KH", "TEN NCC", "TEN"];
    private static readonly string[] TaxHeaders = ["MA SO THUE", "MST"];
    private static readonly string[] AddressHeaders = ["DIA CHI"];
    private static readonly string[] StoppedHeaders = ["NGUNG THEO DOI"];

    public static async Task<IReadOnlyList<PartyCatalogEntry>> ReadAsync(WorkbookReader reader, string path, PartyRole role,
        CancellationToken token = default)
    {
        var workbook = await reader.ReadAsync(path, token);
        foreach (var sheet in workbook.Sheets)
        {
            foreach (var header in sheet.Rows.Take(30))
            {
                int? Column(string[] names) => header.Cells.Values.OrderBy(cell => cell.Column)
                    .Where(cell => names.Contains(Plain(cell.ParsedValue))).Select(cell => (int?)cell.Column).FirstOrDefault();
                if (Column(CodeHeaders) is not { } code || Column(NameHeaders) is not { } name) continue;
                var tax = Column(TaxHeaders);
                var address = Column(AddressHeaders);
                var stopped = Column(StoppedHeaders);
                string Text(WorkbookRow row, int? column)
                    => column is { } index && row.Cells.TryGetValue(index, out var cell) ? cell.ParsedValue?.Trim() ?? "" : "";
                var entries = sheet.Rows.Where(row => row.Number > header.Number)
                    // MISA keeps stopped objects in the export; they cannot be selected on a voucher.
                    .Where(row => !IsTrue(Text(row, stopped)))
                    .Select(row => new PartyCatalogEntry(Text(row, code), Text(row, name), Text(row, tax), Text(row, address)))
                    .Where(entry => entry.Code.Length > 0 && entry.Name.Length > 0 && !Plain(entry.Code).StartsWith("SO DONG", StringComparison.Ordinal))
                    .ToArray();
                if (entries.Length > 0) return entries;
            }
        }
        throw new WorkbookImportException("PARTY_CATALOG_HEADER_MISSING",
            $"Không tìm thấy cột 'Mã {PartyCatalogStore.Label(role)}' và 'Tên {PartyCatalogStore.Label(role)}' trong file "
            + $"(xuất từ Danh mục {PartyCatalogStore.Label(role)} của MISA).");
    }

    private static bool IsTrue(string value) => Plain(value) is "1" or "TRUE" or "CO" or "X";

    // Header matching ignores marks and Đ/đ: export headers differ between MISA builds only in such details.
    private static string Plain(string? value)
        => new(TextKey.Canonical(value).Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => c == 'Đ' ? 'D' : c).ToArray());
}
