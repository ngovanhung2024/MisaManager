using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class ImportSessionStoreTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ImportResult Imported(params InvoiceDocument[] invoices)
        => new(invoices[0].Source, ["Sheet1"], invoices,
            [new ImportIssue("SAMPLE", IssueSeverity.Warning, "Cảnh báo mẫu", "Sheet1", "B2")], 12, 1);

    [Fact]
    public async Task ImportedInvoicesAndTicksSurviveARestart()
    {
        var first = new InvoiceBuilder().WithNumber("1650").Build();
        var second = new InvoiceBuilder().WithNumber("1655").Build();
        await new ImportSessionStore(temp.Root).SaveAsync(@"C:\data\hoadon.xlsx", "Sheet1", Imported(first, second), [second.Fingerprint], Token);

        var restored = await new ImportSessionStore(temp.Root).LoadAsync(Token);

        Assert.NotNull(restored);
        Assert.Equal((@"C:\data\hoadon.xlsx", "Sheet1", 12), (restored.FilePath, restored.Sheet, restored.Result.ReadRows));
        // Records holding immutable arrays compare by reference; the fingerprint covers the invoice content.
        Assert.Equal([first.Fingerprint, second.Fingerprint], restored.Result.Invoices.Select(invoice => invoice.Fingerprint));
        Assert.Equal(first.Lines.Length, restored.Result.Invoices[0].Lines.Length);
        Assert.Equal("SAMPLE", Assert.Single(restored.Result.Issues).Code);
        Assert.Equal([second.Fingerprint], restored.Checked);
    }

    [Fact]
    public async Task ClearForgetsTheSavedSession()
    {
        var invoice = InvoiceBuilder.Valid();
        var store = new ImportSessionStore(temp.Root);
        await store.SaveAsync("a.xlsx", "Sheet1", Imported(invoice), [invoice.Fingerprint], Token);

        store.Clear();
        store.Clear();

        Assert.Null(await new ImportSessionStore(temp.Root).LoadAsync(Token));
    }

    [Fact]
    public async Task TicksFollowLaterChangesAndDropUnknownInvoices()
    {
        var invoice = InvoiceBuilder.Valid();
        var store = new ImportSessionStore(temp.Root);
        await store.SaveAsync("a.xlsx", "Sheet1", Imported(invoice), [], Token);

        await store.SaveCheckedAsync([invoice.Fingerprint, "not-in-workbook"], Token);

        Assert.Equal([invoice.Fingerprint], (await store.LoadAsync(Token))!.Checked);
    }

    [Fact]
    public async Task NothingSavedOrUnreadableRestoresNothing()
    {
        Assert.Null(await new ImportSessionStore(temp.Root).LoadAsync(Token));
        temp.WriteText("import-session.json", "{ not json");
        Assert.Null(await new ImportSessionStore(temp.Root).LoadAsync(Token));
    }
}
