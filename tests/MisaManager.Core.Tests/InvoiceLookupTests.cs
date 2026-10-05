using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class InvoiceLookupTests
{
    private static InvoiceDocument Invoice(string number, string series = "1C26MLV")
    {
        var invoice = new InvoiceBuilder().WithNumber(number).Build();
        return invoice with { Identity = invoice.Identity with { Series = series } };
    }

    [Fact]
    public void TheInvoiceWithMisaNumberIsChosenNotTheSelectedOne()
    {
        var selected = Invoice("1650");
        var open = Invoice("1655");

        Assert.Same(open, InvoiceLookup.ByNumber([selected, open], " 1655 ", ""));
    }

    [Fact]
    public void LeadingZerosDoNotMatter()
        => Assert.NotNull(InvoiceLookup.ByNumber([Invoice("1655")], "0001655", null));

    [Fact]
    public void NoInvoiceWithThatNumberGivesNull()
    {
        Assert.Null(InvoiceLookup.ByNumber([Invoice("1650")], "1655", "1C26MLV"));
        Assert.Null(InvoiceLookup.ByNumber([Invoice("1650")], "", ""));
    }

    [Fact]
    public void SeriesDecidesBetweenInvoicesWithTheSameNumber()
    {
        var first = Invoice("12", "1C26AAA");
        var second = Invoice("12", "1C26BBB");

        Assert.Same(second, InvoiceLookup.ByNumber([first, second], "12", "1c26bbb"));
        Assert.Null(InvoiceLookup.ByNumber([first, second], "12", ""));
    }
}
