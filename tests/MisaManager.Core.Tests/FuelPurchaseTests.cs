using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class FuelPurchaseTests : IDisposable
{
    private static readonly Party LocThuy = new("CÔNG TY TNHH THƯƠNG MẠI VÀ DỊCH VỤ LỘC THỦY", "3301661802", "Huế", "", ValuePresence.Provided);
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static InvoiceDocument Purchase(Party buyer, params string[] names)
        => new InvoiceBuilder().WithBuyer(buyer)
            .WithLines([.. names.Select((name, index) => InvoiceBuilder.Line(index, name, "lít", 20, 20_000m))])
            .Build() with { InvoiceType = InvoiceKinds.Purchase };

    [Theory]
    [InlineData("Dầu Điêzen 0,05S Mức 2", FuelKind.Diesel)]
    [InlineData("Dầu Điêzen 0,001S Mức 5", FuelKind.Diesel)]
    [InlineData("Dầu diezel 0.05%S", FuelKind.Diesel)]
    [InlineData("Dầu DO 0,05S-II", FuelKind.Diesel)]
    [InlineData("Xăng E10 RON 95 Mức 3", FuelKind.Gasoline)]
    [InlineData("Xăng RON 95 Mức 5", FuelKind.Gasoline)]
    [InlineData("Nhớt Caltex super", FuelKind.Gasoline)]
    [InlineData("Dầu thủy Lực aw 68", FuelKind.Gasoline)]
    [InlineData("Dầu 5W30", FuelKind.Gasoline)]
    [InlineData("Bia Tuborg lon 24x330ml (10269)", null)]
    [InlineData("Lọc nhớt", null)]
    [InlineData("Hộp số tự động, động cơ xăng, dung tích xi lanh 1490 cc", null)]
    [InlineData("Dịch vụ kiểm định xe ô tô 25 - 40 chỗ", null)]
    public void FuelKindComesFromTheLeadingWords(string name, FuelKind? kind) => Assert.Equal(kind, FuelPurchases.Kind(name));

    [Fact]
    public void DieselIsStockedInCtyOn152AndGasolineIsExpensedOn154()
    {
        Assert.Equal(new PurchasePosting(PurchaseRoute.Stock, "CTY", "152"),
            FuelPurchases.Posting(Purchase(LocThuy, "Dầu Điêzen 0,05S Mức 2")));
        Assert.Equal(new PurchasePosting(PurchaseRoute.Expense, "CTY", "154"),
            FuelPurchases.Posting(Purchase(LocThuy, "Xăng E10 RON 95 Mức 3", "Nhớt Caltex super")));
        Assert.Null(FuelPurchases.Skipped(Purchase(LocThuy, "Xăng RON 95 Mức 3")));
    }

    [Fact]
    public void NonFuelAndMixedRouteInvoicesOfTheFuelBuyerAreSkipped()
    {
        var beer = Purchase(LocThuy, "Bia heineken bạc 250ml");
        Assert.Null(FuelPurchases.Posting(beer));
        Assert.Equal(FuelPurchases.NotFuelReason, Assert.Single(FuelPurchases.Skipped(beer)!).Reason);

        var repair = Purchase(LocThuy, "Dịch vụ sửa chữa xe ô tô", "Dầu 5W30");
        Assert.Null(FuelPurchases.Posting(repair));
        Assert.Equal([true, false], FuelPurchases.Skipped(repair)!.Select(line => line.IsService));

        var both = Purchase(LocThuy, "Dầu Điêzen 0,05S Mức 2", "Xăng RON 95 Mức 3");
        Assert.Null(FuelPurchases.Posting(both));
        Assert.All(FuelPurchases.Skipped(both)!, line => Assert.Equal(FuelPurchases.BothRoutesReason, line.Reason));
    }

    [Fact]
    public void OtherBuyersAndSalesKeepTheOrdinaryFlow()
    {
        Assert.Null(FuelPurchases.Company(Purchase(InvoiceBuilder.Organization, "Xăng RON 95 Mức 3")));
        Assert.Null(FuelPurchases.Skipped(Purchase(InvoiceBuilder.Organization, "Xăng RON 95 Mức 3")));
        var sale = Purchase(LocThuy, "Dầu Điêzen 0,05S Mức 2") with { InvoiceType = InvoiceKinds.Sales };
        Assert.Null(FuelPurchases.Posting(sale));
    }

    [Theory]
    [InlineData("KKKNT", 0, false)]
    [InlineData("KCT", 0, false)]
    [InlineData("KKKNT", 1_000, true)]
    public void PurchasesAcceptFuelInvoicesWithoutVat(string rate, int tax, bool blocked)
    {
        var line = InvoiceBuilder.LineRaw(0, "Dầu Điêzen 0,05S Mức 2", "lít", 61.647m, 35_690m, 2_200_200m, rate, tax, 2_200_200m + tax);
        var invoice = new InvoiceBuilder().WithBuyer(LocThuy).WithLines(line).Build() with { InvoiceType = InvoiceKinds.Purchase };

        Assert.Equal(blocked, InvoiceValidation.Validate(invoice).Any(issue => issue.Code == "TAX_RATE_UNSUPPORTED"));
    }

    // Lộc Thủy's MISA catalog against the names on its purchase e-invoices.
    [Theory]
    [InlineData("Xăng RON 95 Mức 3", "VT00003")]
    [InlineData("Xăng E10 RON 95 Mức 3", "VT0005")]
    [InlineData("Xăng RON 92 Mức 3", "XANG_01")]
    [InlineData("Dầu Điêzen 0,05S Mức 2", "VT0006")]
    [InlineData("Dầu DO 0,05S-II", "VT0006")]
    [InlineData("Dầu Điêzen 0,001S Mức 5", "VT0003")]
    [InlineData("Nhớt Caltex super", "VT00002")]
    [InlineData("Xăng RON 95 Mức 5", null)]
    [InlineData("Xăng E10 RON 95 Mức 5", null)]
    public void FuelNamesFindTheCatalogItemDespiteSpelling(string name, string? code)
    {
        var store = new ItemCatalogStore(temp.Combine("state", "item-catalog.json"));
        foreach (var (itemCode, itemName) in new[]
                 {
                     ("VT00001", "Dầu diezel 0.05%S"), ("VT00002", "Nhớt Caltex super"), ("VT00003", "Xăng Ron 95-III"),
                     ("VT0003", "Dầu Điêzen 0,001S Mức 5"), ("VT0005", "Xăng E10 RON 95 Mức 3"),
                     ("VT0006", "Dầu Điêzen 0,05S Mức 2"), ("XANG_01", "Xăng RON 92 - III")
                 })
            store.Add(new(itemCode, itemName, "Lít"));

        Assert.Equal(code, store.Find(name, "lít")?.Code);
        if (code is not null) Assert.True(ItemCatalogStore.SameItemName(name, store.FindByCode(code)!.Name));
    }

    [Fact]
    public void GradeNumeralsAreOnlyReadInFuelNames()
    {
        Assert.Equal(ItemCatalogStore.ItemNameKey("Xăng Ron 95-III"), ItemCatalogStore.ItemNameKey("XĂNG RON 95 MỨC 3"));
        Assert.NotEqual(ItemCatalogStore.ItemNameKey("Thùng nhựa 30-II"), ItemCatalogStore.ItemNameKey("Thùng nhựa 30 Mức 2"));
        Assert.NotEqual(ItemCatalogStore.ItemNameKey("Xăng RON 95 Mức 5"), ItemCatalogStore.ItemNameKey("Xăng RON 95 Mức 3"));
    }
}
