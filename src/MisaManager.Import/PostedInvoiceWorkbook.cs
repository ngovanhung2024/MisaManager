using MisaManager.Core;

namespace MisaManager.Import;

/// <summary>
/// Reads MISA's "Danh sách bán hàng" export: a title row, a header with Số chứng từ/Số hóa đơn/Ký hiệu HĐ… and a
/// "Số dòng = N" footer. Every row with an invoice number is an invoice already posted in MISA.
/// </summary>
public static class PostedInvoiceWorkbook
{
    private static readonly string[] NumberHeaders = ["SO HOA DON", "SO HD"];
    private static readonly string[] SeriesHeaders = ["KY HIEU HD", "KY HIEU HOA DON", "KY HIEU"];
    private static readonly string[] VoucherHeaders = ["SO CHUNG TU", "SO CT"];

    public static async Task<IReadOnlyList<PostedInvoiceEntry>> ReadAsync(WorkbookReader reader, string path, CancellationToken token = default)
    {
        var workbook = await reader.ReadAsync(path, token);
        foreach (var sheet in workbook.Sheets)
        {
            foreach (var header in sheet.Rows.Take(30))
            {
                int? Column(string[] names) => header.Cells.Values.OrderBy(cell => cell.Column)
                    .Where(cell => names.Contains(PartyCatalogWorkbook.Plain(cell.ParsedValue))).Select(cell => (int?)cell.Column).FirstOrDefault();
                if (Column(NumberHeaders) is not { } number) continue;
                var series = Column(SeriesHeaders);
                var voucher = Column(VoucherHeaders);
                string Text(WorkbookRow row, int? column)
                    => column is { } index && row.Cells.TryGetValue(index, out var cell) ? cell.ParsedValue?.Trim() ?? "" : "";
                var entries = sheet.Rows.Where(row => row.Number > header.Number)
                    .Select(row => new PostedInvoiceEntry(Text(row, number), Text(row, series), Text(row, voucher)))
                    .Where(entry => entry.Number.Length > 0 && !PartyCatalogWorkbook.Plain(entry.Number).StartsWith("SO DONG", StringComparison.Ordinal))
                    .ToArray();
                if (entries.Length > 0) return entries;
            }
        }
        throw new WorkbookImportException("POSTED_INVOICE_HEADER_MISSING",
            "Không tìm thấy cột 'Số hóa đơn' có dữ liệu trong file (xuất từ danh sách chứng từ Bán hàng của MISA).");
    }
}
