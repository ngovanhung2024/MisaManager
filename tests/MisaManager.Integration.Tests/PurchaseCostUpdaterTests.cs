using MisaManager.Automation;
using MisaManager.Core;

namespace MisaManager.Integration.Tests;

public sealed class PurchaseCostUpdaterTests : UiTest
{
    [Fact]
    public Task ExistingGasolineVoucherGetsCostItem()
    {
        var existing = new FakeVoucher("MH00429", "12730", "C26MLV", "NCC_PBH", Invoices.Supplier.Name,
            Invoices.Supplier.TaxCode, "13/11/2026", "13/11/2026", "13/11/2026", InvoiceKinds.PurchasePayment,
            [new FakeLine("VT0005", "Xăng E10 RON 95 Múc 3", "Lít", "632", "", 24.95m, 20_040, 500_000, "KCT", 0,
                CostItem: "")], 500_000, 0, 500_000, "expense");
        var scenario = new FakeScenario(Purchase: true, SaveClosesEditor: false, ExistingVouchers: [existing]);
        return WithFakeAsync(async (misa, harness, token) =>
        {
            var rule = new FuelPurchaseCompany(Invoices.LocThuy.TaxCode, Invoices.LocThuy.Name, "KHH", "1561", "6421");
            var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "12730", Invoices.Supplier, Invoices.LocThuy,
                Invoices.Line(0, "VT0005", "Xăng E10 RON 95 Múc 3", "Lít", 24.95m, 20_040, "KCT"));
            var updater = new PurchaseCostUpdater(new MisaWindowDiscovery(), harness.Input, harness.PurchaseProfile);

            var result = await updater.UpdateAsync([invoice], rule, token);

            Assert.Equal((1, 1), (result.Requested, result.Updated));
            Assert.Empty(result.Failed);
            Assert.Empty(result.Failed);
            Assert.Equal("CPBH.XANG", Assert.Single(misa.State().Vouchers).Lines[0].CostItem);
        }, scenario);
    }
}
