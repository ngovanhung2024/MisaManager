using System.Globalization;

namespace MisaManager.Import;

/// <summary>
/// Sales invoices downloaded from the tax portal as one HTML page each (usually zipped). They are read like purchase
/// pages and written in the layout of the portal's old "Danh sách hóa đơn" Excel export, so the sales importer reads
/// them exactly as before.
/// </summary>
public static class SalesInvoiceArchive
{
    public const string SheetName = "HoaDon";

    public static readonly string[] Headers =
    [
        "STT", "Mẫu số", "Ký hiệu", "Số hóa đơn", "Ngày lập", "Người mua", "DN mua", "MST người mua", "Địa chỉ người mua",
        "Tên hàng hóa", "Đơn vị tính", "Đơn giá", "Số lượng", "Tổng tiền chưa thuế", "Thuế hàng hóa", "Tiền thuế chi tiết", "Tổng tiền có thuế"
    ];

    public static PurchaseArchiveResult Read(string path, CancellationToken token = default) => PurchaseInvoiceArchive.Read(path, token);

    public static void WriteWorkbook(string path, IReadOnlyList<PurchaseHtmlInvoice> invoices)
    {
        var index = 0;
        PurchaseInvoiceArchive.WriteSheet(path, SheetName, Headers, invoices.SelectMany(invoice => invoice.Lines.Select(line =>
        {
            var (person, organization) = BuyerColumns(invoice);
            return (IEnumerable<object?>)
            [
                (++index).ToString(CultureInfo.InvariantCulture), invoice.Template, invoice.Series, invoice.Number,
                invoice.Issued.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), person, organization,
                invoice.BuyerTaxCode.Length > 0 ? invoice.BuyerTaxCode : invoice.BuyerIdNumber, invoice.BuyerAddress,
                line.Name, line.Unit, line.UnitPrice, line.Quantity, line.Amount, TaxRate(line.TaxRate), line.TaxAmount,
                line.Amount + line.TaxAmount
            ];
        })));
    }

    /// <summary>
    /// Old export: "Người mua" is the person (or the only name), "DN mua" the organisation when a person is named too;
    /// the importer then enters the organisation as customer and keeps the person as contact.
    /// </summary>
    private static (string Person, string Organization) BuyerColumns(PurchaseHtmlInvoice invoice)
        => invoice.BuyerPerson.Length > 0
            ? (invoice.BuyerPerson, invoice.BuyerOrganization)
            : (invoice.BuyerOrganization.Length > 0 ? invoice.BuyerOrganization : invoice.Buyer, "");

    // The old export wrote the rate as a number (5, 8, 10); "KCT"/"KKKNT" stay text.
    private static object? TaxRate(string rate)
        => PurchaseInvoiceArchive.RateKey(rate) is { } key && decimal.TryParse(key, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value : rate.Trim();
}
