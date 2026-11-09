using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class CatalogHoldTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private ItemCatalogStore Catalog(params ItemCatalogEntry[] entries)
    {
        var store = new ItemCatalogStore(temp.Combine("state", "item-catalog.json"));
        store.Merge(entries, temp.Combine("export.xlsx"));
        return store;
    }

    private static readonly ItemCatalogEntry[] Duplicated =
    [
        new("HH_03", "Phân kali", "kg"), new("HH_50", "Phân kali", "kg"),
        new("HH_120", "FUJI ONE 40EC 100CC PET", "Chai"), new("HH_203", "FUJI ONE 40EC-100CC PET", "Chai"),
        new("HH_16", "Hesman 60WG 18g", "Gói")
    ];

    [Fact]
    public void SameNameSameUnitUsesTheSmallestCodeAndHoldsOnlyWhenSeveralAreChosen()
    {
        var invoice = new InvoiceBuilder().WithLines(InvoiceBuilder.Line(0, "PHÂN KALI", "Kg", 50, 10_000m),
            InvoiceBuilder.Line(1, "Hesman 60WG 18g", "Gói", 2, 16_000m)).Build();
        var catalog = Catalog(Duplicated);

        Assert.Empty(CatalogHold.Issues(invoice, catalog));
        Assert.Equal("HH_03", catalog.Find("PHÂN KALI", "Kg")?.Code);

        catalog.SetPreferred(["HH_50", "HH_03"]);
        var issue = Assert.Single(CatalogHold.Issues(invoice, catalog));

        Assert.Equal(CatalogHold.Code, issue.Code);
        Assert.Equal(IssueSeverity.Blocking, issue.Severity);
        Assert.Contains("HH_03, HH_50", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NearDuplicateNamesHoldTheInvoiceWhicheverSpellingIsUsedWhenBothAreChosen()
    {
        var catalog = Catalog(Duplicated);
        catalog.SetPreferred(["HH_120", "HH_203"]);
        foreach (var name in new[] { "FUJI ONE 40EC-100CC PET", "FUJI ONE 40EC 100CC PET" })
        {
            var invoice = new InvoiceBuilder().WithLines(InvoiceBuilder.Line(0, name, "Chai", 2, 50_000m)).Build();
            var issue = Assert.Single(CatalogHold.Issues(invoice, catalog));
            Assert.Contains("HH_120 'FUJI ONE 40EC 100CC PET', HH_203 'FUJI ONE 40EC-100CC PET'", issue.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UniqueNamesOtherUnitsAndExplicitCodesAreNotHeld()
    {
        var catalog = Catalog(Duplicated);
        var line = InvoiceBuilder.Line(0, "Phân kali", "kg", 50, 10_000m);
        InvoiceDocument[] free =
        [
            new InvoiceBuilder().WithLines(InvoiceBuilder.Line(0, "Hesman 60WG 18g", "Gói", 2, 16_000m)).Build(),
            new InvoiceBuilder().WithLines(InvoiceBuilder.Line(0, "Phân kali", "Bao", 1, 500_000m)).Build(),
            new InvoiceBuilder().WithLines(line with { ItemCode = "HH_03" }).Build()
        ];

        Assert.All(free, invoice => Assert.Empty(CatalogHold.Issues(invoice, catalog)));
        Assert.Empty(CatalogHold.Issues(new InvoiceBuilder().WithLines(line).Build(), Catalog()));
    }
    [Fact]
    public void ChosenCodeSettlesTheDuplicateAndSurvivesReload()
    {
        var path = temp.Combine("state", "item-catalog.json");
        var catalog = Catalog(Duplicated);
        catalog.SetPreferred(["HH_03", "hh_203"]);
        InvoiceDocument Invoice(string name, string unit) => new InvoiceBuilder().WithLines(InvoiceBuilder.Line(0, name, unit, 1, 10_000m)).Build();

        Assert.Empty(CatalogHold.Issues(Invoice("PHÂN KALI", "Kg"), catalog));
        Assert.Empty(CatalogHold.Issues(Invoice("FUJI ONE 40EC 100CC PET", "Chai"), catalog));
        Assert.Equal("HH_03", catalog.Find("Phân kali", "kg")?.Code);
        Assert.Equal("HH_203", catalog.Find("FUJI ONE 40EC 100CC PET", "Chai")?.Code);
        Assert.DoesNotContain(CatalogAudit.Items(Duplicated, new ItemGroupStore(temp.Combine("state", "groups.json")), catalog.PreferredCodes()),
            finding => finding.Contains("trùng", StringComparison.Ordinal));

        // A fresh catalog export replaces the rows but keeps the user's choice.
        var reloaded = new ItemCatalogStore(path);
        reloaded.Import(Duplicated, temp.Combine("export2.xlsx"));
        Assert.Equal("HH_03", reloaded.Find("phân kali", "kg")?.Code);
        Assert.Equal("HH_203", new ItemCatalogStore(path).Find("FUJI ONE 40EC-100CC PET", "Chai")?.Code);
    }

    [Fact]
    public void TwoChosenCodesForOneNameStayAmbiguous()
    {
        var catalog = Catalog(Duplicated);
        catalog.SetPreferred(["HH_03", "HH_50"]);

        Assert.Single(CatalogHold.Issues(new InvoiceBuilder().WithLines(InvoiceBuilder.Line(0, "Phân kali", "kg", 1, 10_000m)).Build(), catalog));
        Assert.Equal("ITEM_AMBIGUOUS", Assert.Throws<AutomationException>(() => catalog.Find("Phân kali", "kg")).Code);
    }
}
