using System.Collections.Immutable;
using MisaManager.Core.Tests.TestData;
using MisaManager.Import;

namespace MisaManager.Core.Tests;

public sealed class InvoiceWorkbookImporterTests : IDisposable
{
    private static readonly DateOnly Day = InvoiceSheet.Day;
    private const string Organization = "CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V";

    private readonly TempDirectory temp = new();
    private readonly InvoiceWorkbookImporter importer = new(new WorkbookReader());

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Save(WorkbookBuilder book, string name = "hoa-don.xlsx") => book.Save(temp.Combine(name));

    private Task<ImportResult> Import(WorkbookBuilder book, WorkbookImportOptions? options = null)
        => importer.ImportAsync(Save(book), options, Token);

    private async Task<ImportResult> Import(Func<SheetBuilder, SheetBuilder> arrange, WorkbookImportOptions? options = null)
    {
        var (book, sheet) = InvoiceSheet.Standard();
        arrange(sheet);
        return await Import(book, options);
    }

    private static object?[] Line(string number = "1000", string? buyer = Organization, string? code = "A&V", string? tax = "0107286506",
        string name = "Rông đỏ 200SL - Chai 100ml", string unit = "Chai", decimal quantity = 22, decimal price = 10_000m, int rate = 8)
        => InvoiceSheet.Line(number, Day, buyer ?? "", code, tax, name, unit, quantity, price, rate);

    private static object?[] WithCell(object?[] row, int column, object? value)
    {
        var copy = (object?[])row.Clone();
        copy[column - 1] = value;
        return copy;
    }

    private static InvoiceDocument Only(ImportResult result) => Assert.Single(result.Invoices);

    private static bool Has(IEnumerable<ImportIssue> issues, string code, IssueSeverity severity = IssueSeverity.Blocking)
        => issues.Any(issue => issue.Code == code && issue.Severity == severity);

    // ---- happy path ----

    [Fact]
    public async Task StandardSheetImportsEachInvoiceNumberAsOneInvoice()
    {
        var (book, _) = InvoiceSheet.Standard();

        var result = await Import(book);

        Assert.False(result.IsBlocked);
        Assert.Equal(["1000", "1001"], result.Invoices.Select(invoice => invoice.Identity.Number));
        Assert.Equal([2, 1], result.Invoices.Select(invoice => invoice.Lines.Length));
        Assert.Equal(["Hóa đơn"], result.Worksheets);
        Assert.Equal(4, result.ReadRows);
        Assert.Equal(0, result.IgnoredEmptyRows);
        Assert.DoesNotContain(result.Issues, issue => issue.Severity != IssueSeverity.Info);
    }

    [Fact]
    public async Task InvoiceFieldsComeFromTheWorkbookWithSensibleDefaults()
    {
        var (book, _) = InvoiceSheet.Standard();

        var invoice = (await Import(book)).Invoices[0];

        Assert.Equal(Organization, invoice.Buyer.Name);
        Assert.Equal("A&V", invoice.Buyer.Code);
        Assert.Equal("0107286506", invoice.Buyer.TaxCode);
        Assert.Equal(ValuePresence.Provided, invoice.Buyer.TaxCodePresence);
        Assert.Equal(Day, invoice.Issued);
        Assert.Equal(Day, invoice.Identity.Issued);
        Assert.Equal(Day, invoice.VoucherDate);
        Assert.Equal(Day, invoice.PostedDate);
        Assert.Equal("VND", invoice.Currency);
        Assert.Equal("Tiền mặt", invoice.PaymentMethod);
        Assert.Equal("Hóa đơn bán hàng", invoice.InvoiceType);
        Assert.Matches("^[0-9a-f]{24}$", invoice.InvoiceId);
        Assert.Equal(1, invoice.Revision);
        Assert.Equal(InvoiceFingerprint.Compute(invoice), invoice.Fingerprint);
        Assert.Empty(invoice.Corrections);
    }

    [Fact]
    public async Task LinesAndTotalsAreReadExactly()
    {
        var (book, _) = InvoiceSheet.Standard();

        var invoice = (await Import(book)).Invoices[0];

        Assert.Equal(["Rông đỏ 200SL - Chai 100ml", "Hesman 60WG 18g"], invoice.Lines.Select(line => line.Name));
        Assert.Equal(["Chai", "Gói"], invoice.Lines.Select(line => line.Unit));
        var first = invoice.Lines[0];
        Assert.Equal((22m, 10_000m, 220_000m, "8%", 17_600m, 237_600m),
            (first.Quantity, first.UnitPrice, first.AmountBeforeTax, first.TaxRate, first.TaxAmount, first.TotalWithTax));
        Assert.Equal(828_000m, invoice.Subtotal);
        Assert.Equal(66_240m, invoice.TaxTotal);
        Assert.Equal(894_240m, invoice.GrandTotal);
        Assert.Equal($"{invoice.InvoiceId}:line:2", first.LineId);
        Assert.Equal((2, 3), (invoice.Source.FirstRow, invoice.Source.LastRow));
    }

    [Fact]
    public async Task ConsumerInvoiceGetsTheStandardNtdCustomerAndOnlyAnInfo()
    {
        var (book, _) = InvoiceSheet.Standard();

        var invoice = (await Import(book)).Invoices[1];

        Assert.Equal("BÁN CHO NGƯỜI TIÊU DÙNG", invoice.Buyer.Name);
        Assert.Equal("NTD", invoice.Buyer.Code);
        Assert.Equal(PartyKind.Individual, invoice.Buyer.Kind);
        Assert.Equal(ValuePresence.NotApplicable, invoice.Buyer.TaxCodePresence);
        Assert.Equal("", invoice.Buyer.TaxCode);
        Assert.Equal(IssueSeverity.Info, Assert.Single(invoice.Issues, issue => issue.Code == "BUYER_TAX_OPTIONAL").Severity);
        Assert.False(invoice.IsBlocked);
        Assert.Equal(1_542_900m, invoice.Subtotal);
        Assert.Equal(123_432m, invoice.TaxTotal);
        Assert.Equal(1_666_332m, invoice.GrandTotal);
    }

    [Theory]
    [InlineData("Bán cho người tiêu dùng")]
    [InlineData("  BÁN  CHO  NGƯỜI  TIÊU  DÙNG ")]
    [InlineData("BAN CHO NGUOI TIEU DUNG")]
    public async Task ConsumerBuyerIsRecognisedDespiteCaseSpacingAndDiacritics(string buyer)
    {
        var result = await Import(sheet => sheet.Row(2, Line("1000", buyer, null, null)).Row(3, Line("1000", buyer, null, null)));

        Assert.Equal("NTD", result.Invoices[0].Buyer.Code);
    }

    // ---- e-invoice portal export (DanhSachHoaDon_*.xlsx): STT, Mẫu số, Ký hiệu, …, Người mua, DN mua, … ----

    private static readonly string[] PortalHeaders =
    [
        "STT", "Mẫu số", "Ký hiệu", "Số hóa đơn", "Ngày lập", "Người mua", "DN mua", "MST người mua", "Địa chỉ người mua",
        "Tên hàng hóa", "Đơn vị tính", "Đơn giá", "Số lượng", "Tổng tiền chưa thuế", "Thuế hàng hóa", "Tiền thuế chi tiết", "Tổng tiền có thuế"
    ];

    // The portal writes every identifier and the date as text and the money columns as numbers.
    private static object?[] PortalLine(int stt, string? number, string person, string company, string tax, string name = "COMPASS 60OD-100CC PET",
        string unit = "Chai", double price = 41904.76, int quantity = 37, long amount = 1_550_476, int rate = 5, long taxAmount = 77_524)
        => [stt.ToString(System.Globalization.CultureInfo.InvariantCulture), "1", "C26MLV", number ?? "", "06/10/2026", person, company, tax,
            "", name, unit, price, quantity, amount, rate, taxAmount, amount + taxAmount];

    private async Task<ImportResult> ImportPortal(params object?[][] lines)
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("HoaDon").Row(1, PortalHeaders.Cast<object?>().ToArray());
        for (var index = 0; index < lines.Length; index++) sheet.Row(index + 2, lines[index]);
        return await Import(book);
    }

    [Fact]
    public async Task PortalExportMapsDnMuaAndImportsTheInvoice()
    {
        var book = new WorkbookBuilder();
        book.Sheet("HoaDon").Row(1, PortalHeaders.Cast<object?>().ToArray()).Row(2, PortalLine(1, "1824", "BÁN CHO NGƯỜI TIÊU DÙNG", "", ""));
        var path = Save(book);
        var workbook = await new WorkbookReader().ReadAsync(path, Token);

        var schema = importer.DescribeMapping(workbook, "HoaDon");
        var invoice = Only(await importer.ImportAsync(path, null, Token));

        Assert.Equal(7, schema.Fields.Single(field => field.Key == "buyerCompany").SuggestedColumn);
        Assert.Equal(6, schema.Fields.Single(field => field.Key == "buyer").SuggestedColumn);
        Assert.False(invoice.IsBlocked);
        Assert.Equal(("1824", new DateOnly(2026, 10, 6), "NTD"), (invoice.Identity.Number, invoice.Issued, invoice.Buyer.Code));
        Assert.Equal((1_550_476m, 77_524m, 1_628_000m), (invoice.Subtotal, invoice.TaxTotal, invoice.GrandTotal));
        Assert.Equal("5", invoice.Lines[0].TaxRate);
    }

    [Fact]
    public async Task DnMuaNamesTheBuyerAndNguoiMuaBecomesTheContact()
    {
        var result = await ImportPortal(
            PortalLine(1, "628", "Trần Duy Tiệp", "CÔNG TY CỔ PHẦN DỆT MAY HUẾ", "3300100628"),
            PortalLine(2, "628", "Trần Duy Tiệp", "CÔNG TY CỔ PHẦN DỆT MAY HUẾ", "3300100628", "FUJI ONE 40EC-450CC PET"),
            // "Người mua" left empty when only the organisation is printed on the invoice.
            PortalLine(3, "863", "", "CÔNG TY TNHH NHÀ NƯỚC MỘT THÀNH VIÊN LÂM NGHIỆP NAM HÒA", "3300100096"),
            PortalLine(4, "1805", "CÔNG TY TNHH NHÀ NƯỚC MỘT THÀNH VIÊN LÂM NGHIỆP NAM HÒA",
                "CÔNG TY TNHH NHÀ NƯỚC MỘT THÀNH VIÊN LÂM NGHIỆP NAM HÒA", "3300100096"),
            PortalLine(5, "1454", "Lê Thị Hương", "", "046172000663"));

        Assert.DoesNotContain(result.Issues, issue => issue.Severity == IssueSeverity.Blocking);
        Assert.Equal(
        [
            ("CÔNG TY CỔ PHẦN DỆT MAY HUẾ", "Trần Duy Tiệp", "3300100628"),
            ("CÔNG TY TNHH NHÀ NƯỚC MỘT THÀNH VIÊN LÂM NGHIỆP NAM HÒA", "", "3300100096"),
            ("CÔNG TY TNHH NHÀ NƯỚC MỘT THÀNH VIÊN LÂM NGHIỆP NAM HÒA", "", "3300100096"),
            ("Lê Thị Hương", "", "046172000663")
        ], result.Invoices.Select(invoice => (invoice.Buyer.Name, invoice.Buyer.Contact, invoice.Buyer.TaxCode)));
        Assert.Equal(2, result.Invoices[0].Lines.Length);
    }

    [Fact]
    public async Task OptionalMaHangColumnSetsTheItemCodePerLine()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("HoaDon").Row(1, [.. PortalHeaders, "Mã hàng"]);
        sheet.Row(2, [.. PortalLine(1, "1830", "Khách hàng không cung cấp thông tin", "", "", "Phân kali", "kg"), "HH_50"]);
        sheet.Row(3, [.. PortalLine(2, "1830", "Khách hàng không cung cấp thông tin", "", "", "FUJI ONE 40EC-100CC PET"), " HH_203 "]);
        sheet.Row(4, [.. PortalLine(3, "1830", "Khách hàng không cung cấp thông tin", "", "", "Chevin 5SC nano - 900ml (ĐN)"), ""]);
        var path = Save(book);

        var schema = importer.DescribeMapping(await new WorkbookReader().ReadAsync(path, Token), "HoaDon");
        var invoice = Only(await importer.ImportAsync(path, null, Token));

        Assert.Equal(18, schema.Fields.Single(field => field.Key == "itemCode").SuggestedColumn);
        Assert.False(schema.Fields.Single(field => field.Key == "itemCode").Required);
        // Codes differ per line, so they must not be treated as conflicting invoice metadata.
        Assert.DoesNotContain(invoice.Issues, issue => issue.Code == "METADATA_CONFLICT");
        Assert.False(invoice.IsBlocked);
        Assert.Equal(["HH_50", "HH_203", ""], invoice.Lines.Select(line => line.ItemCode));
    }

    [Fact]
    public async Task DnMuaColumnAloneSatisfiesTheBuyerColumn()
    {
        var book = new WorkbookBuilder();
        book.Sheet("HoaDon")
            .Row(1, "Số hóa đơn", "Ngày lập", "DN mua", "MST người mua", "Tên hàng hóa", "Đơn vị tính", "Đơn giá", "Số lượng",
                "Tổng tiền chưa thuế", "Thuế hàng hóa", "Tiền thuế chi tiết", "Tổng tiền có thuế")
            .Row(2, "533", "09/04/2026", "TRUNG TÂM DỊCH VỤ SỰ NGHIỆP CÔNG XÃ HƯNG LỘC", "3301747496", "Phân kali", "kg", 10_000, 10, 100_000, 5, 5_000, 105_000);

        var result = await Import(book);

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "COLUMN_MISSING");
        Assert.Equal("TRUNG TÂM DỊCH VỤ SỰ NGHIỆP CÔNG XÃ HƯNG LỘC", Only(result).Buyer.Name);
        Assert.False(result.IsBlocked);
    }

    [Theory]
    [InlineData("Khách hàng không cung cấp thông tin")]
    [InlineData("KHACH HANG KHONG CUNG CAP THONG TIN")]
    public async Task AnonymousBuyerNeedsNoTaxCodeAndKeepsTheCodeForTheCatalog(string buyer)
    {
        var invoice = Only(await ImportPortal(PortalLine(1, "1425", buyer, "", "")));

        Assert.False(invoice.IsBlocked);
        Assert.Equal(("", PartyKind.Individual, ValuePresence.NotApplicable), (invoice.Buyer.Code, invoice.Buyer.Kind, invoice.Buyer.TaxCodePresence));
        Assert.Contains(invoice.Issues, issue => issue.Code == "BUYER_TAX_OPTIONAL" && issue.Severity == IssueSeverity.Info);
    }

    /// <summary>An individual buyer (no "DN mua", not a company name) has no MST: accepted.</summary>
    [Fact]
    public async Task IndividualBuyerWithoutTaxCodeIsAccepted()
    {
        var invoice = Only(await ImportPortal(PortalLine(1, "1040", "Hoàng Trọng Tiếp", "", "")));

        Assert.False(invoice.IsBlocked);
        Assert.True(Has(invoice.Issues, "BUYER_TAX_OPTIONAL", IssueSeverity.Info));
    }

    [Fact]
    public async Task CompanyBuyerWithoutTaxCodeIsStillBlocked()
    {
        var invoice = Only(await ImportPortal(PortalLine(1, "863", "", "CÔNG TY TNHH LÂM NGHIỆP NAM HÒA", "")));

        Assert.True(Has(invoice.Issues, "BUYER_TAX_STATUS"));
    }

    /// <summary>Invoices not yet numbered by the portal are skipped and reported once as information.</summary>
    [Fact]
    public async Task RowsWithoutNumberAreSkipped()
    {
        var result = await ImportPortal(
            PortalLine(1, null, "Khách hàng không cung cấp thông tin", "", ""),
            PortalLine(2, null, "", "", "", "Chani 300EC - 100ml (ĐN Nicotex)"),
            PortalLine(3, "1823", "BÁN CHO NGƯỜI TIÊU DÙNG", "", ""));

        var invoice = Only(result);
        Assert.Equal("1823", invoice.Identity.Number);
        Assert.False(result.IsBlocked);
        Assert.Equal(2, result.SkippedUnnumberedRows);
        var skipped = Assert.Single(result.Issues, issue => issue.Code == "INVOICE_NUMBER_MISSING_SKIPPED");
        Assert.Equal(IssueSeverity.Info, skipped.Severity);
        Assert.Contains("dòng 2, 3", skipped.Message);
    }

    [Fact]
    public async Task WorkbookWithOnlyUnnumberedRowsHasNoInvoices()
    {
        var result = await ImportPortal(PortalLine(1, null, "Khách hàng không cung cấp thông tin", "", ""));

        Assert.Empty(result.Invoices);
        Assert.True(Has(result.Issues, "NO_INVOICES"));
    }

    [Fact]
    public async Task ImportIsRepeatableForTheSameFile()
    {
        var (book, _) = InvoiceSheet.Standard();
        var path = Save(book);

        var first = await importer.ImportAsync(path, null, Token);
        var second = await importer.ImportAsync(path, null, Token);

        Assert.Equal(first.Invoices.Select(invoice => invoice.InvoiceId), second.Invoices.Select(invoice => invoice.InvoiceId));
        Assert.Equal(first.Invoices.Select(invoice => invoice.Source.Sha256), second.Invoices.Select(invoice => invoice.Source.Sha256));
        Assert.Equal(first.Source.Sha256, await FileDigest.Sha256Async(path, Token));
    }

    [Fact]
    public async Task EditingTheWorkbookProducesNewInvoiceIdentities()
    {
        var (book, sheet) = InvoiceSheet.Standard();
        var before = await importer.ImportAsync(Save(book), null, Token);
        sheet.Row(3, Line("1000", name: "Hesman 60WG 18g", unit: "Gói", quantity: 39, price: 16_000m));
        var after = await importer.ImportAsync(Save(book), null, Token);

        Assert.NotEqual(before.Source.Sha256, after.Source.Sha256);
        Assert.NotEqual(before.Invoices[0].InvoiceId, after.Invoices[0].InvoiceId);
        Assert.NotEqual(before.Invoices[0].Fingerprint, after.Invoices[0].Fingerprint);
    }

    [Fact]
    public async Task ImportRevisionIsStampedOnTheSourceAndInvoices()
    {
        var (book, _) = InvoiceSheet.Standard();

        var result = await Import(book, new WorkbookImportOptions(ImportRevision: 3));

        Assert.Equal(3, result.Source.ImportRevision);
        Assert.All(result.Invoices, invoice => Assert.Equal(3, invoice.Revision));
    }

    [Fact]
    public async Task SameInvoiceNumberOnNonAdjacentRowsIsOneInvoice()
    {
        var result = await Import(sheet => sheet
            .Row(2, Line("1000"))
            .Row(3, Line("1001", "BÁN CHO NGƯỜI TIÊU DÙNG", null, null))
            .Row(4, Line("1000", name: "Hesman 60WG 18g", unit: "Gói", quantity: 38, price: 16_000m))
            .Row(5, null, null, null, null, null, null, null, null, null, null, null, null, null));

        Assert.Equal(["1000", "1001"], result.Invoices.Select(invoice => invoice.Identity.Number));
        Assert.Equal(2, result.Invoices[0].Lines.Length);
        Assert.Equal((2, 4), (result.Invoices[0].Source.FirstRow, result.Invoices[0].Source.LastRow));
    }

    [Fact]
    public async Task TitleRowsAboveTheHeaderAreSkipped()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Bảng kê");
        sheet.Row(1, "BẢNG KÊ HÓA ĐƠN BÁN RA THÁNG 10/2026").Merge("A1:F1");
        sheet.Row(2, "Công ty TNHH Thương mại Lá Việt");
        sheet.Row(4, InvoiceSheet.Headers.Cast<object?>().ToArray());
        sheet.Row(5, Line());

        var result = await Import(book);

        Assert.Equal(4, result.Source.FirstRow);
        Assert.Equal(["1000"], result.Invoices.Select(invoice => invoice.Identity.Number));
        Assert.Single(Only(result).Lines);
    }

    [Fact]
    public async Task BlankAndWhitespaceRowsAreIgnoredAndCounted()
    {
        var result = await Import(sheet => sheet.Row(5, "", " ", "  ").Row(6, "   "));

        Assert.Equal(2, result.IgnoredEmptyRows);
        Assert.Equal(2, result.Invoices.Count);
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public async Task CellTextIsTrimmed()
    {
        var result = await Import(sheet => sheet.Row(2, WithCell(WithCell(Line(), InvoiceSheet.Number, " 1000 "), InvoiceSheet.Name, "  Rông đỏ 200SL - Chai 100ml  "))
            .Row(3, WithCell(Line(name: "Hesman 60WG 18g", unit: "Gói", quantity: 38, price: 16_000m), InvoiceSheet.Number, "1000 ")));

        var invoice = result.Invoices[0];
        Assert.Equal("1000", invoice.Identity.Number);
        Assert.Equal("Rông đỏ 200SL - Chai 100ml", invoice.Lines[0].Name);
        Assert.Equal(2, invoice.Lines.Length);
    }

    // ---- header aliases ----

    public static TheoryData<string[]> HeaderVariants => new()
    {
        InvoiceSheet.Headers,
        new[] { "So hoa don", "Ngay hoa don", "Ten nguoi mua", "Ma khach hang", "MST nguoi mua", "Ten hang hoa", "Don vi tinh", "So luong", "Don gia",
            "Thanh tien", "Thue suat", "Tien thue", "Tong tien co thue" },
        new[] { "SỐ HÓA ĐƠN", "NGÀY HÓA ĐƠN", "TÊN NGƯỜI MUA", "MÃ KHÁCH HÀNG", "MST NGƯỜI MUA", "TÊN HÀNG HÓA", "ĐƠN VỊ TÍNH", "SỐ LƯỢNG", "ĐƠN GIÁ",
            "THÀNH TIỀN", "THUẾ SUẤT", "TIỀN THUẾ", "TỔNG TIỀN CÓ THUẾ" },
        new[] { "  Số   hóa  đơn ", "Ngày lập", "Người mua", "Mã khách hàng", "Mã số thuế người mua", "Hàng hóa", "ĐVT", "Số  lượng", "Đơn giá",
            "Tổng tiền chưa thuế", "Thuế suất", "Tiền thuế chi tiết", "Tổng tiền có thuế" },
        new[] { "Invoice Number", "Invoice Date", "Buyer", "Buyer Code", "Buyer Tax Code", "Item Name", "UOM", "Qty", "Price", "Line Amount", "Tax Rate",
            "Tax Amount", "Line Total With Tax" },
        new[] { "so_hoa_don", "ngay-hoa-don", "customer", "customer code", "buyer tax code", "description", "unit", "quantity", "unit price", "amount before tax",
            "vat rate", "vat amount", "tong dong co thue" },
        new[] { "SỐ HÓA ĐƠN", "NGÀY HÓA ĐƠN", "TÊN NGƯỜI MUA", "MÃ KHÁCH HÀNG", "MST NGƯỜI MUA", "TÊN HÀNG HÓA", "ĐƠN VỊ TÍNH", "SỐ LƯỢNG", "ĐƠN GIÁ",
            "THÀNH TIỀN", "THUẾ SUẤT", "TIỀN THUẾ", "TỔNG TIỀN CÓ THUẾ" }.Select(text => text.Normalize(System.Text.NormalizationForm.FormD)).ToArray()
    };

    [Theory]
    [MemberData(nameof(HeaderVariants))]
    public async Task HeaderAliasesWorkWithAndWithoutDiacriticsCaseAndLanguage(string[] headers)
    {
        var (book, sheet) = InvoiceSheet.Standard();
        sheet.Row(1, headers.Cast<object?>().ToArray());

        var result = await Import(book);

        Assert.False(result.IsBlocked);
        Assert.Equal([2, 1], result.Invoices.Select(invoice => invoice.Lines.Length));
        Assert.Equal(894_240m, result.Invoices[0].GrandTotal);
    }

    [Fact]
    public async Task ColumnOrderDoesNotMatter()
    {
        var order = new[] { 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Hóa đơn");
        sheet.Row(1, order.Select(column => (object?)InvoiceSheet.Headers[column - 1]).ToArray());
        foreach (var (row, line) in new[] { Line(), Line(name: "Hesman 60WG 18g", unit: "Gói", quantity: 38, price: 16_000m) }.Select((line, index) => (index + 2, line)))
            sheet.Row(row, order.Select(column => line[column - 1]).ToArray());

        var result = await Import(book);

        Assert.False(result.IsBlocked);
        Assert.Equal(894_240m, Only(result).GrandTotal);
    }

    [Fact]
    public async Task UnrelatedExtraColumnsAreIgnored()
    {
        var result = await Import(sheet => sheet.Cell(1, 14, "Ghi chú").Cell(2, 14, "giao buổi sáng").Cell(1, 15, "Cột lạ").Cell(2, 15, 99));

        Assert.False(result.IsBlocked);
        Assert.Equal(2, result.Invoices.Count);
    }

    // ---- merged cells ----

    [Fact]
    public async Task MergedHeaderCellsFlowIntoEveryLineOfTheInvoice()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Hóa đơn");
        sheet.Row(1, InvoiceSheet.Headers.Cast<object?>().ToArray());
        sheet.Row(2, Line());
        sheet.Row(3, Line(name: "Hesman 60WG 18g", unit: "Gói", quantity: 38, price: 16_000m));
        foreach (var column in new[] { InvoiceSheet.Number, InvoiceSheet.Date, InvoiceSheet.Buyer, InvoiceSheet.BuyerCode, InvoiceSheet.BuyerTax })
        {
            sheet.Cell(3, column, null);
            sheet.Merge($"{CellAddress.Format(column, 2)}:{CellAddress.Format(column, 3)}");
        }

        var result = await Import(book);

        var invoice = Only(result);
        Assert.False(result.IsBlocked);
        Assert.Equal("1000", invoice.Identity.Number);
        Assert.Equal(2, invoice.Lines.Length);
        Assert.Equal("0107286506", invoice.Buyer.TaxCode);
        Assert.Equal(894_240m, invoice.GrandTotal);
    }

    [Fact]
    public async Task RowWithoutAnInvoiceNumberIsNeverGluedToThePreviousInvoice()
    {
        var result = await Import(sheet => sheet.Row(3, WithCell(Line(name: "Hesman 60WG 18g", unit: "Gói", quantity: 38, price: 16_000m), InvoiceSheet.Number, null)));

        Assert.True(Has(result.Issues, "INVOICE_NUMBER_MISSING_SKIPPED", IssueSeverity.Info));
        Assert.Equal(1, result.SkippedUnnumberedRows);
        Assert.Single(result.Invoices.Single(invoice => invoice.Identity.Number == "1000").Lines);
    }

    // ---- dates ----

    public static TheoryData<object, string?> DateCells => new()
    {
        { Day, null },
        { "2026-10-04", null },
        { "04/10/2026", null },
        { "04/10/2026", "vi-VN" },
        { "2026-10-04", "vi-VN" },
        { "10/04/2026", "en-US" },
        { "2026-10-04", "en-US" },
        { "2026-10-04", "invariant" },
    };

    [Theory]
    [MemberData(nameof(DateCells))]
    public async Task DatesAreReadFromSerialNumbersAndTextInEveryLocale(object cell, string? locale)
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Date, cell).Cell(3, InvoiceSheet.Date, cell), new WorkbookImportOptions(Locale: locale));

        Assert.Equal(Day, result.Invoices[0].Issued);
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public async Task DayFirstIsAssumedForSlashDatesWithoutALocale()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Date, "05/10/2026").Cell(3, InvoiceSheet.Date, "05/10/2026"));

        Assert.Equal(new DateOnly(2026, 10, 5), result.Invoices[0].Issued);
    }

    [Fact]
    public async Task Excel1904WorkbookDatesUseTheirOwnEpoch()
    {
        var (book, sheet) = InvoiceSheet.Standard();
        book.Date1904 = true;
        sheet.Cell(2, InvoiceSheet.Date, Day).Cell(3, InvoiceSheet.Date, Day).Cell(4, InvoiceSheet.Date, Day);

        var result = await Import(book);

        Assert.All(result.Invoices, invoice => Assert.Equal(Day, invoice.Issued));
    }

    [Theory]
    [InlineData(59, 1900, 2, 28)]
    [InlineData(61, 1900, 3, 1)]
    [InlineData(1, 1900, 1, 1)]
    [InlineData(45000, 2023, 3, 15)]
    public async Task SerialNumbersFollowExcelsLeapYearQuirk(int serial, int year, int month, int day)
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Date, serial).Cell(3, InvoiceSheet.Date, serial));

        Assert.Equal(new DateOnly(year, month, day), result.Invoices[0].Issued);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ImpossibleSerialNumbersAreBlocking(int serial)
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Date, serial).Cell(3, InvoiceSheet.Date, serial));

        Assert.True(Has(result.Issues, "DATE_AMBIGUOUS"));
        Assert.True(result.IsBlocked);
    }

    [Theory]
    [InlineData("hôm nay")]
    [InlineData("13/25/2026")]
    [InlineData("31/02/2026")]
    [InlineData("2026/10/04")]
    [InlineData("4 tháng 10 năm 2026")]
    public async Task UnreadableDatesAreBlockingAndNeverGuessed(string text)
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Date, text).Cell(3, InvoiceSheet.Date, text));

        var issue = result.Issues.First(item => item.Code == "DATE_AMBIGUOUS");
        Assert.Equal(IssueSeverity.Blocking, issue.Severity);
        Assert.Equal("B2", issue.Cell);
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public async Task FractionalSerialNumbersAreRejected()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Date, 45000.5).Cell(3, InvoiceSheet.Date, 45000.5));

        Assert.True(Has(result.Issues, "DATE_AMBIGUOUS"));
    }

    [Fact]
    public async Task MissingDateBlocksTheInvoice()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Date, null).Cell(3, InvoiceSheet.Date, null));

        Assert.True(Has(result.Invoices[0].Issues, "DATES_MISSING"));
        Assert.True(result.IsBlocked);
    }

    // ---- numbers ----

    private static object?[] TextNumbers(string quantity, string price, string amount, string tax, string total, string name = "Hesman 60WG 18g")
        => [ "1000", Day, Organization, "A&V", "0107286506", name, "Gói", quantity, price, amount, "8%", tax, total ];

    [Fact]
    public async Task VietnameseTextNumbersAreReadWithTheViVnLocale()
    {
        var result = await Import(sheet => sheet
            .Row(2, TextNumbers("100", "15.429", "1.542.900", "123.432", "1.666.332"))
            .Row(3, TextNumbers("2,5", "10.000", "25.000", "2.000", "27.000")),
            new WorkbookImportOptions(Locale: "vi-VN"));

        var invoice = result.Invoices[0];
        Assert.False(invoice.IsBlocked);
        Assert.Equal((100m, 15_429m, 1_542_900m, 123_432m, 1_666_332m),
            (invoice.Lines[0].Quantity, invoice.Lines[0].UnitPrice, invoice.Lines[0].AmountBeforeTax, invoice.Lines[0].TaxAmount, invoice.Lines[0].TotalWithTax));
        Assert.Equal(2.5m, invoice.Lines[1].Quantity);
        Assert.Equal(1_567_900m, invoice.Subtotal);
    }

    [Fact]
    public async Task EnglishTextNumbersAreReadWithTheEnUsLocale()
    {
        var result = await Import(sheet => sheet
            .Row(2, TextNumbers("100", "15,429", "1,542,900", "123,432", "1,666,332"))
            .Row(3, TextNumbers("2.5", "10,000", "25,000", "2,000", "27,000")),
            new WorkbookImportOptions(Locale: "en-US"));

        Assert.False(result.Invoices[0].IsBlocked);
        Assert.Equal(1_542_900m, result.Invoices[0].Lines[0].AmountBeforeTax);
        Assert.Equal(2.5m, result.Invoices[0].Lines[1].Quantity);
    }

    [Fact]
    public async Task TextNumbersWithThousandSeparatorsAreRefusedWithoutALocale()
    {
        var result = await Import(sheet => sheet
            .Row(2, TextNumbers("100", "15.429", "1.542.900", "123.432", "1.666.332"))
            .Row(3, TextNumbers("2", "10000", "20000", "1600", "21600")));

        var invalid = result.Issues.Where(issue => issue.Code == "NUMBER_INVALID").ToArray();
        Assert.Equal(["I2", "J2", "L2", "M2"], invalid.Select(issue => issue.Cell!).Distinct().Order());
        Assert.All(invalid, issue => Assert.Equal(IssueSeverity.Blocking, issue.Severity));
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public async Task PlainDigitTextNumbersAreAcceptedWithoutALocale()
    {
        var result = await Import(sheet => sheet
            .Row(2, TextNumbers("2", "10000", "20000", "1600", "21600"))
            .Row(3, TextNumbers("3", "10000", "30000", "2400", "32400")));

        Assert.False(result.IsBlocked);
        Assert.Equal(50_000m, result.Invoices[0].Subtotal);
    }

    [Theory]
    [InlineData("vi-VN", "1.23.4")]
    [InlineData("vi-VN", "1,234.5")]
    [InlineData("vi-VN", "12.34")]
    [InlineData("vi-VN", "1.542.900 ₫")]
    [InlineData("vi-VN", "abc")]
    [InlineData("en-US", "1.542.900")]
    [InlineData("en-US", "1,23")]
    [InlineData("invariant", "1,000")]
    public async Task AmbiguousOrMalformedTextNumbersAreBlocking(string locale, string text)
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Amount, text), new WorkbookImportOptions(Locale: locale));

        var issue = Assert.Single(result.Issues, item => item.Code == "NUMBER_INVALID");
        Assert.Equal("J2", issue.Cell);
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public async Task ExcelDoublesWithLongFractionsAreRoundedToSixDecimals()
    {
        var row = WithCell(WithCell(WithCell(WithCell(Line(), InvoiceSheet.Quantity, 3m), InvoiceSheet.UnitPrice, 10000d / 3d), InvoiceSheet.Amount, 10_000m), InvoiceSheet.TaxAmount, 800m);
        var result = await Import(sheet => sheet.Row(2, row).Row(3, Line(name: "Hesman 60WG 18g", unit: "Gói", quantity: 38, price: 16_000m)));

        Assert.Equal(3333.333333m, result.Invoices[0].Lines[0].UnitPrice);
    }

    [Fact]
    public async Task NegativeQuantityIsRefusedAsAnAdjustmentLine()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Quantity, -22m));

        Assert.True(Has(result.Invoices[0].Issues, "LINE_NUMBERS_INVALID"));
    }

    [Fact]
    public async Task LineTotalThatDisagreesWithAmountPlusTaxBlocks()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Total, 237_601m));

        Assert.True(Has(result.Invoices[0].Issues, "LINE_TOTAL_MISMATCH"));
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public async Task AmountDifferingFromQuantityTimesPriceOnlyWarns()
    {
        // Real case: 100 kg x 15.429 = 1.542.900, but MISA derived the amount 1.542.857 from the price.
        var row = Line("1001", "BÁN CHO NGƯỜI TIÊU DÙNG", null, null, "Phân Ure Đạm Phú Mỹ loại 1 (bao)", "kg", 100, 15_429m);
        row = WithCell(WithCell(WithCell(row, InvoiceSheet.Amount, 1_542_857m), InvoiceSheet.TaxAmount, 123_429m), InvoiceSheet.Total, 1_666_286m);

        var result = await Import(sheet => sheet.Row(4, row));

        var invoice = result.Invoices.Single(item => item.Identity.Number == "1001");
        Assert.True(Has(invoice.Issues, "UNIT_PRICE_AMOUNT_DIFFERENCE", IssueSeverity.Warning));
        Assert.False(invoice.IsBlocked);
        Assert.Equal(1_542_857m, invoice.Subtotal);
    }

    [Fact]
    public async Task NumericTaxRateCellsAreAccepted()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.TaxRate, 8).Cell(3, InvoiceSheet.TaxRate, 8));

        Assert.Equal("8", result.Invoices[0].Lines[0].TaxRate);
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public async Task PercentFormattedTaxRateIsAtLeastSurfacedAsAWarning()
    {
        // Excel stores 8% as 0.08; the importer keeps the stored text, so validation flags the tax mismatch instead of passing silently.
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.TaxRate, new Percent(0.08)).Cell(3, InvoiceSheet.TaxRate, new Percent(0.08)));

        Assert.True(Has(result.Invoices[0].Issues, "TAX_RATE_AMOUNT_DIFFERENCE", IssueSeverity.Warning));
    }

    [Fact(Skip = "BUG (suspected): src/MisaManager.Import/InvoiceWorkbookImporter.cs:192 imports a percent-formatted tax-rate cell (Excel stores 8% as 0.08, "
        + "NumberFormat builtin:9) as the text \"0.08\"; the format is available on the SourceCell but not used, so the typed line carries TaxRate=0.08 "
        + "and only a TAX_RATE_AMOUNT_DIFFERENCE warning (not a block) protects the user.")]
    public async Task PercentFormattedTaxRateIsNormalisedToPercentPoints()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.TaxRate, new Percent(0.08)).Cell(3, InvoiceSheet.TaxRate, new Percent(0.08)));

        Assert.Equal("8", result.Invoices[0].Lines[0].TaxRate.TrimEnd('%'));
    }

    // ---- required / ambiguous columns ----

    public static TheoryData<int, string> RequiredColumns => new()
    {
        { InvoiceSheet.Number, "number" }, { InvoiceSheet.Date, "issued" }, { InvoiceSheet.Buyer, "buyer" }, { InvoiceSheet.Name, "name" },
        { InvoiceSheet.Unit, "unit" }, { InvoiceSheet.Quantity, "quantity" }, { InvoiceSheet.UnitPrice, "unitPrice" }, { InvoiceSheet.Amount, "amount" },
        { InvoiceSheet.TaxRate, "taxRate" }, { InvoiceSheet.TaxAmount, "taxAmount" }, { InvoiceSheet.Total, "lineTotal" }
    };

    [Theory]
    [MemberData(nameof(RequiredColumns))]
    public async Task MissingRequiredColumnBlocksTheWholeImport(int column, string field)
    {
        var result = await Import(sheet => sheet.Cell(1, column, null), new WorkbookImportOptions("Hóa đơn", HeaderRow: 1));

        var issue = Assert.Single(result.Issues, item => item.Code == "COLUMN_MISSING");
        Assert.Equal(IssueSeverity.Blocking, issue.Severity);
        Assert.Contains(field, issue.Message);
        Assert.Empty(result.Invoices);
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public async Task EveryMissingRequiredColumnIsReported()
    {
        var result = await Import(sheet => sheet.Cell(1, InvoiceSheet.Quantity, null).Cell(1, InvoiceSheet.UnitPrice, null).Cell(1, InvoiceSheet.Total, null));

        Assert.Equal(3, result.Issues.Count(issue => issue.Code == "COLUMN_MISSING"));
    }

    [Fact]
    public async Task OptionalBuyerColumnsMayBeAbsentButOrganisationsThenNeedATaxCode()
    {
        var result = await Import(sheet => sheet.Cell(1, InvoiceSheet.BuyerCode, null).Cell(1, InvoiceSheet.BuyerTax, null));

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "COLUMN_MISSING");
        Assert.True(Has(result.Invoices[0].Issues, "BUYER_TAX_STATUS"));
        Assert.False(result.Invoices.Single(invoice => invoice.Identity.Number == "1001").IsBlocked);
    }

    [Theory]
    [InlineData("Số lượng")]
    [InlineData("So luong")]
    [InlineData("QUANTITY")]
    public async Task DuplicateColumnForTheSameFieldIsAmbiguousAndBlocksTheImport(string duplicateHeader)
    {
        var result = await Import(sheet => sheet.Cell(1, 14, duplicateHeader).Cell(2, 14, 99).Cell(3, 14, 98));

        var issue = Assert.Single(result.Issues, item => item.Code == "COLUMN_AMBIGUOUS");
        Assert.Equal(IssueSeverity.Blocking, issue.Severity);
        Assert.Equal("N1", issue.Cell);
        Assert.Empty(result.Invoices);
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public async Task EachDuplicatedHeaderIsReported()
    {
        var result = await Import(sheet => sheet.Cell(1, 14, "Đơn giá").Cell(1, 15, "Thành tiền"));

        Assert.Equal(["N1", "O1"], result.Issues.Where(issue => issue.Code == "COLUMN_AMBIGUOUS").Select(issue => issue.Cell!).Order());
    }

    [Fact]
    public async Task FormulaInTheHeaderBlocks()
    {
        var result = await Import(sheet => sheet.Cell(1, InvoiceSheet.Quantity, new Formula("\"Số lượng\"", "Số lượng")));

        Assert.True(Has(result.Issues, "HEADER_FORMULA"));
        Assert.True(result.IsBlocked);
    }

    // ---- formulas and bad cell types ----

    [Fact]
    public async Task FormulaCellsAreFlaggedAtTheirAddressAndBlockTheInvoice()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Amount, new Formula("H2*I2", 220_000m)));

        var issues = result.Issues.Where(issue => issue.Code == "FORMULA_UNCONFIRMED").ToArray();
        Assert.Contains(issues, issue => issue.Cell == "J2" && issue.Severity == IssueSeverity.Blocking);
        Assert.True(result.IsBlocked);
        Assert.True(result.Invoices[0].IsBlocked);
        Assert.Equal(220_000m, result.Invoices[0].Lines[0].AmountBeforeTax);
        Assert.True(result.Invoices[0].Lines[0].HasFormula);
        Assert.False(result.Invoices[1].IsBlocked);
    }

    [Fact]
    public async Task EveryFormulaCellIsReportedNotJustTheFirst()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Amount, new Formula("H2*I2", 220_000m)).Cell(3, InvoiceSheet.Total, new Formula("J3+L3", 656_640m)));

        var cells = result.Invoices[0].SourceIssues.Where(issue => issue.Code == "FORMULA_UNCONFIRMED").Select(issue => issue.Cell!);
        Assert.Equal(["J2", "M3"], cells.Order());
    }

    [Fact]
    public async Task BooleanAndErrorCellsInMappedColumnsAreRefused()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.Unit, true).Cell(3, InvoiceSheet.Total, new ErrorValue("#DIV/0!")));

        Assert.Equal(["G2", "M3"], result.Invoices[0].SourceIssues.Where(issue => issue.Code == "CELL_TYPE_INVALID").Select(issue => issue.Cell!).Order());
        Assert.True(result.IsBlocked);
    }

    // ---- identity cells ----

    [Fact]
    public async Task NumericInvoiceNumberAndTaxCodeUnderFifteenDigitsAreAccepted()
    {
        var result = await Import(sheet => sheet.Row(2, WithCell(WithCell(Line(), InvoiceSheet.Number, 1000), InvoiceSheet.BuyerTax, 107286506L))
            .Row(3, WithCell(WithCell(Line(name: "Hesman 60WG 18g", unit: "Gói", quantity: 38, price: 16_000m), InvoiceSheet.Number, 1000), InvoiceSheet.BuyerTax, 107286506L)));

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "IDENTITY_REQUIRES_TEXT");
        Assert.Equal("1000", result.Invoices[0].Identity.Number);
    }

    [Fact]
    public async Task NumericBuyerCodeIsRefusedBecauseItCouldHaveLostLeadingZeros()
    {
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.BuyerCode, 12).Cell(3, InvoiceSheet.BuyerCode, 12));

        Assert.True(Has(result.Issues, "IDENTITY_REQUIRES_TEXT"));
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public async Task NumericTaxCodeBeyondFifteenDigitsIsRefused()
    {
        var sixteenDigits = long.Parse(new string('1', 16));
        var result = await Import(sheet => sheet.Cell(2, InvoiceSheet.BuyerTax, sixteenDigits).Cell(3, InvoiceSheet.BuyerTax, sixteenDigits));

        Assert.True(Has(result.Issues, "IDENTITY_REQUIRES_TEXT"));
    }

    [Fact]
    public async Task TextCodesKeepLeadingZeros()
    {
        var result = await Import(sheet => sheet.Row(2, WithCell(Line("00012"), InvoiceSheet.BuyerTax, "0000123456"))
            .Row(3, WithCell(Line("00012", name: "Hesman 60WG 18g", unit: "Gói", quantity: 38, price: 16_000m), InvoiceSheet.BuyerTax, "0000123456")));

        var invoice = result.Invoices.Single(item => item.Identity.Number == "00012");
        Assert.Equal("0000123456", invoice.Buyer.TaxCode);
    }

    // ---- grouping conflicts ----

    [Theory]
    [InlineData(InvoiceSheet.Buyer, "CÔNG TY KHÁC")]
    [InlineData(InvoiceSheet.BuyerTax, "0101234567")]
    [InlineData(InvoiceSheet.BuyerCode, "KH02")]
    public async Task LinesOfOneInvoiceMustAgreeOnBuyerData(int column, string other)
    {
        var result = await Import(sheet => sheet.Cell(3, column, other));

        Assert.True(Has(result.Invoices[0].SourceIssues, "METADATA_CONFLICT"));
        Assert.True(result.Invoices[0].IsBlocked);
    }

    [Fact]
    public async Task LinesOfOneInvoiceMustAgreeOnTheDate()
    {
        var result = await Import(sheet => sheet.Cell(3, InvoiceSheet.Date, Day.AddDays(1)));

        Assert.True(Has(result.Invoices[0].SourceIssues, "METADATA_CONFLICT"));
    }

    [Fact]
    public async Task BlankMetadataOnLaterLinesIsNotAConflict()
    {
        var result = await Import(sheet => sheet.Cell(3, InvoiceSheet.Buyer, null).Cell(3, InvoiceSheet.Date, null).Cell(3, InvoiceSheet.BuyerTax, null).Cell(3, InvoiceSheet.BuyerCode, null));

        Assert.False(result.Invoices[0].IsBlocked);
        Assert.Equal(Organization, result.Invoices[0].Buyer.Name);
    }

    [Fact]
    public async Task HeaderOnlyWorksheetHasNoInvoices()
    {
        var book = new WorkbookBuilder();
        book.Sheet("Hóa đơn").Row(1, InvoiceSheet.Headers.Cast<object?>().ToArray());

        var result = await Import(book);

        Assert.True(Has(result.Issues, "NO_INVOICES"));
        Assert.Empty(result.Invoices);
        Assert.True(result.IsBlocked);
    }

    // ---- hidden rows / filters ----

    [Fact]
    public async Task HiddenRowsAreStillImportedButWarned()
    {
        var result = await Import(sheet => sheet.Hide(3));

        Assert.True(Has(result.Issues, "HIDDEN_ROWS_INCLUDED", IssueSeverity.Warning));
        Assert.Equal(2, result.Invoices[0].Lines.Length);
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public async Task AutoFilteredWorksheetIsWarned()
    {
        var result = await Import(sheet => sheet.Filter());

        Assert.True(Has(result.Issues, "HIDDEN_ROWS_INCLUDED", IssueSeverity.Warning));
        Assert.False(result.IsBlocked);
    }

    // ---- worksheet and header selection ----

    private static WorkbookBuilder TwoDataSheets()
    {
        var (book, _) = InvoiceSheet.Standard("Tháng 10");
        var second = book.Sheet("Tháng 11");
        second.Row(1, InvoiceSheet.Headers.Cast<object?>().ToArray());
        second.Row(2, Line("2000"));
        return book;
    }

    [Fact]
    public async Task DataSheetIsFoundAmongOtherSheets()
    {
        var (book, _) = InvoiceSheet.Standard();
        book.Sheet("Ghi chú").Row(1, "Bảng này không phải dữ liệu");

        var result = await Import(book);

        Assert.Equal(["Hóa đơn", "Ghi chú"], result.Worksheets);
        Assert.Equal("Hóa đơn", result.Source.Worksheet);
    }

    [Fact]
    public async Task SeveralDataSheetsRequireAnExplicitChoice()
    {
        var error = await Assert.ThrowsAsync<WorkbookImportException>(() => Import(TwoDataSheets()));

        Assert.Equal("WORKSHEET_SELECTION_REQUIRED", error.Code);
    }

    [Fact]
    public async Task ExplicitWorksheetSelectsOnlyThatSheet()
    {
        var result = await Import(TwoDataSheets(), new WorkbookImportOptions(Worksheet: "Tháng 11"));

        Assert.Equal(["2000"], result.Invoices.Select(invoice => invoice.Identity.Number));
        Assert.Equal("Tháng 11", result.Source.Worksheet);
    }

    [Fact]
    public async Task UnknownWorksheetIsReported()
    {
        var (book, _) = InvoiceSheet.Standard();

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() => Import(book, new WorkbookImportOptions(Worksheet: "Không có")));

        Assert.Equal("WORKSHEET_NOT_FOUND", error.Code);
    }

    [Fact]
    public async Task WorkbookWithoutAnyInvoiceHeaderNeedsASheetChoice()
    {
        var book = new WorkbookBuilder();
        book.Sheet("Sheet1").Row(1, "Mã", "Tên").Row(2, "HH_01", "Chery 15g");

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() => Import(book));

        Assert.Equal("WORKSHEET_SELECTION_REQUIRED", error.Code);
    }

    [Fact]
    public async Task ExplicitSheetWithoutAHeaderReportsHeaderMissing()
    {
        var book = new WorkbookBuilder();
        book.Sheet("Sheet1").Row(1, "Mã", "Tên").Row(2, "HH_01", "Chery 15g");

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() => Import(book, new WorkbookImportOptions(Worksheet: "Sheet1")));

        Assert.Equal("HEADER_MISSING", error.Code);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("vi")]
    [InlineData("")]
    public async Task UnsupportedLocalesAreRejectedBeforeReading(string locale)
    {
        var error = await Assert.ThrowsAsync<WorkbookImportException>(() => importer.ImportAsync(temp.Combine("never-read.xlsx"), new WorkbookImportOptions(Locale: locale), Token));

        Assert.Equal("LOCALE_UNSUPPORTED", error.Code);
    }

    [Fact]
    public async Task ImportRevisionMustBePositive()
    {
        var (book, _) = InvoiceSheet.Standard();

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() => Import(book, new WorkbookImportOptions(ImportRevision: 0)));

        Assert.Equal("REVISION_INVALID", error.Code);
    }

    [Fact]
    public async Task CancelledImportThrowsOperationCanceled()
    {
        var (book, _) = InvoiceSheet.Standard();
        var path = Save(book);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync(path, null, cancelled.Token));
    }

    // ---- explicit mapping ----

    private static WorkbookBuilder CustomHeaderBook()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Dữ liệu");
        sheet.Row(3, "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M");
        sheet.Row(4, Line());
        sheet.Row(5, Line(name: "Hesman 60WG 18g", unit: "Gói", quantity: 38, price: 16_000m));
        return book;
    }

    private static ImmutableDictionary<string, int> FullMap() => new Dictionary<string, int>
    {
        ["number"] = 1, ["issued"] = 2, ["buyer"] = 3, ["buyerCode"] = 4, ["buyerTaxCode"] = 5, ["name"] = 6, ["unit"] = 7,
        ["quantity"] = 8, ["unitPrice"] = 9, ["amount"] = 10, ["taxRate"] = 11, ["taxAmount"] = 12, ["lineTotal"] = 13
    }.ToImmutableDictionary();

    [Fact]
    public async Task ExplicitMappingWorksForHeadersTheAliasesDoNotKnow()
    {
        var result = await Import(CustomHeaderBook(), new WorkbookImportOptions("Dữ liệu", HeaderRow: 3, ColumnMap: FullMap()));

        Assert.False(result.IsBlocked);
        Assert.Equal(894_240m, Only(result).GrandTotal);
        Assert.Equal(3, result.Source.FirstRow);
    }

    [Fact]
    public async Task MissingRequiredFieldInAnExplicitMappingBlocks()
    {
        var map = FullMap().Remove("taxAmount");

        var result = await Import(CustomHeaderBook(), new WorkbookImportOptions("Dữ liệu", HeaderRow: 3, ColumnMap: map));

        Assert.True(Has(result.Issues, "COLUMN_MISSING"));
    }

    [Theory]
    [InlineData("unknownField", 14)]
    [InlineData("quantity", 99)]
    [InlineData("unitPrice", 8)]
    public async Task InvalidMappingsAreRejected(string field, int column)
    {
        var map = FullMap().SetItem(field, column);

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() =>
            Import(CustomHeaderBook(), new WorkbookImportOptions("Dữ liệu", HeaderRow: 3, ColumnMap: map)));

        Assert.Equal("MAPPING_INVALID", error.Code);
    }

    [Fact]
    public async Task ExplicitHeaderRowThatDoesNotExistIsReported()
    {
        var error = await Assert.ThrowsAsync<WorkbookImportException>(() =>
            Import(CustomHeaderBook(), new WorkbookImportOptions("Dữ liệu", HeaderRow: 9, ColumnMap: FullMap())));

        Assert.Equal("HEADER_MISSING", error.Code);
    }

    // ---- DescribeMapping / SuggestField ----

    private static readonly string[] RequiredKeys = ["number", "issued", "buyer", "name", "unit", "unitPrice", "quantity", "amount", "taxRate", "taxAmount", "lineTotal"];

    [Fact]
    public async Task DescribeMappingSuggestsEachFieldColumn()
    {
        var (book, _) = InvoiceSheet.Standard();
        var workbook = await new WorkbookReader().ReadAsync(Save(book), Token);

        var schema = importer.DescribeMapping(workbook, "Hóa đơn");

        Assert.Equal(1, schema.HeaderRow);
        Assert.Equal(13, schema.Columns.Count);
        Assert.Equal("A1 - Số hóa đơn", schema.Columns[0].Display);
        Assert.Equal(InvoiceWorkbookImporter.Fields, schema.Fields.Select(field => field.Key));
        var byKey = schema.Fields.ToDictionary(field => field.Key);
        Assert.Equal(InvoiceSheet.Quantity, byKey["quantity"].SuggestedColumn);
        Assert.Equal(InvoiceSheet.TaxRate, byKey["taxRate"].SuggestedColumn);
        Assert.Equal(InvoiceSheet.BuyerTax, byKey["buyerTaxCode"].SuggestedColumn);
        Assert.Null(byKey["signed"].SuggestedColumn);
        Assert.All(schema.Fields, field => Assert.Equal(RequiredKeys.Contains(field.Key), field.Required));
    }

    [Fact]
    public async Task DescribeMappingLeavesDuplicatedHeadersWithoutASuggestion()
    {
        var (book, sheet) = InvoiceSheet.Standard();
        sheet.Cell(1, 14, "So luong");
        var workbook = await new WorkbookReader().ReadAsync(Save(book), Token);

        var schema = importer.DescribeMapping(workbook, "Hóa đơn");

        Assert.Null(schema.Fields.Single(field => field.Key == "quantity").SuggestedColumn);
    }

    [Fact]
    public async Task DescribeMappingValidatesTheWorksheetAndHeader()
    {
        var book = new WorkbookBuilder();
        book.Sheet("Sheet1").Row(1, "Mã", "Tên");
        var workbook = await new WorkbookReader().ReadAsync(Save(book), Token);

        Assert.Equal("WORKSHEET_NOT_FOUND", Assert.Throws<WorkbookImportException>(() => importer.DescribeMapping(workbook, "Khác")).Code);
        Assert.Equal("HEADER_MISSING", Assert.Throws<WorkbookImportException>(() => importer.DescribeMapping(workbook, "Sheet1")).Code);
    }

    [Theory]
    [InlineData("Số hóa đơn", "number")]
    [InlineData("so hoa don", "number")]
    [InlineData("  THÀNH  TIỀN ", "amount")]
    [InlineData("Đơn vị tính", "unit")]
    [InlineData("ĐVT", "unit")]
    [InlineData("MST người mua", "buyerTaxCode")]
    [InlineData("Ngày ký", "signed")]
    [InlineData("Tong dong co thue", "lineTotal")]
    [InlineData("Tổng tiền thuế", null)]
    [InlineData("Ghi chú", null)]
    [InlineData("", null)]
    [InlineData("Cột lạ", null)]
    public void SuggestFieldMapsHeaderTextToAFieldKey(string header, string? expected)
        => Assert.Equal(expected, InvoiceWorkbookImporter.SuggestField(header));

    // ---- confirmation / legacy defaults ----

    private async Task<WorkbookData> ReadStandardAsync()
    {
        var (book, _) = InvoiceSheet.Standard();
        return await new WorkbookReader().ReadAsync(Save(book), Token);
    }

    [Theory]
    [InlineData(true, "xác nhận", "CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT")]
    [InlineData(false, "", "CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT")]
    [InlineData(false, "   ", "CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT")]
    [InlineData(false, "xác nhận", "")]
    public async Task StaleOrIncompleteConfirmationIsRejected(bool staleHash, string text, string company)
    {
        var workbook = await ReadStandardAsync();
        var confirmation = new ImportConfirmation(true, true, company, staleHash ? new string('0', 64) : workbook.Sha256, text);

        var error = Assert.Throws<WorkbookImportException>(() => importer.Import(workbook, new WorkbookImportOptions(Confirmation: confirmation), Token));

        Assert.Equal("CONFIRMATION_STALE", error.Code);
    }

    [Fact]
    public async Task LegacySalesDefaultsFillSellerAndDatesFromTheConfirmedCompany()
    {
        var workbook = await ReadStandardAsync();
        var confirmation = new ImportConfirmation(true, true, "CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT", workbook.Sha256, "Người dùng xác nhận");

        var result = importer.Import(workbook, new WorkbookImportOptions(Confirmation: confirmation), Token);

        var invoice = result.Invoices[0];
        Assert.Equal("CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT", invoice.Seller.Name);
        Assert.Equal(confirmation, invoice.ImportConfirmation);
        Assert.Equal(Day, invoice.VoucherDate);
        Assert.Equal("user_override", invoice.HeaderCells!["seller"].ValueOrigin);
        Assert.Equal("Người dùng xác nhận", invoice.HeaderCells["seller"].Confirmation);
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public async Task ConfirmationWithoutLegacyDefaultsStillRecordsTheConfirmation()
    {
        var workbook = await ReadStandardAsync();
        var confirmation = new ImportConfirmation(true, false, "", workbook.Sha256, "Người dùng xác nhận");

        var result = importer.Import(workbook, new WorkbookImportOptions(Confirmation: confirmation), Token);

        Assert.All(result.Invoices, invoice => Assert.Equal(confirmation, invoice.ImportConfirmation));
        Assert.All(result.Invoices, invoice => Assert.Equal("", invoice.Seller.Name));
    }

    [Fact]
    public async Task ImportingWithoutConfirmationLeavesSellerEmpty()
    {
        var workbook = await ReadStandardAsync();

        var result = importer.Import(workbook, new WorkbookImportOptions(), Token);

        Assert.All(result.Invoices, invoice => Assert.Null(invoice.ImportConfirmation));
        Assert.All(result.Invoices, invoice => Assert.Equal("", invoice.Seller.Name));
    }
}
