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
    public void SameNameSameUnitHoldsTheInvoice()
    {
        var invoice = new InvoiceBuilder().WithLines(InvoiceBuilder.Line(0, "PHÂN KALI", "Kg", 50, 10_000m),
            InvoiceBuilder.Line(1, "Hesman 60WG 18g", "Gói", 2, 16_000m)).Build();

        var issue = Assert.Single(CatalogHold.Issues(invoice, Catalog(Duplicated)));

        Assert.Equal(CatalogHold.Code, issue.Code);
        Assert.Equal(IssueSeverity.Blocking, issue.Severity);
        Assert.Contains("HH_03, HH_50", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NearDuplicateNamesHoldTheInvoiceWhicheverSpellingIsUsed()
    {
        var catalog = Catalog(Duplicated);
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
}
