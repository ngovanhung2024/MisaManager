using System.IO.Compression;
using System.Net;
using System.Text;
using MisaManager.Core.Tests.TestData;
using MisaManager.Import;

namespace MisaManager.Core.Tests;

public sealed class SalesInvoiceArchiveTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Tax portal "bản thể hiện" of an own sales invoice (seller = our company).</summary>
    private static string Html(string number, int day, (string Label, string Value)[] buyer,
        (string Name, string Unit, string Quantity, string Price, string Amount)[] lines, string tax, string total, string template = "1")
    {
        static string Encode(string text) => WebUtility.HtmlEncode(text);
        static string Item(string label, string value)
            => $"<li><div class=\"data-item\"><div class=\"di-label\"><span>{Encode(label)}</span></div><div class=\"di-value\"><div>{Encode(value)}</div></div></div></li>";
        var html = new StringBuilder()
            .Append("<html><body><div class=\"code-content\">")
            .Append($"<b>{Encode("Mẫu số")}: {template}</b><br><b>{Encode("Ký hiệu")}: C26MLV</b><br><b>{Encode("Số")}: {number}</b></div>")
            .Append($"<p>{Encode($"Ngày {day:00} tháng 10 năm 2026")}</p><ul>")
            .Append(Item("Tên người bán:", "CÔNG TY TNHH THƯƠNG MẠI MẪU")).Append(Item("Mã số thuế:", "3301000999"))
            .Append(Item("Địa chỉ:", "Xã Lộc An"));
        foreach (var (label, value) in buyer) html.Append(Item(label, value));
        html.Append("</ul><table class=\"res-tb\"><thead><tr>");
        foreach (var header in new[] { "STT", "Tính chất", "Loại hàng hoá đặc trưng", "Tên hàng hóa, dịch vụ", "Đơn vị tính", "Số lượng",
                     "Đơn giá", "Chiết khấu", "Thuế suất", "Thành tiền chưa có thuế GTGT" })
            html.Append($"<th>{Encode(header)}</th>");
        html.Append("</tr></thead><tbody>");
        var index = 0;
        foreach (var line in lines)
            html.Append($"<tr><td>{++index}</td><td>{Encode("Hàng hóa, dịch vụ")}</td><td></td><td>{Encode(line.Name)}</td><td>{Encode(line.Unit)}</td>")
                .Append($"<td>{line.Quantity}</td><td>{line.Price}</td><td>0</td><td>5%</td><td>{line.Amount}</td></tr>");
        html.Append("</tbody></table><table class=\"res-tb\"><thead><tr><th>Thuế suất</th><th>Tổng tiền chưa thuế</th><th>Tổng tiền thuế</th></tr></thead><tbody>")
            .Append($"<tr><td>5%</td><td></td><td>{tax}</td></tr></tbody></table><table class=\"res-tb\"><tbody>")
            .Append($"<tr><td>{Encode("Tổng tiền thanh toán bằng số")}</td><td>{total}</td></tr></tbody></table></body></html>");
        return html.ToString();
    }

    private static (string, string)[] Buyer(string organization, string person = "", string tax = "", string id = "", string address = "")
        => [("Tên người mua:", organization), ("Họ tên người mua:", person), ("Mã số thuế:", tax), ("CCCD người mua:", id), ("Địa chỉ:", address),
            ("Hình thức thanh toán:", "Tiền mặt/Chuyển khoản")];

    [Fact]
    public void WorkbookHasTheOldExportColumns()
    {
        var invoice = SalesInvoiceArchive.Read(Zip(("a.html", Html("1805", 5,
            Buyer("CÔNG TY TNHH LÂM NGHIỆP MẪU", "Trần Văn Mẫu", "3300100999", address: "48 Đại Nam"),
            [("Bassa 50EC - Chai 90ml", "Chai", "28", "16.000", "448.000")], "22.400", "470.400"))), Token).Invoices.Single();
        Assert.Equal(("1", "C26MLV", "CÔNG TY TNHH LÂM NGHIỆP MẪU", "Trần Văn Mẫu", "48 Đại Nam"),
            (invoice.Template, invoice.Series, invoice.BuyerOrganization, invoice.BuyerPerson, invoice.BuyerAddress));
    }

    private string Zip(params (string Name, string Html)[] pages)
    {
        var zip = temp.Combine(Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, html) in pages)
        {
            using var writer = new StreamWriter(archive.CreateEntry("html/" + name).Open(), new UTF8Encoding(false));
            writer.Write(html);
        }
        return zip;
    }

    private async Task<ImportResult> Import(params (string Name, string Html)[] pages)
    {
        var read = SalesInvoiceArchive.Read(Zip(pages), Token);
        var workbook = temp.Combine("ban-ra.xlsx");
        SalesInvoiceArchive.WriteWorkbook(workbook, read.Invoices);
        return await new InvoiceWorkbookImporter(new WorkbookReader()).ImportAsync(workbook, null, Token);
    }

    [Fact]
    public async Task PortalPagesImportLikeTheOldInvoiceListExcel()
    {
        var result = await Import(
            ("3301000999_1_C26MLV_1824_dung_a.html", Html("1824", 6, Buyer("BÁN CHO NGƯỜI TIÊU DÙNG"),
                [("Phân NPK 16-16-8+13S HCQN (BM-BV) (50Kg)", "Kg", "150", "13.333,33", "2.000.000"),
                    ("COMPASS 60OD-100CC PET", "Chai", "37", "41.904,76", "1.550.476"), ("Dylan 10WG - 10gr", "Gói", "66", "7.619", "502.854")],
                "202.667", "4.255.997")),
            ("3301000999_1_C26MLV_1805_dung_b.html", Html("1805", 5,
                Buyer("CÔNG TY TNHH LÂM NGHIỆP MẪU", "Trần Văn Mẫu", "3300100999", address: "48 Đại Nam, Thành Phố Huế"),
                [("NÓN TAI BÈO", "Cái", "1", "0", "0"), ("Bassa 50EC - Chai 90ml", "Chai", "28", "16.000", "448.000")], "22.400", "470.400")),
            ("3301000999_1_C26MLV_1880_dung_c.html", Html("1880", 7, Buyer("Lê Thị Mẫu", id: "046172000999", address: "Xã Nam Đông"),
                [("Ni-tin 300EC - 10ml", "Cốc", "39", "7.000", "273.000")], "13.650", "286.650", template: "2")),
            ("3301000999_1_C26MLV_1881_huy_d.html", Html("1881", 7, Buyer("BÁN CHO NGƯỜI TIÊU DÙNG"),
                [("Ni-tin 300EC - 10ml", "Cốc", "1", "7.000", "7.000")], "350", "7.350")));

        Assert.Equal(["1805", "1824", "1880"], result.Invoices.Select(invoice => invoice.Identity.Number));
        // "Mẫu số" is read per invoice (typed into MISA "Mẫu số HĐ"): 1 or 2.
        Assert.Equal(["1", "1", "2"], result.Invoices.Select(invoice => invoice.Template));
        Assert.All(result.Invoices, invoice =>
        {
            Assert.False(invoice.IsBlocked, string.Join("; ", invoice.Issues.Select(issue => issue.Message)));
            Assert.False(invoice.IsPurchase());
        });
        var consumer = result.Invoices.Single(invoice => invoice.Identity.Number == "1824");
        Assert.Equal(("NTD", new DateOnly(2026, 10, 6)), (consumer.Buyer.Code, consumer.Issued!.Value));
        // Per-line tax: amount × 5% rounded, matching the old export (100.000 + 77.524 + 25.143).
        Assert.Equal([100_000m, 77_524m, 25_143m], consumer.Lines.Select(line => line.TaxAmount!.Value));
        Assert.Equal(4_255_997m, consumer.GrandTotal);
        var company = result.Invoices.Single(invoice => invoice.Identity.Number == "1805");
        Assert.Equal(("CÔNG TY TNHH LÂM NGHIỆP MẪU", "3300100999", "Trần Văn Mẫu"),
            (company.Buyer.Name, company.Buyer.TaxCode, company.Buyer.Contact));
        Assert.Equal((0m, 0m), (company.Lines[0].UnitPrice!.Value, company.Lines[0].AmountBeforeTax!.Value));
        // An individual without MST: the old export put the CCCD in "MST người mua".
        var person = result.Invoices.Single(invoice => invoice.Identity.Number == "1880");
        Assert.Equal(("Lê Thị Mẫu", "046172000999"), (person.Buyer.Name, person.Buyer.TaxCode));
    }
}
