using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class StockBalanceTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private static readonly DateOnly Day = new(2026, 1, 15);

    public void Dispose() => temp.Dispose();

    private ItemCatalogStore Catalog(params ItemCatalogEntry[] entries)
    {
        var store = new ItemCatalogStore(temp.Combine("state", "item-catalog.json"));
        store.Import(entries, temp.Combine("export.xlsx"));
        return store;
    }

    private static InvoiceDocument Invoice(string number, int day, params (string Name, string Unit, decimal Quantity)[] lines)
    {
        var date = Day.AddDays(day);
        var invoice = new InvoiceBuilder().WithNumber(number).WithIssued(date)
            .WithLines([.. lines.Select((line, index) => InvoiceBuilder.Line(index, line.Name, line.Unit, line.Quantity, 10_000m))]).Build();
        return invoice with { InvoiceId = "invoice-" + number };
    }

    private static OpeningStockEntry Opening(string code, decimal quantity) => new(code, "", "", quantity);

    [Theory]
    [InlineData("Đầu Trâu bón đòng nuôi củ quả NPK 15-4-18", "kg", "Phân Đầu Trâu bón đòng nuôi củ quả NPK 15-4-18", "kg")]
    [InlineData("Phân NPK 16-16-8+13S HCQN (BM-25 kg-OPP)- xanh", "kg", "Phân NPK 16-16-8+13S HCQN (BM-25 kg-OPP)- xanh -TN", "kg")]
    [InlineData("Chani 300EC - 100ml (ĐN)", "Chai", "Chani 300EC- 100ml (Nifit)", "Chai")]
    [InlineData("Chani 300EC - 100ml (ĐN)", "Chai", "Chani 300EC - 100ml (ĐN Nicotex)", "Chai")]
    [InlineData("Phân NPK 16-16-8+13S+TE Phú Mỹ (Bao 25kg)", "kg", "Phân NPK 16-16-8+13S+TE Phú Mỹ (Bao 50kg)", "Tấn")]
    [InlineData("FUJI ONE 40EC-100CC PET-M", "Chai", "FUJI ONE 40EC 100CC PET", "Chai")]
    [InlineData("Nón tai bèo (Hàng khuyến mãi không thu tiền)", "Cái", "NÓN TAI BÈO", "Cái")]
    public void DistributorTagsPrefixesAndBagSizesAreTheSameGoods(string name, string unit, string other, string otherUnit)
        => Assert.Equal(ItemFamilies.SameItemKey(name, unit), ItemFamilies.SameItemKey(other, otherUnit));

    [Theory]
    [InlineData("Catex 3.6EC 450ml", "Chai", "Catex 3.6EC 450ml (ĐN - chai tím - nhãn tím - nước trong)", "Chai")]
    [InlineData("Chani 300EC - 100ml", "Chai", "Chani 300EC - 450ml", "Chai")]
    [InlineData("Chlorin 10SC - 35ml", "Gói", "Chlorin 10SC - 25ml", "Gói")]
    [InlineData("Phân NPK 16-16-8-TE", "kg", "Phân NPK 16-16-8", "kg")]
    [InlineData("Phân NPK 16-16-8+13S HCQN (BM-BV)", "kg", "Phân NPK 16-16-8+13S HCQN (BM-25 kg-OPP)- xanh", "kg")]
    [InlineData("Dylan 10WG - 10gr", "Gói", "Dylan 10WG - 10gr", "Chai")]
    public void VariantsSizesAndCountUnitsStayApart(string name, string unit, string other, string otherUnit)
        => Assert.NotEqual(ItemFamilies.SameItemKey(name, unit), ItemFamilies.SameItemKey(other, otherUnit));

    [Theory]
    [InlineData("Newcat 2.5WP - 1kg", "kg", "Newcate 2.5WP - 1kg", "Gói", true)]
    [InlineData("Fuamy 40EC - 450ml (ĐN)", "Chai", "Fu-army 40EC 450ml (ĐN)", "Chai", true)]
    [InlineData("FUJI ONE 40EC-450CC PET", "Chai", "FUJI ONE 40EC-450CC PEPA", "Chai", true)]
    [InlineData("Phân NPK 16-16-8+13S HCQN (BM-BV)", "kg", "Phân NPK 16-16-8+13S HCQN (BM-25 kg-OPP)- xanh", "kg", true)]
    [InlineData("Hesman 60WG 18g", "Gói", "Hesman 60WG 25gr", "Gói", false)]
    [InlineData("Phân kali", "kg", "Phân Kali bột Phú Mỹ", "kg", false)]
    [InlineData("Fuamy 40EC - 450ml", "Chai", "Fuamy 40EC - 450ml", "Thùng", false)]
    public void NearEqualNamesAreOnlySimilar(string name, string unit, string other, string otherUnit, bool similar)
        => Assert.Equal(similar, ItemFamilies.Similar(name, unit, other, otherUnit));

    [Fact]
    public void OpeningStockIsSoldFirstThenTheNewCodeOfTheSameGoods()
    {
        var catalog = Catalog(new("HH_69", "Chani 300EC - 100ml (ĐN)", "Chai"), new("HH_70", "Chani 300EC- 100ml (Nifit)", "Chai"),
            new("HH_339", "Chani 300EC - 100ml (ĐN Nicotex)", "Chai"));
        var purchases = new[] { Invoice("P1", 1, ("Chani 300EC - 100ml (ĐN-Nicotex)", "Chai", 100)) };
        var sales = new[]
        {
            Invoice("1", 0, ("Chani 300EC - 100ml (ĐN Nicotex)", "Chai", 40), ("Chani 300EC- 100ml (Nifit)", "Chai", 20)),
            Invoice("2", 1, ("Chani 300EC - 100ml (ĐN Nicotex)", "Chai", 30)),
            Invoice("3", 2, ("Chani 300EC- 100ml (Nifit)", "Chai", 15), ("Chani 300EC - 100ml (ĐN Nicotex)", "Chai", 10))
        };

        var plan = StockBalancer.Plan(catalog, [Opening("HH_69", 50), Opening("HH_70", 30)], null, purchases, sales);

        Assert.Equal(["HH_69", "HH_70", "HH_339", "HH_339", "HH_69"], plan.Sales.Select(line => line.Code));
        Assert.Equal([StockAllocationKind.Family, StockAllocationKind.Matched, StockAllocationKind.Matched, StockAllocationKind.Family,
            StockAllocationKind.Family], plan.Sales.Select(line => line.Kind));
        Assert.Empty(plan.NegativeCodes);
        Assert.Equal([0m, 10m, 55m], new[] { "HH_69", "HH_70", "HH_339" }.Select(code => plan.Codes.Single(row => row.Code == code).Closing));
        // A line moved to a code whose name still matches keeps the invoice's own wording.
        Assert.Equal("Chani 300EC - 100ml (ĐN Nicotex)", plan.Sales[0].Name);
        Assert.Contains(plan.Families, group => group.Select(item => item.Code).SequenceEqual(["HH_69", "HH_70", "HH_339"]));
    }

    [Fact]
    public void UnitsConvertBetweenKilogramsTonsAndPacksOfStatedWeight()
    {
        var catalog = Catalog(new("HH_320", "Newcat 2.5WP - 1kg", "kg"), new("HH_327", "Newcate 2.5WP - 1kg", "Gói"),
            new("HH_152", "Phân NPK 16-16-8+13S+TE Phú Mỹ (Bao 25kg)", "kg"),
            new("HH_97", "Phân NPK 16-16-8+13S+TE Phú Mỹ (Bao 50kg)", "Tấn", "kg", ConversionRate: 1000, ConversionOperation: "/"),
            new("HH_177", "Sông Gianh HC-15 bột (bao 25kg)", "Tấn"));
        var purchases = new[] { Invoice("P1", 0, ("Newcate 2.5WP - 1kg", "Gói", 100)) };
        var sales = new[]
        {
            Invoice("1", 1, ("Newcat 2.5WP - 1kg", "Kg", 30), ("Phân NPK 16-16-8+13S+TE Phú Mỹ (Bao 25kg)", "kg", 500),
                ("Sông Gianh HC-15 bột (bao 25kg)", "Kg", 250))
        };

        var plan = StockBalancer.Plan(catalog, [Opening("HH_97", 5), Opening("HH_177", 1)], null, purchases, sales);

        var newcat = plan.Sales[0];
        Assert.Equal(("HH_320", "HH_327", StockAllocationKind.Similar), (newcat.MatchedCode, newcat.Code, newcat.Kind));
        Assert.Equal(("Newcate 2.5WP - 1kg", "Gói", 30m, 10_000m), (newcat.Name, newcat.Unit, newcat.Quantity, newcat.UnitPrice));
        Assert.True(newcat.Converted);
        // kg is registered on HH_97, so MISA converts it: the line keeps its unit and counts 0.5 tấn.
        var bag = plan.Sales[1];
        Assert.Equal(("HH_97", StockAllocationKind.Family, "kg", 500m, 0.5m), (bag.Code, bag.Kind, bag.Unit, bag.Quantity, bag.StockQuantity));
        // The tool itself converts Kg to an unregistered Tấn unit.
        var tons = plan.Sales[2];
        Assert.Equal(("HH_177", "Kg", 250m, 0.25m), (tons.Code, tons.Unit, tons.Quantity, tons.StockQuantity));
        Assert.Equal(4.5m, plan.Codes.Single(row => row.Code == "HH_97").Closing);
        Assert.Equal(70m, plan.Codes.Single(row => row.Code == "HH_327").Closing);
        Assert.Empty(plan.NegativeCodes);
    }

    [Fact]
    public void SaleBeforeThePurchaseBorrowsAndAShortageStaysOnItsOwnCode()
    {
        var catalog = Catalog(new("HH_88", "Fuamy 40EC - 450ml (ĐN)", "Chai"), new("HH_344", "Fu-army 40EC 450ml (ĐN)", "Chai"),
            new("HH_159", "Marvel 30SL - 7ml", "Gói"));
        var purchases = new[] { Invoice("P1", 10, ("Fu-army 40EC 450ml (ĐN)", "Chai", 80)) };
        var sales = new[]
        {
            Invoice("1", 5, ("Fuamy 40EC - 450ml (ĐN)", "Chai", 30)),
            Invoice("2", 12, ("Fuamy 40EC - 450ml (ĐN)", "Chai", 60), ("Marvel 30SL - 7ml", "Gói", 577))
        };

        var plan = StockBalancer.Plan(catalog, [Opening("HH_159", 528)], null, purchases, sales);

        Assert.Equal([("HH_344", StockAllocationKind.Borrowed, 30m), ("HH_88", StockAllocationKind.Shortage, 60m),
            ("HH_159", StockAllocationKind.Shortage, 49m)], plan.Sales.Select(line => (line.Code!, line.Kind, line.Missing)));
        var negative = plan.NegativeCodes.ToDictionary(code => code.Code);
        Assert.Equal((-30m, Day.AddDays(5), 50m), (negative["HH_344"].Lowest, negative["HH_344"].LowestOn!.Value, negative["HH_344"].Closing));
        Assert.Equal(-60m, negative["HH_88"].Closing);
        Assert.Equal(-49m, negative["HH_159"].Closing);
    }

    [Fact]
    public void ExcelCodesAreKeptAndUnknownPurchasesAndWrongRatiosAreReported()
    {
        var catalog = Catalog(new("HH_69", "Chani 300EC - 100ml (ĐN)", "Chai"), new("HH_70", "Chani 300EC- 100ml (Nifit)", "Chai"),
            new("HH_312", "Phân NPK Cà Mau 20-10-15+1Mg+TE-25kg", "Tấn", "kg", ConversionRate: 1, ConversionOperation: "/"));
        var sale = Invoice("1", 3, ("Chani 300EC- 100ml (Nifit)", "Chai", 5), ("Phân NPK Cà Mau 20-10-15+1Mg+TE-25kg", "Kg", 500));
        sale = sale with { Lines = [sale.Lines[0] with { ItemCode = "HH_70" }, sale.Lines[1]] };
        var purchases = new[] { Invoice("P1", 0, ("Bia Huda lon 24 x 330ml", "Thùng", 23), ("Phân NPK Cà Mau 20-10-15+1Mg+TE-25kg", "Tấn", 2)),
            Invoice("P0", -30, ("Chani 300EC- 100ml (Nifit)", "Chai", 1000)) };

        var plan = StockBalancer.Plan(catalog, [Opening("HH_69", 50), Opening("HH_70", 10)], Day, purchases, [sale]);

        Assert.Equal(("HH_70", StockAllocationKind.Fixed), (plan.Sales[0].Code!, plan.Sales[0].Kind));
        Assert.Equal(1, plan.UnresolvedPurchases);
        Assert.Equal(1, plan.Purchases);
        Assert.Contains(plan.Warnings, warning => warning.Contains("Bia Huda lon 24 x 330ml", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, warning => warning.Contains("HH_312", StringComparison.Ordinal) && warning.Contains("1000", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, warning => warning.Contains("Bỏ qua 1 hóa đơn trước ngày", StringComparison.Ordinal));
        // MISA's own ratio (1 kg = 1 tấn) is what its stock will show: 500 tấn out of 2.
        Assert.Equal(-498m, plan.Codes.Single(row => row.Code == "HH_312").Closing);
    }
}
