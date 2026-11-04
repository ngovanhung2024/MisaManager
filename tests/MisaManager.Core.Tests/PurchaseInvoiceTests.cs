using System.IO.Compression;
using System.Net;
using System.Text;
using MisaManager.Core.Tests.TestData;
using MisaManager.Import;

namespace MisaManager.Core.Tests;

public sealed class PurchaseInvoiceTests : IDisposable
{
    private const string Supplier = "CÔNG TY CỔ PHẦN VẬT TƯ NÔNG NGHIỆP MẪU";
    private const string SupplierTax = "0400100999";
    private const string OtherSupplier = "CÔNG TY TNHH THUỐC BẢO VỆ THỰC VẬT MẪU";
    private const string OtherTax = "0100101999-008";
    private const string Bank = "Ngân hàng TMCP Mẫu - Chi nhánh Huế";
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Row(string Kind, string Name, string Unit, string Quantity, string Price, string Rate, string Amount);

    private static Row Goods(string name, string unit, string quantity, string price, string amount, string rate = "5%")
        => new("Hàng hóa, dịch vụ", name, unit, quantity, price, rate, amount);

    /// <summary>Same markup as the tax portal's "bản thể hiện" page, with HTML 5 entities like the real files.</summary>
    private static string Html(string number, int day, int month, string seller, string sellerTax, IEnumerable<Row> rows,
        IEnumerable<(string Rate, string Before, string Tax)> taxes, string total, string series = "C26TAA")
    {
        static string Encode(string text) => WebUtility.HtmlEncode(text).Replace("ă", "&abreve;", StringComparison.Ordinal);
        static string Item(string label, string value)
            => $"<li><div class=\"data-item\"><div class=\"di-label\"><span>{Encode(label)}</span></div><div class=\"di-value\"><div>{Encode(value)}</div></div></div></li>";
        var html = new StringBuilder()
            .Append("<html><head><meta charset=\"UTF-8\"><title>Hóa đơn</title><style>.x{}</style></head><body>")
            .Append("<div class=\"top-content\"><div id=\"qrcodeTable\"><svg><path d=\"M0\"/></svg></div><div class=\"code-content\">")
            .Append($"<b>{Encode("Mẫu số")}: 1</b><br><b>{Encode("Ký hiệu")}: {series}</b><br><b>{Encode("Số")}: {number}</b></div></div>")
            .Append($"<p class=\"day\">{Encode($"Ngày {day:00} tháng {month:00} năm 2026")}</p>")
            .Append("<img src=\"data:image/png;base64,AAAA\"/><ul class=\"list-fill-out\">")
            .Append(Item("Tên người bán:", seller)).Append(Item("Mã số thuế:", sellerTax)).Append(Item("Địa chỉ:", "Thành phố Huế"))
            .Append(Item("Tên người mua:", "CÔNG TY TNHH THƯƠNG MẠI MẪU")).Append(Item("Mã số thuế:", "3301000999"))
            .Append(Item("Địa chỉ:", "Xã Lộc An")).Append("</ul>")
            .Append("<table class=\"res-tb\"><thead><tr>");
        foreach (var header in new[] { "STT", "Tính chất", "Loại hàng hoá đặc trưng", "Tên hàng hóa, dịch vụ", "Đơn vị tính", "Số lượng",
                     "Đơn giá", "Chiết khấu", "Thuế suất", "Thành tiền chưa có thuế GTGT" })
            html.Append($"<th>{Encode(header)}</th>");
        html.Append("</tr></thead><tbody>");
        var index = 0;
        foreach (var row in rows)
            html.Append($"<tr><td>{++index}</td><td><span>{Encode(row.Kind)}</span></td><td></td><td>{Encode(row.Name)}</td><td>{Encode(row.Unit)}</td>")
                .Append($"<td>{row.Quantity}</td><td>{row.Price}</td><td></td><td>{row.Rate}</td><td>{row.Amount}</td></tr>");
        html.Append("</tbody></table><table class=\"res-tb\"><thead><tr><th>Thuế suất</th><th>Tổng tiền chưa thuế</th><th>Tổng tiền thuế</th></tr></thead><tbody>");
        foreach (var (rate, before, tax) in taxes) html.Append($"<tr><td>{rate}</td><td>{before}</td><td>{tax}</td></tr>");
        html.Append("</tbody></table><table class=\"res-tb\"><tbody>")
            .Append($"<tr><td>{Encode("Tổng tiền thanh toán bằng số")}</td><td>{total}</td></tr></tbody></table></body></html>");
        return html.ToString();
    }

    private static string Fertilizer(string number, int day, int month, string seller = Supplier, string tax = SupplierTax)
        => Html(number, day, month, seller, tax,
            [Goods("Phân NPK 16-16-8+13S (Bao 50kg)", "Tấn", "2", "13.809.524", "27.619.048"),
                Goods("Phân Ure Đạm Phú Mỹ loại 1 (bao)", "Tấn", "1", "12.666.667", "12.666.667")],
            [("5%", "40.285.715", "2.014.286")], "42.300.001");

    private string Zip(params (string Name, string Html)[] pages)
    {
        var path = temp.Combine("html.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, html) in pages)
        {
            using var writer = new StreamWriter(zip.CreateEntry("html/" + name).Open(), new UTF8Encoding(false));
            writer.Write(html);
        }
        return path;
    }

    private async Task<ImportResult> ImportArchive(string zip)
    {
        var archive = PurchaseInvoiceArchive.Read(zip, Token);
        var workbook = temp.Combine("mua-vao.xlsx");
        PurchaseInvoiceArchive.WriteWorkbook(workbook, archive.Invoices);
        return await new InvoiceWorkbookImporter(new WorkbookReader(), purchase: true).ImportAsync(workbook, null, Token);
    }

    [Theory]
    [InlineData("Phân NPK 16-16-8+13S (Bao 50kg)", "Tấn", "2")]
    [InlineData("Bassa 50EC - Chai 90 ml", "Chai", "400")]
    [InlineData("Phân hữu cơ Phì Nhiêu", "Kg", "100")]
    [InlineData("Ni-tin 300EC 10ml (Cốc)", "Cốc", "5")]
    [InlineData("Thau nhựa (Hàng khuyến mãi không thu tiền)", "Cái", "20")]
    public void GoodsAreEntered(string name, string unit, string quantity)
        => Assert.False(ItemNatures.Classify(name, unit, decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)).IsService);

    [Theory]
    [InlineData("Phí dịch vụ ngân hàng", "", null)]
    [InlineData("Chi phí mua hàng", "Lần", "1")]
    [InlineData("Vé máy bay Hà Nội - Huế", "Vé", "2")]
    [InlineData("Xăng RON 95-III", "Lít", "40")]
    [InlineData("Tiền điện tháng 6/2026", "kWh", "300")]
    [InlineData("Nước sinh hoạt tháng 6", "m3", "12")]
    [InlineData("Cơm chiên Thái Gà", "Phần", "1")]
    [InlineData("Điện thoại iPhone 16 Pro", "Chiếc", "1")]
    [InlineData("Laptop Dell Latitude 5440", "Cái", "1")]
    [InlineData("Thanh toán đợt 1 theo hợp đồng số 23/2026/HDDV", "", null)]
    [InlineData("Phần mềm hóa đơn điện tử - Gói 30.000 hóa đơn", "gói", "1")]
    public void ServicesAndExpensesAreNotEntered(string name, string unit, string? quantity)
        => Assert.True(ItemNatures.Classify(name, unit, quantity is null ? null : decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)).IsService);

    [Fact]
    public void TheClassificationColumnOverridesTheAutomaticDecision()
    {
        Assert.False(ItemNatures.Classify("Phí dịch vụ ngân hàng", "", null, "Hàng hóa").IsService);
        Assert.True(ItemNatures.Classify("Phân NPK 16-16-8", "Kg", 10, "Dịch vụ").IsService);
        Assert.Equal("dịch vụ", ItemNatures.Classify("Phí dịch vụ ngân hàng", "", null, "Dịch vụ").Reason);
    }

    [Fact]
    public void PortalPageIsReadWithTaxSpreadOverTheLines()
    {
        var invoice = PurchaseInvoiceArchive.Parse(Fertilizer("869", 30, 6), "a.html");

        Assert.Equal(("1", "C26TAA", "869", new DateOnly(2026, 6, 30)), (invoice.Template, invoice.Series, invoice.Number, invoice.Issued));
        Assert.Equal((Supplier, SupplierTax), (invoice.Seller, invoice.SellerTaxCode));
        Assert.Equal("3301000999", invoice.BuyerTaxCode);
        Assert.Equal([27_619_048m, 12_666_667m], invoice.Lines.Select(line => line.Amount));
        // 1.380.952,4 + 633.333,35 rounds to 2.014.285; the invoice says 2.014.286, so the largest line takes the đồng.
        Assert.Equal([1_380_953m, 633_333m], invoice.Lines.Select(line => line.TaxAmount));
        Assert.Equal(42_300_001m, invoice.StatedTotal);
        Assert.Equal(invoice.StatedTotal, invoice.Subtotal + invoice.TaxTotal);
    }

    [Fact]
    public void DiscountLinesAreNegativeAndGiftsCostNothing()
    {
        var invoice = PurchaseInvoiceArchive.Parse(Html("1545", 5, 1, OtherSupplier, OtherTax,
            [Goods("Catex 3.6EC 100ml", "Chai", "100", "20.000", "2.000.000"),
                Goods("Bia Huda lon 24 x 330ml", "Thùng", "2", "", "0", "0%") with { Kind = "Khuyến mại" },
                new("Chiết khấu thương mại", "Chiết khấu giảm giá", "", "0", "0", "5%", "100.000")],
            [("5%", "1.900.000", "95.000")], "1.995.000"), "b.html");

        Assert.Equal([2_000_000m, 0m, -100_000m], invoice.Lines.Select(line => line.Amount));
        Assert.Equal(0m, invoice.Lines[1].UnitPrice);
        Assert.Equal(95_000m, invoice.TaxTotal);
    }

    [Fact]
    public void ArchiveIsSortedFromTheStartOfTheYearWithoutDuplicates()
    {
        var zip = Zip(("0400100999_1_C26TAA_919_dung_aa.html", Fertilizer("919", 3, 7)),
            ("0400100999_1_C26TAA_27_dung_bb.html", Fertilizer("27", 7, 1)),
            ("0400100999_1_C26TAA_27_dung_cc.html", Fertilizer("27", 7, 1)),
            ("0400100999_1_C26TAA_30_huy_dd.html", Fertilizer("30", 8, 1)),
            ("readme.txt", "not an invoice"));

        var archive = PurchaseInvoiceArchive.Read(zip, Token);

        Assert.Equal(["27", "919"], archive.Invoices.Select(invoice => invoice.Number));
        Assert.Equal(2, archive.Skipped.Count);
        Assert.Contains(archive.Skipped, item => item.Contains("trùng", StringComparison.Ordinal));
        Assert.Contains(archive.Skipped, item => item.Contains("hủy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConvertedWorkbookImportsGoodsAsPurchasesAndListsServices()
    {
        var zip = Zip(
            ("a.html", Fertilizer("396", 25, 6)),
            // Another supplier reuses the number: still a different invoice.
            ("b.html", Fertilizer("396", 26, 6, OtherSupplier, OtherTax)),
            ("c.html", Html("20815", 2, 1, Bank, "0100112999-015",
                [new("", "Phí dịch vụ ngân hàng", "", "", "", "10%", "50.000")], [("10%", "50.000", "5.000")], "55.000", "K26TEA")),
            ("d.html", Html("784", 4, 2, "HỘ KINH DOANH NHÀ HÀNG MẪU", "3300999999",
                [Goods("Cơm Chiên Thái Gà", "Phần", "1", "115.000", "115.000", "8%"), Goods("Dasani", "Chai", "2", "15.000", "30.000", "8%")],
                [("8%", "145.000", "11.600")], "156.600")),
            ("e.html", Html("946", 9, 3, OtherSupplier, OtherTax,
                [new("Chiết khấu thương mại", "Điều chỉnh giảm (Chiết khấu doanh số tháng 1)", "", "0", "0", "5%", "90.476")],
                [("5%", "90.476", "4.524")], "95.000")));

        var result = await ImportArchive(zip);

        Assert.Equal(2, result.Invoices.Count);
        Assert.All(result.Invoices, invoice =>
        {
            Assert.True(invoice.IsPurchase());
            Assert.False(invoice.IsBlocked);
            Assert.Equal(invoice.Seller, invoice.Partner());
            Assert.Equal(InvoiceKinds.PurchasePayment, invoice.PaymentMethod);
            Assert.Equal(42_300_001m, invoice.GrandTotal);
        });
        Assert.Equal([OtherTax, SupplierTax], result.Invoices.Select(invoice => invoice.Seller.TaxCode).Order());
        Assert.Equal(2, result.Invoices.Select(invoice => invoice.InvoiceId).Distinct().Count());
        Assert.Equal(["20815", "784"], result.Services!.Select(service => service.Number).Order());
        Assert.All(result.Services!, service => Assert.Equal(InvoiceNature.Service, service.Nature));
        Assert.Contains("ăn uống", result.Services!.Single(service => service.Number == "784").Reason, StringComparison.Ordinal);
        Assert.Equal(1, result.SkippedNegativeInvoices);
    }

    [Fact]
    public async Task GoodsWithASmallServiceShareAreHeldAsMixed()
    {
        var zip = Zip(("a.html", Html("51", 2, 2, Supplier, SupplierTax,
            [Goods("Phân Kali bột Phú Mỹ", "Tấn", "1", "10.000.000", "10.000.000"),
                Goods("Phí vận chuyển", "Chuyến", "1", "500.000", "500.000", "8%")],
            [("5%", "10.000.000", "500.000"), ("8%", "500.000", "40.000")], "11.040.000")));

        var result = await ImportArchive(zip);

        Assert.Empty(result.Invoices);
        Assert.Equal(InvoiceNature.Mixed, Assert.Single(result.Services!).Nature);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "NO_INVOICES");
    }

    [Fact]
    public async Task PurchaseNeedsTheSupplierTaxCodeButNotTheBuyerOne()
    {
        var html = Fertilizer("12", 5, 5).Replace(SupplierTax, "", StringComparison.Ordinal).Replace("3301000999", "", StringComparison.Ordinal);

        var invoice = Assert.Single((await ImportArchive(Zip(("a.html", html)))).Invoices);

        Assert.Contains(invoice.Issues, issue => issue.Code == "SELLER_TAX_MISSING");
        Assert.DoesNotContain(invoice.Issues, issue => issue.Code == "BUYER_TAX_STATUS");
    }

    [Fact]
    public async Task PostedPurchasesMatchByNumberAndSupplier()
    {
        var result = await ImportArchive(Zip(("a.html", Fertilizer("396", 25, 6)), ("b.html", Fertilizer("396", 26, 6, OtherSupplier, OtherTax))));
        var posted = new PostedInvoiceStore(temp.Combine("posted-purchases.json"));
        posted.Replace([new("396", "", "NK00406", OtherSupplier.Replace("CÔNG TY TNHH", "CTY TNHH", StringComparison.Ordinal))], "Mua_hang.xlsx");

        var (pending, hidden) = posted.WithoutPosted(result);

        Assert.Equal(1, hidden);
        Assert.Equal(SupplierTax, Assert.Single(pending.Invoices).Seller.TaxCode);
        // Reloaded from disk the supplier is still part of the match.
        Assert.False(new PostedInvoiceStore(posted.FilePath).Contains("396", "", Supplier));
    }
}
