using MisaManager.Core;

namespace MisaManager.Integration.Tests;

/// <summary>End-to-end "Thêm vào MISA" runs: real WorkflowRunner + NativeWorkflowGateway against the fake R20.</summary>
public sealed class SalesVoucherTests : UiTest
{
    private static readonly ItemCatalogEntry[] Catalog =
    [
        new("HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai"),
        new("HH_07", "Hesman 60WG 18g", "Gói"),
        new("HH_08", "Phân Ure Đạm Phú Mỹ loại 1 (bao)", "kg")
    ];

    [Fact]
    public Task SingleLineWithItemCodeIsSavedExactly() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1000", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal(WorkflowStatus.Saved, result.Context.Snapshot.Status);
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal(("1000", "1C26MLV", "A&V", "0107286506"), (voucher.InvoiceNumber, voucher.Series, voucher.CustomerCode, voucher.CustomerTaxCode));
        Assert.Equal(("04/10/2026", "04/10/2026", "04/10/2026"), (voucher.PostedDate, voucher.VoucherDate, voucher.IssuedDate));
        var line = Assert.Single(voucher.Lines);
        Assert.Equal(("HH_06", "Chai", 22m, 10_000m, 220_000m, "5%", 11_000m),
            (line.Code, line.Unit, line.Quantity, line.UnitPrice, line.Amount, line.TaxRate, line.TaxAmount));
        Assert.Equal((220_000m, 11_000m, 231_000m), (voucher.Subtotal, voucher.TaxTotal, voucher.GrandTotal));
    });

    [Fact]
    public Task MultiLineInvoiceWithDifferentUnitsAndRatesIsSaved() => WithFakeAsync(async (misa, harness, token) =>
    {
        // HH_08 is 5% in the catalog: the source 10% must be typed on the "2. Thuế" tab.
        var invoice = Invoices.Build(TempDirectory(misa), "1001", Invoices.Consumer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 38, 16_000, "8%"),
            Invoices.Line(2, "HH_08", "Phân Ure Đạm Phú Mỹ loại 1 (bao)", "kg", 100, 15_429, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal(["HH_06", "HH_07", "HH_08"], voucher.Lines.Select(line => line.Code));
        Assert.Equal(["5%", "8%", "10%"], voucher.Lines.Select(line => line.TaxRate));
        Assert.Equal(invoice.Subtotal, voucher.Subtotal);
        Assert.Equal(invoice.TaxTotal, voucher.TaxTotal);
        Assert.Equal("NTD", voucher.CustomerCode);
    });

    [Fact]
    public Task LineWithoutCodeIsResolvedFromTheItemCatalog() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1002", Invoices.AV,
            Invoices.Line(0, "", "Hesman 60WG 18g", "Gói", 5, 16_000, "8%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal("HH_07", Assert.Single(Assert.Single(misa.State().Vouchers).Lines).Code);
    }, catalog: Catalog);

    [Fact]
    public Task UnknownItemIsCreatedThroughTheF9DialogThenSaved() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1003", Invoices.AV,
            Invoices.Line(0, "", "Phân bón lá Siêu Kali 1kg", "Gói", 4, 45_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var state = misa.State();
        var created = Assert.Single(state.CreatedItems);
        Assert.Equal(("HH_11", "Phân bón lá Siêu Kali 1kg", "Gói", "10%"), (created.Code, created.Name, created.Unit, created.TaxRate));
        Assert.Equal("HH_11", Assert.Single(Assert.Single(state.Vouchers).Lines).Code);
        Assert.NotNull(harness.Catalog.Find("Phân bón lá Siêu Kali 1kg", "Gói"));
    }, catalog: Catalog);

    [Fact]
    public Task ExistingInvoiceNumberIsAnsweredNoAndNothingIsSaved() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1000", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 1, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "existing", Describe(result));
        Assert.Equal(WorkflowRunner.ExistingInvoiceCode, result.Context.Snapshot.LastErrorCode);
        Assert.Empty(misa.State().Vouchers);
    }, new FakeScenario(ExistingVouchers:
    [
        new("PT02000", "1000", "1C26MLV", "KL", "Khách lẻ", "", "01/10/2026", "01/10/2026", "01/10/2026", "Tiền mặt", [], 10_000, 500, 10_500)
    ]));

    [Fact]
    public Task MisaTaxDifferentFromSourceIsDiscardedAndNothingIsSaved() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1004", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome is "failed" or "discarded", Describe(result));
        Assert.True(result.MisaClean, "Chứng từ lệch phải được Bỏ (Đóng → Không cất). " + Describe(result));
        Assert.Empty(misa.State().Vouchers);
    }, new FakeScenario(TaxOffset: 1));

    [Fact]
    public Task SaveThatKeepsTheEditorReadOnlyIsConfirmed() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1005", Invoices.AV,
            Invoices.Line(0, "HH_07", "Hesman 60WG 18g", "Gói", 3, 16_000, "8%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Single(misa.State().Vouchers);
    }, new FakeScenario(SaveClosesEditor: false));

    [Fact]
    public Task TaxRoundingPromptIsAnsweredNoAndSourceTaxIsKept() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1006", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 38, 16_000, "8%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal(invoice.TaxTotal, Assert.Single(misa.State().Vouchers).TaxTotal);
    }, new FakeScenario(TaxRoundingPrompt: true));

    [Fact]
    public Task TwoInvoicesInOneSessionAreSavedAsSeparateVouchers() => WithFakeAsync(async (misa, harness, token) =>
    {
        var first = Invoices.Build(TempDirectory(misa), "1007", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));
        var second = Invoices.Build(TempDirectory(misa), "1008", Invoices.Consumer,
            Invoices.Line(0, "HH_07", "Hesman 60WG 18g", "Gói", 1, 16_000, "8%"));

        var one = await harness.RunAsync(first, misa, token);
        var two = await harness.RunAsync(second, misa, token);

        Assert.True(one.Outcome == "saved", Describe(one));
        Assert.True(two.Outcome == "saved", Describe(two));
        var vouchers = misa.State().Vouchers;
        Assert.Equal(["1007", "1008"], vouchers.Select(voucher => voucher.InvoiceNumber));
        Assert.NotEqual(vouchers[0].VoucherNumber, vouchers[1].VoucherNumber);
    });
}
