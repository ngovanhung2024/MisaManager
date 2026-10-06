using MisaManager.Core;

namespace MisaManager.Import;

/// <summary>
/// Reads MISA's "Danh sách nhóm vật tư, hàng hóa, dịch vụ" export: a title row, a header with Mã nhóm/Tên nhóm/Ngừng
/// theo dõi and a "Số dòng = N" footer.
/// </summary>
public static class ItemGroupWorkbook
{
    private static readonly string[] CodeHeaders = ["MA NHOM VAT TU, HANG HOA, DICH VU", "MA NHOM VTHH", "MA NHOM"];
    private static readonly string[] NameHeaders = ["TEN NHOM VAT TU, HANG HOA, DICH VU", "TEN NHOM VTHH", "TEN NHOM"];
    private static readonly string[] StoppedHeaders = ["NGUNG THEO DOI"];

    public static async Task<IReadOnlyList<ItemGroupEntry>> ReadAsync(WorkbookReader reader, string path, CancellationToken token = default)
    {
        var workbook = await reader.ReadAsync(path, token);
        foreach (var sheet in workbook.Sheets)
        {
            foreach (var header in sheet.Rows.Take(30))
            {
                int? Column(string[] names) => header.Cells.Values.OrderBy(cell => cell.Column)
                    .Where(cell => names.Contains(PartyCatalogWorkbook.Plain(cell.ParsedValue))).Select(cell => (int?)cell.Column).FirstOrDefault();
                if (Column(CodeHeaders) is not { } code) continue;
                var name = Column(NameHeaders);
                var stopped = Column(StoppedHeaders);
                string Text(WorkbookRow row, int? column)
                    => column is { } index && row.Cells.TryGetValue(index, out var cell) ? cell.ParsedValue?.Trim() ?? "" : "";
                var entries = sheet.Rows.Where(row => row.Number > header.Number)
                    // A stopped group cannot be chosen in MISA, so its prefix must not be used for new codes.
                    .Where(row => PartyCatalogWorkbook.Plain(Text(row, stopped)) is not ("1" or "TRUE" or "CO" or "X"))
                    .Select(row => new ItemGroupEntry(Text(row, code), Text(row, name)))
                    .Where(entry => entry.Code.Length > 0 && !PartyCatalogWorkbook.Plain(entry.Code).StartsWith("SO DONG", StringComparison.Ordinal))
                    .ToArray();
                if (entries.Length > 0) return entries;
            }
        }
        throw new WorkbookImportException("ITEM_GROUP_HEADER_MISSING",
            "Không tìm thấy cột 'Mã nhóm vật tư, hàng hóa, dịch vụ' trong file (xuất từ danh mục Nhóm vật tư, hàng hóa, dịch vụ của MISA).");
    }
}
