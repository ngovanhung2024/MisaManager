using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class CompanyProfileTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static readonly Party LocThuy = new("CÔNG TY TNHH THƯƠNG MẠI VÀ DỊCH VỤ LỘC THỦY", "3301661802", "Huế", "", ValuePresence.Provided);
    private static readonly Party LaViet = new("CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT", "3301999999", "Huế", "", ValuePresence.Provided);

    private static InvoiceDocument Purchase(Party buyer)
        => new InvoiceBuilder().WithBuyer(buyer).Build() with { InvoiceType = InvoiceKinds.Purchase };

    [Fact]
    public void LaVietAndLocThuyAreSeparateCompaniesEachStartingFromTheEarlierData()
    {
        var root = temp.Combine("MisaManager");
        Directory.CreateDirectory(Path.Combine(root, "import-purchase"));
        File.WriteAllText(Path.Combine(root, "item-catalog.json"), "items");
        File.WriteAllText(Path.Combine(root, "import-purchase", "session.json"), "session");
        File.WriteAllText(Path.Combine(root, "mode.txt"), "Purchase");

        var store = new CompanyProfileStore(root);

        Assert.Equal(["Lá Việt", "Lộc Thủy"], store.Companies.Select(company => company.Name));
        // Purchase mode was last used for Lộc Thủy's fuel invoices.
        Assert.Equal(CompanyProfileStore.LocThuyId, store.Active.Id);
        var locThuy = store.ActiveFolder;
        var laViet = store.DataFolder(store.Companies[0]);
        Assert.NotEqual(locThuy, laViet);
        foreach (var folder in new[] { locThuy, laViet })
        {
            Assert.Equal("items", File.ReadAllText(Path.Combine(folder, "item-catalog.json")));
            Assert.Equal("session", File.ReadAllText(Path.Combine(folder, "import-purchase", "session.json")));
        }
        // Loading a catalog for one company leaves the other's alone.
        File.WriteAllText(Path.Combine(laViet, "item-catalog.json"), "la viet items");
        Assert.Equal("items", File.ReadAllText(Path.Combine(locThuy, "item-catalog.json")));
        Assert.Equal("items", File.ReadAllText(Path.Combine(root, "item-catalog.json")));

        store.Select(CompanyProfileStore.LaVietId);
        var reopened = new CompanyProfileStore(root);
        Assert.Equal(CompanyProfileStore.LaVietId, reopened.Active.Id);
        Assert.Equal("la viet items", File.ReadAllText(Path.Combine(reopened.ActiveFolder, "item-catalog.json")));
    }

    [Fact]
    public void AddedCompanyStartsEmptyAndKeepsItsOwnSettings()
    {
        var root = temp.Combine("MisaManager");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "item-catalog.json"), "items");
        var store = new CompanyProfileStore(root);
        Assert.Equal(CompanyProfileStore.LaVietId, store.Active.Id);

        var added = store.Upsert(new CompanyProfile("", "Công ty Mới", "3300111222"));
        store.Upsert(store.Companies[0] with { TaxCode = "3301999999" });

        Assert.Equal("cong-ty-moi", added.Id);
        Assert.False(File.Exists(Path.Combine(store.DataFolder(added), "item-catalog.json")));
        var reopened = new CompanyProfileStore(root);
        Assert.Equal(["Lá Việt", "Lộc Thủy", "Công ty Mới"], reopened.Companies.Select(company => company.Name));
        Assert.Equal("3301999999", reopened.Companies[0].TaxCode);
        Assert.Equal(new FuelPurchaseRule("CTY", "152", "154"), reopened.Companies[1].Fuel);
        Assert.Throws<ArgumentException>(() => store.Upsert(new CompanyProfile("", "Trùng", "3301661802")));
        Assert.Throws<ArgumentException>(() => store.Upsert(new CompanyProfile("", "Xăng", "", new FuelPurchaseRule("CTY", "152", "154"))));
    }

    [Fact]
    public void InvoiceOfAnotherCompanyIsRefused()
    {
        var companies = CompanyProfileStore.Defaults.Select(company =>
            company.Id == CompanyProfileStore.LaVietId ? company with { TaxCode = "" } : company).ToArray();
        var laViet = companies[0];
        var locThuy = companies[1];

        // Lộc Thủy's fuel invoice under Lá Việt (no MST yet) would skip the fuel rule.
        var message = CompanyProfileStore.Mismatch(Purchase(LocThuy), laViet, companies);
        Assert.Contains("Lộc Thủy", message);
        Assert.Contains("Lá Việt", message);
        Assert.Null(CompanyProfileStore.Mismatch(Purchase(LocThuy), locThuy, companies));
        Assert.Null(CompanyProfileStore.Mismatch(Purchase(LocThuy with { TaxCode = "3301661802-001" }), locThuy, companies));
        Assert.NotNull(CompanyProfileStore.Mismatch(Purchase(LaViet), locThuy, companies));
        // Unknown company and no MST on Lá Việt: nothing to compare.
        Assert.Null(CompanyProfileStore.Mismatch(Purchase(LaViet), laViet, companies));
        // Sales: the seller is the company.
        var sale = new InvoiceBuilder().Build() with { Seller = LocThuy };
        Assert.NotNull(CompanyProfileStore.Mismatch(sale, laViet, companies));
        Assert.Null(CompanyProfileStore.Mismatch(sale, locThuy, companies));
    }
}
