using MisaManager.Core.Tests.TestData;
using MisaManager.Import;

namespace MisaManager.Core.Tests;

public sealed class PostedInvoiceTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PostedInvoiceStore Store() => new(temp.Combine("posted-invoices.json"));

    [Fact]
    public void NumberAndSeriesDecideWhetherAnInvoiceIsPosted()
    {
        var store = Store();
        store.Replace([new("1733", "1C26MLV", "PT02403"), new("0002", "1C26MLV"), new("50", "")], "Ban_hang.xlsx");

        Assert.True(store.Contains("1733", "1C26MLV"));
        Assert.True(store.Contains("00001733", "1c26mlv"));
        Assert.True(store.Contains("2", "1C26MLV"));
        // Form digit kept in a separate column of the invoice workbook.
        Assert.True(store.Contains("1733", "C26MLV"));
        // No series on one side: never risk entering it twice.
        Assert.True(store.Contains("1733", ""));
        Assert.True(store.Contains("50", "1C25TAA"));
        Assert.False(store.Contains("1733", "1C25TAA"));
        Assert.False(store.Contains("1734", "1C26MLV"));
        Assert.False(store.Contains("", ""));
    }

    [Fact]
    public void ListIsKeptOnDiskAndReplacedOrClearedAsAWhole()
    {
        var invoice = new InvoiceBuilder().WithNumber("1655").Build();
        Store().Replace([new(invoice.Identity.Number, invoice.Identity.Series)], "a.xlsx");

        var reopened = Store();
        Assert.True(reopened.Contains(invoice));
        Assert.Equal(("a.xlsx", 1), (reopened.Source, reopened.Count));

        reopened.Replace([new("1", "1C26MLV")], "b.xlsx");
        Assert.False(Store().Contains(invoice));

        reopened.Clear();
        Assert.Equal(0, Store().Count);
    }

    /// <summary>Layout of MISA's "Danh sách bán hàng" export (the attached Ban_hang.xlsx).</summary>
    [Fact]
    public async Task SalesListExportIsReadWithoutFooter()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Ban_hang");
        sheet.Row(1, "DANH SÁCH BÁN HÀNG").Merge("A1:O1");
        sheet.Row(2, "Ngày hạch toán", "Ngày chứng từ", "Số chứng từ", "Số hóa đơn", "Mẫu số HĐ", "Ký hiệu HĐ", "Khách hàng",
            "Diễn giải", "Tổng tiền hàng", "Tiền chiết khấu", "Tiền thuế GTGT", "Tổng tiền thanh toán");
        sheet.Row(3, 46289, 46289, "PT02403", "1733", null, "1C26MLV", "BÁN CHO NGƯỜI TIÊU DÙNG",
            "Thu tiền bán hàng theo hóa đơn 1733", 7255666, 0, 362783, 7618449);
        sheet.Row(4, 46037, 46037, "BH00133", "1", null, "1C26MLV", "LÊ THỊ HƯƠNG");
        sheet.Row(5, 46037, 46037, "BH00134", null, null, null, "Chưa lập hóa đơn");
        sheet.Row(6, "Số dòng = 3", null, null, null, null, null, null, null, 12745072195, 0, 636686122, 13381758317);

        var entries = await PostedInvoiceWorkbook.ReadAsync(new WorkbookReader(), book.Save(temp.Combine("Ban_hang.xlsx")), Token);

        Assert.Equal([new PostedInvoiceEntry("1733", "1C26MLV", "PT02403"), new PostedInvoiceEntry("1", "1C26MLV", "BH00133")], entries);
    }

    [Fact]
    public async Task WorkbookWithoutInvoiceNumberColumnIsRejected()
    {
        var book = new WorkbookBuilder();
        book.Sheet("S").Row(1, "Số chứng từ", "Khách hàng").Row(2, "PT01", "x");

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() =>
            PostedInvoiceWorkbook.ReadAsync(new WorkbookReader(), book.Save(temp.Combine("sai.xlsx")), Token));
        Assert.Equal("POSTED_INVOICE_HEADER_MISSING", error.Code);
    }
}
