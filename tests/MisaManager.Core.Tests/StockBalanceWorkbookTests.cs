using MisaManager.Core.Tests.TestData;
using MisaManager.Import;

namespace MisaManager.Core.Tests;

public sealed class StockBalanceWorkbookTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => temp.Dispose();

    /// <summary>Layout of MISA's "Tổng hợp tồn kho" export (TONG_HOP_TON_KHO.xlsx).</summary>
    private string InventoryReport()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Báo cáo");
        sheet.Row(1, "TỔNG HỢP TỒN KHO").Merge("A1:L1");
        sheet.Row(2, "Kho: Kho hàng hóa; Từ ngày 01/01/2026 đến ngày 09/11/2026").Merge("A2:L2");
        sheet.Row(3, null, "Mã hàng", "Tên hàng", "ĐVT", "Đầu kỳ", null, "Nhập kho", null, "Xuất kho", null, "Cuối kỳ", null)
            .Merge("E3:F3").Merge("G3:H3").Merge("I3:J3").Merge("K3:L3");
        sheet.Row(4, null, null, null, null, "Số lượng", "Giá trị", "Số lượng", "Giá trị", "Số lượng", "Giá trị", "Số lượng", "Giá trị");
        sheet.Row(5, "Tên kho : Kho hàng hóa (3 )", null, null, null, 15925.5, 528864888, 1, 1, 2, 2, 15924.5, 528864887);
        sheet.Row(6, null, "HH_209", "Phân NPK 16-16-8+13S HCQN (BM-25 kg-OPP)- xanh", "kg", 15900, 181293440, 0, 0, 0, 0, 15900, 181293440);
        sheet.Row(7, null, "HH_129", "Phân NPK Việt Nhật 16-16-8+13S TPH-G01C", "Tấn", 25, 347619048, 1, 1, 2, 2, 24, 347619047);
        sheet.Row(8, null, "HH_57", "Hàng âm kho", "Cái", -0.5, 0, 0, 0, 0, 0, -0.5, 0);
        sheet.Row(9, null, "Số dòng = 3");
        return book.Save(temp.Combine("TONG_HOP_TON_KHO.xlsx"));
    }

    [Fact]
    public async Task InventoryReportGivesOpeningQuantityAndPeriodStart()
    {
        var (items, asOf) = await OpeningStockWorkbook.ReadAsync(new WorkbookReader(), InventoryReport(), Token);

        Assert.Equal(new DateOnly(2026, 1, 1), asOf);
        Assert.Equal(
        [
            new OpeningStockEntry("HH_209", "Phân NPK 16-16-8+13S HCQN (BM-25 kg-OPP)- xanh", "kg", 15900, 181293440),
            new OpeningStockEntry("HH_129", "Phân NPK Việt Nhật 16-16-8+13S TPH-G01C", "Tấn", 25, 347619048),
            new OpeningStockEntry("HH_57", "Hàng âm kho", "Cái", -0.5m, 0)
        ], items);
    }

    [Fact]
    public void StoreSumsWarehousesAndSurvivesReload()
    {
        var path = temp.Combine("state", "opening-stock.json");
        var store = new OpeningStockStore(path);

        store.Replace([new("HH_69", "Chani", "Chai", 500, 10), new("hh_69", "Chani", "Chai", 400, 5), new("HH_70", "Chani", "Chai", 1647)],
            new DateOnly(2026, 1, 1), "TONG_HOP_TON_KHO.xlsx");

        var reloaded = new OpeningStockStore(path);
        Assert.Equal(new DateOnly(2026, 1, 1), reloaded.AsOf);
        Assert.Equal([("HH_69", 900m, 15m), ("HH_70", 1647m, 0m)], reloaded.Snapshot().Select(item => (item.Code, item.Quantity, item.Value)));
        reloaded.Clear();
        Assert.Equal(0, new OpeningStockStore(path).Count);
    }

    [Fact]
    public async Task BalancedSalesWorkbookImportsWithCodesAndConvertedUnits()
    {
        var day = new DateOnly(2026, 2, 3);
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("HoaDon");
        sheet.Row(1, InvoiceSheet.Headers.Cast<object?>().ToArray());
        sheet.Row(2, InvoiceSheet.Line("1000", day, "Lê Thị Hương", null, null, "Newcat 2.5WP - 1kg", "Kg", 30, 10_000m));
        sheet.Row(3, InvoiceSheet.Line("1000", day, "Lê Thị Hương", null, null, "Chani 300EC - 100ml (ĐN Nicotex)", "Chai", 10, 20_000m));
        sheet.Row(4, InvoiceSheet.Line("1001", day, "Lê Thị Hương", null, null, "Hesman 60WG 18g", "Gói", 2, 16_000m));
        var source = book.Save(temp.Combine("ban_ra.xlsx"));
        var reader = new WorkbookReader();
        var imported = await new InvoiceWorkbookImporter(reader).ImportAsync(source, null, Token);
        var catalog = new ItemCatalogStore(temp.Combine("state", "item-catalog.json"));
        catalog.Import([new("HH_320", "Newcat 2.5WP - 1kg", "kg"), new("HH_327", "Newcate 2.5WP - 1kg", "Gói"),
            new("HH_69", "Chani 300EC - 100ml (ĐN)", "Chai"), new("HH_339", "Chani 300EC - 100ml (ĐN Nicotex)", "Chai")],
            temp.Combine("export.xlsx"));
        var plan = StockBalancer.Plan(catalog, [new("HH_69", "", "", 50), new("HH_327", "", "", 100)], null, [], imported.Invoices);
        var balanced = temp.Combine("ban_ra_can_doi_kho.xlsx");

        Assert.Equal(2, StockBalanceWorkbook.WriteSales(await reader.ReadAsync(source, Token), imported, plan, balanced));
        var again = await new InvoiceWorkbookImporter(reader).ImportAsync(balanced, null, Token);

        Assert.DoesNotContain(again.Issues, issue => issue.Severity == IssueSeverity.Blocking);
        Assert.Single(again.Invoices);
        Assert.All(again.Invoices, invoice => Assert.False(invoice.IsBlocked));
        var lines = again.Invoices.SelectMany(invoice => invoice.Lines).ToArray();
        Assert.Equal(("HH_327", "Newcate 2.5WP - 1kg", "Gói", 30m, 300_000m),
            (lines[0].ItemCode, lines[0].Name, lines[0].Unit, lines[0].Quantity!.Value, lines[0].AmountBeforeTax!.Value));
        Assert.Equal(("HH_69", "Chani 300EC - 100ml (ĐN Nicotex)", "Chai", 10m), (lines[1].ItemCode, lines[1].Name, lines[1].Unit, lines[1].Quantity!.Value));
        Assert.Equal(2, lines.Length);
        Assert.Equal(day, again.Invoices.Single().Issued);
        var copy = (await reader.ReadAsync(balanced, Token)).Sheets.Single();
        Assert.Equal("Newcat 2.5WP - 1kg", copy.Rows.Single(row => row.Number == 2).Cells.Values
            .Single(cell => cell.Column == InvoiceSheet.Headers.Length + 2).ParsedValue);
    }

    [Fact]
    public async Task FormulaCellsAreNotFlattenedAndHiddenRowsStayHidden()
    {
        var day = new DateOnly(2026, 2, 3);
        var reader = new WorkbookReader();
        var catalog = new ItemCatalogStore(temp.Combine("state", "item-catalog.json"));
        catalog.Import([new("HH_16", "Hesman 60WG 18g", "Gói")], temp.Combine("export.xlsx"));
        async Task<(WorkbookData Data, ImportResult Imported, StockBalancePlan Plan)> Load(string name, Action<SheetBuilder> edit)
        {
            var book = new WorkbookBuilder();
            var sheet = book.Sheet("HoaDon");
            sheet.Row(1, InvoiceSheet.Headers.Cast<object?>().ToArray());
            sheet.Row(2, InvoiceSheet.Line("1000", day, "Lê Thị Hương", null, null, "Hesman 60WG 18g", "Gói", 2, 16_000m));
            sheet.Row(3, InvoiceSheet.Line("1001", day, "Lê Thị Hương", null, null, "Hesman 60WG 18g", "Gói", 3, 16_000m));
            edit(sheet);
            var source = book.Save(temp.Combine(name));
            var imported = await new InvoiceWorkbookImporter(reader).ImportAsync(source, null, Token);
            return (await reader.ReadAsync(source, Token), imported, StockBalancer.Plan(catalog, [new("HH_16", "", "", 10)], null, [], imported.Invoices));
        }

        // The importer holds a line whose amount is a formula; a copy with the cached value must not release it.
        var formula = await Load("cong_thuc.xlsx", sheet => sheet.Cell(3, InvoiceSheet.Amount, new Formula("H3*I3", 48_000m)));
        var target = temp.Combine("cong_thuc_can_doi_kho.xlsx");
        var error = Assert.Throws<WorkbookImportException>(() => StockBalanceWorkbook.WriteSales(formula.Data, formula.Imported, formula.Plan, target));
        Assert.Equal("SOURCE_CELLS_UNCONFIRMED", error.Code);
        Assert.Contains("J3", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));

        var hidden = await Load("an_dong.xlsx", sheet => sheet.Hide(3));
        var balanced = temp.Combine("an_dong_can_doi_kho.xlsx");
        Assert.Equal(2, StockBalanceWorkbook.WriteSales(hidden.Data, hidden.Imported, hidden.Plan, balanced));
        Assert.True((await reader.ReadAsync(balanced, Token)).Sheets.Single().Rows.Single(row => row.Number == 3).Hidden);
        Assert.Contains((await new InvoiceWorkbookImporter(reader).ImportAsync(balanced, null, Token)).Issues,
            issue => issue.Code == "HIDDEN_ROWS_INCLUDED");
    }

    [Fact]
    public async Task ReportListsSkippedLinesGroupsAndWarnings()
    {
        var catalog = new ItemCatalogStore(temp.Combine("state", "item-catalog.json"));
        catalog.Import([new("HH_69", "Chani 300EC - 100ml (ĐN)", "Chai"), new("HH_339", "Chani 300EC - 100ml (ĐN Nicotex)", "Chai")],
            temp.Combine("export.xlsx"));
        var sale = new InvoiceBuilder().WithLines(InvoiceBuilder.Line(0, "Chani 300EC - 100ml (ĐN Nicotex)", "Chai", 70, 20_000m)).Build();
        var plan = StockBalancer.Plan(catalog, [new("HH_69", "", "", 50)], null, [], [sale]);
        var path = temp.Combine("bao_cao_ton_kho.xlsx");

        StockBalanceWorkbook.WriteReport(plan, path);

        var report = await new WorkbookReader().ReadAsync(path, Token);
        Assert.Equal(["Tồn kho theo mã", "Dòng bán ra đổi mã", "Nhóm cùng mặt hàng", "Cảnh báo"], report.Sheets.Select(sheet => sheet.Name));
        var stock = report.Sheets[0].Rows.Single(row => row.Cells.Values.Any(cell => cell.ParsedValue == "HH_69"));
        Assert.DoesNotContain(stock.Cells.Values, cell => cell.ParsedValue == "Âm kho");
        Assert.Contains(report.Sheets[1].Rows.SelectMany(row => row.Cells.Values), cell => cell.ParsedValue?.StartsWith("Bỏ qua dòng", StringComparison.Ordinal) == true);
        Assert.Equal(3, report.Sheets[2].Rows.Length);
    }
}
