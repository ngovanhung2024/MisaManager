using MisaManager.Core.Tests.TestData;
using MisaManager.Import;

namespace MisaManager.Core.Tests;

public sealed class ItemCatalogWorkbookTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly WorkbookReader reader = new();

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Task<IReadOnlyList<ItemCatalogEntry>> Read(WorkbookBuilder book)
        => ItemCatalogWorkbook.ReadAsync(reader, book.Save(temp.Combine("vat-tu.xlsx")), Token);

    /// <summary>Layout of the MISA "Danh mục vật tư, hàng hóa" export: title, blank row, header, items, "Số dòng" footer.</summary>
    private static WorkbookBuilder MisaExport()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Vật tư hàng hóa");
        sheet.Row(1, "DANH MỤC VẬT TƯ, HÀNG HÓA").Merge("A1:D1");
        sheet.Row(3, "Mã", "Tên", "ĐVT chính", "Đơn vị chuyển đổi");
        sheet.Row(4, "CM GOLD 20.10.10", "Phân NPK Cà Mau Gold 20-10-10-25kg", "Tấn", "kg");
        sheet.Row(5, "CPMH", "Chi phí mua hàng", null, null);
        sheet.Row(6, "HH _CAI", "HANG HOA CAI", "Cái");
        sheet.Row(7, "HH_01", "Chery 15g", "Gói");
        sheet.Row(8, "HH_02", "Phân NPK 16.16.813S QN", "kg");
        sheet.Row(9, "Số dòng = 5");
        return book;
    }

    [Fact]
    public async Task ReadsTheMisaExportAndSkipsTheFooter()
    {
        var items = await Read(MisaExport());

        Assert.Equal(
        [
            new ItemCatalogEntry("CM GOLD 20.10.10", "Phân NPK Cà Mau Gold 20-10-10-25kg", "Tấn", "kg"),
            new ItemCatalogEntry("CPMH", "Chi phí mua hàng", ""),
            new ItemCatalogEntry("HH _CAI", "HANG HOA CAI", "Cái"),
            new ItemCatalogEntry("HH_01", "Chery 15g", "Gói"),
            new ItemCatalogEntry("HH_02", "Phân NPK 16.16.813S QN", "kg")
        ], items);
    }

    [Theory]
    [InlineData("Mã hàng", "Tên hàng", "ĐVT")]
    [InlineData("Ma hang", "Ten hang hoa", "DVT chinh")]
    [InlineData("MÃ VẬT TƯ", "TÊN VẬT TƯ HÀNG HÓA", "ĐƠN VỊ TÍNH")]
    [InlineData("Mã VTHH", "Tên VTHH", "Đơn vị tính chính")]
    [InlineData("  Mã  ", " Tên ", " ĐVT   chính ")]
    public async Task HeaderAliasesAreAcceptedWithAndWithoutDiacritics(string code, string name, string unit)
    {
        var book = new WorkbookBuilder();
        book.Sheet("S").Row(1, code, name, unit).Row(2, "HH_01", "Chery 15g", "Gói");

        var item = Assert.Single(await Read(book));

        Assert.Equal(new ItemCatalogEntry("HH_01", "Chery 15g", "Gói"), item);
    }

    [Fact(Skip = "BUG (suspected): src/MisaManager.Import/ItemCatalogWorkbook.cs:12 lists the alias \"ten vat tu hang hoa\", but Normalize() keeps punctuation, "
        + "so the natural header \"Tên vật tư, hàng hóa\" (with a comma) normalises to \"ten vat tu, hang hoa\" and is not recognised.")]
    public async Task HeaderWithACommaBetweenItemAndGoodsIsAccepted()
    {
        var book = new WorkbookBuilder();
        book.Sheet("S").Row(1, "Mã vật tư, hàng hóa", "Tên vật tư, hàng hóa", "ĐVT chính").Row(2, "HH_01", "Chery 15g", "Gói");

        Assert.Single(await Read(book));
    }

    [Fact]
    public async Task UnitColumnsAreOptional()
    {
        var book = new WorkbookBuilder();
        book.Sheet("S").Row(1, "Mã", "Tên").Row(2, "HH_01", "Chery 15g");

        var item = Assert.Single(await Read(book));

        Assert.Equal("", item.Unit);
        Assert.Equal("", item.ConversionUnit);
    }

    [Fact]
    public async Task HeaderMayBeFarDownTheSheet()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("S");
        for (var row = 1; row <= 20; row++) sheet.Row(row, "dòng tiêu đề số " + row);
        sheet.Row(25, "Mã", "Tên", "ĐVT").Row(26, "HH_01", "Chery 15g", "Gói");

        Assert.Single(await Read(book));
    }

    [Fact]
    public async Task HeaderBeyondTheThirtiethRowIsNotFound()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("S");
        for (var row = 1; row <= 40; row++) sheet.Row(row, "dòng " + row);
        sheet.Row(41, "Mã", "Tên").Row(42, "HH_01", "Chery 15g");

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() => Read(book));

        Assert.Equal("ITEM_CATALOG_HEADER_MISSING", error.Code);
    }

    [Fact]
    public async Task RowsWithoutCodeOrNameAreDropped()
    {
        var book = new WorkbookBuilder();
        book.Sheet("S").Row(1, "Mã", "Tên", "ĐVT")
            .Row(2, "HH_01", "Chery 15g", "Gói")
            .Row(3, null, "Không có mã", "kg")
            .Row(4, "HH_03", null, "kg")
            .Row(5, "   ", "Mã toàn khoảng trắng", "kg")
            .Row(6, "HH_06", "Phân kali", "kg");

        var items = await Read(book);

        Assert.Equal(["HH_01", "HH_06"], items.Select(item => item.Code));
    }

    [Fact]
    public async Task CodesAndNamesAreReadAsTextEvenWhenTheyLookLikeNumbers()
    {
        var book = new WorkbookBuilder();
        book.Sheet("S").Row(1, "Mã", "Tên", "ĐVT").Row(2, "007", "Phân kali", "kg").Row(3, 15, "Thuốc 15", "Chai");

        var items = await Read(book);

        Assert.Equal(["007", "15"], items.Select(item => item.Code));
    }

    [Fact]
    public async Task FirstSheetWithAHeaderAndItemsWins()
    {
        var book = new WorkbookBuilder();
        book.Sheet("Trống").Row(1, "Mã", "Tên");
        book.Sheet("Dữ liệu").Row(1, "Mã", "Tên", "ĐVT").Row(2, "HH_01", "Chery 15g", "Gói");
        book.Sheet("Khác").Row(1, "Mã", "Tên", "ĐVT").Row(2, "HH_99", "Không dùng", "kg");

        var items = await Read(book);

        Assert.Equal("HH_01", Assert.Single(items).Code);
    }

    [Theory]
    [InlineData("Mã", "Tuổi")]
    [InlineData("Số", "Tên")]
    public async Task HeaderNeedsBothACodeAndANameColumn(string first, string second)
    {
        var book = new WorkbookBuilder();
        book.Sheet("S").Row(1, first, second).Row(2, "HH_01", "Chery 15g");

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() => Read(book));

        Assert.Equal("ITEM_CATALOG_HEADER_MISSING", error.Code);
        Assert.Contains("Mã", error.Message);
    }

    [Fact]
    public async Task ExportWithOnlyAFooterHasNoItems()
    {
        var book = new WorkbookBuilder();
        book.Sheet("S").Row(1, "Mã", "Tên").Row(2, "Số dòng = 0");

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() => Read(book));

        Assert.Equal("ITEM_CATALOG_HEADER_MISSING", error.Code);
    }

    [Fact]
    public async Task NonXlsxFilesAreRejectedByTheReader()
    {
        var path = temp.WriteText("vat-tu.csv", "Mã,Tên");

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() => ItemCatalogWorkbook.ReadAsync(reader, path, Token));

        Assert.Equal("UNSUPPORTED_EXTENSION", error.Code);
    }

    [Fact]
    public async Task ExportFeedsTheCatalogStoreAndLookupsWork()
    {
        var items = await Read(MisaExport());
        var export = temp.Combine("vat-tu.xlsx");
        var store = new ItemCatalogStore(temp.Combine("state", "item-catalog.json"));

        var count = store.Merge(items, export);

        Assert.Equal(5, count);
        Assert.Equal("HH_01", store.Find("CHERY 15G", "gói")!.Code);
        Assert.Equal("CM GOLD 20.10.10", store.Find("Phân NPK Cà Mau Gold 20–10–10–25 kg", "kg")!.Code);
        Assert.Equal("kg", store.CanonicalUnit("KG"));
        Assert.Equal("HH_03", store.NextCode(proposed: "HH_03"));
    }

    [Fact]
    public async Task ReloadingTheSameExportKeepsTheCatalogStable()
    {
        var items = await Read(MisaExport());
        var store = new ItemCatalogStore(temp.Combine("state", "item-catalog.json"));

        store.Merge(items, temp.Combine("vat-tu.xlsx"));
        var again = store.Merge(await Read(MisaExport()), temp.Combine("vat-tu.xlsx"));

        Assert.Equal(5, again);
    }

    [Fact]
    public async Task LegacyCatalogRecoversExportProvenanceWithoutLosingPendingCreations()
    {
        var export = MisaExport().Save(temp.Combine("vat-tu.xlsx"));
        var path = temp.Combine("state", "item-catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = new PendingItemCreation("HH_999", "Chưa xác nhận", "kg", DateTimeOffset.UtcNow.AddMinutes(-1));
        var legacy = new ItemCatalogFile(1, DateTimeOffset.UtcNow, export,
            [new("HH_01", "Chery 15g", "Gói"), new("HH_340", "CHERY 15G", "Gói")], [pending]);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(legacy), Token);
        var store = new ItemCatalogStore(path);

        await ItemCatalogWorkbook.EnsureImportedProvenanceAsync(reader, store, Token);

        Assert.True(store.HasImportedProvenance);
        var reopened = new ItemCatalogStore(path);
        Assert.Equal("HH_01", reopened.Find("Chery 15g", "Gói")!.Code);
        Assert.Equal(pending, Assert.Single(reopened.PendingSnapshot()));
        Assert.NotNull(reopened.FindByCode("HH_340"));
    }

    [Fact]
    public async Task LegacyCatalogWithMissingSourceStopsBeforeUsingGeneratedCodes()
    {
        var path = temp.Combine("state", "item-catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var legacy = new ItemCatalogFile(1, DateTimeOffset.UtcNow, temp.Combine("missing.xlsx"),
            [new("HH_340", "Hàng cũ", "kg")]);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(legacy), Token);

        var error = await Assert.ThrowsAsync<AutomationException>(() =>
            ItemCatalogWorkbook.EnsureImportedProvenanceAsync(reader, new ItemCatalogStore(path), Token));

        Assert.Equal("ITEM_CATALOG_SOURCE_UNAVAILABLE", error.Code);
        Assert.False(error.InputMayHaveBeenSent);
    }
}
