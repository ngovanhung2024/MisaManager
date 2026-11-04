using MisaManager.Core;

namespace MisaManager.Integration.Tests;

/// <summary>"Mua hàng" runs: purchase invoices become "Mua hàng hóa, dịch vụ" vouchers in the fake's purchase module.</summary>
public sealed class PurchaseVoucherTests : UiTest
{
    private static readonly FakeScenario Purchase = new(Purchase: true);

    private static string[] Events(FakeMisa misa)
        => File.ReadAllLines(Path.Combine(misa.Root, "fake-events.log"));

    [Fact]
    public Task PurchaseInvoiceIsSavedWithSupplierFoundByMstAndFixedStockAccounts() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2000", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 38, 16_000, "8%"));
        Assert.True(invoice.IsPurchase());

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal(WorkflowStatus.Saved, result.Context.Snapshot.Status);
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.StartsWith("NK0", voucher.VoucherNumber);
        Assert.Equal(("2000", "NCC_PBH", Invoices.Supplier.Name, Invoices.Supplier.TaxCode),
            (voucher.InvoiceNumber, voucher.CustomerCode, voucher.CustomerName, voucher.CustomerTaxCode));
        Assert.Equal(("04/10/2026", "04/10/2026", "04/10/2026"), (voucher.PostedDate, voucher.VoucherDate, voucher.IssuedDate));
        Assert.Equal(InvoiceKinds.PurchasePayment, voucher.Payment);
        Assert.Equal(["HH_06", "HH_07"], voucher.Lines.Select(line => line.Code));
        Assert.All(voucher.Lines, line => Assert.Equal(("KHH", "1561", "331"), (line.Stock, line.DebitAccount, line.CreditAccount)));
        Assert.Equal(["5%", "8%"], voucher.Lines.Select(line => line.TaxRate));
        Assert.Equal((invoice.Subtotal, invoice.TaxTotal, invoice.GrandTotal), (voucher.Subtotal, voucher.TaxTotal, voucher.GrandTotal));

        var events = Events(misa);
        // The supplier is looked up by the MST typed into "Nhà cung cấp".
        Assert.Contains(events, line => line.Contains("field customer <- '" + Invoices.Supplier.TaxCode + "'"));
        Assert.Contains(events, line => line.Contains("customer NCC_PBH"));
        // MISA already proposed KHH/1561/331 and no series: the tool types neither.
        Assert.DoesNotContain(events, line => line.Contains("field series <-"));
        Assert.DoesNotContain(events, line => line.Contains(" stock <-") || line.Contains(" debit_account <-") || line.Contains(" credit_account <-"));
    }, Purchase);

    [Fact]
    public Task PurchaseLineTaxDifferingFromMisaIsTypedOnTheTaxTab() => WithFakeAsync(async (misa, harness, token) =>
    {
        // 10.010 × 5% = 500,5: MISA rounds to 501 while the invoice states 500.
        var line = Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 1, 10_010, "5%") with
        {
            TaxAmount = 500, TotalWithTax = 10_510
        };
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2001", Invoices.Supplier, line);

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal(500m, Assert.Single(voucher.Lines).TaxAmount);
        Assert.Equal((10_010m, 500m, 10_510m), (voucher.Subtotal, voucher.TaxTotal, voucher.GrandTotal));
        Assert.Contains(Events(misa), entry => entry.Contains("tax_amount <- '500'"));
    }, Purchase);

    [Fact]
    public Task SupplierMstUnknownToMisaStopsBeforeAnythingIsSaved() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2002", Invoices.UnknownSupplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.NotEqual("saved", result.Outcome);
        Assert.Empty(misa.State().Vouchers);
        Assert.Contains(Events(misa), line => line.Contains("field customer <- '" + Invoices.UnknownSupplier.TaxCode + "'"));
    }, Purchase);
}
