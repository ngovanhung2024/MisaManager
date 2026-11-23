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
        var scenario = new FakeScenario(Purchase: true, SaveClosesEditor: false, ExistingVouchers: [existing],
            PurchaseListCellsUseCustomProvider: true);
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
            var eventsPath = Path.Combine(misa.Root, "fake-events.log");
            var events = File.Exists(eventsPath) ? File.ReadAllText(eventsPath) : "";
            Assert.DoesNotContain("Bỏ ghi chứng từ đã có", events, StringComparison.Ordinal);
        }, scenario);
    }

    [Fact]
    public Task StaleInvoiceFilterStopsBeforeOpeningAnotherVoucher()
    {
        var existing = new FakeVoucher("MH00429", "12671", "C26MLV", "NCC_PBH", Invoices.Supplier.Name,
            Invoices.Supplier.TaxCode, "13/11/2026", "13/11/2026", "13/11/2026", InvoiceKinds.PurchasePayment,
            [new FakeLine("VT0005", "Xăng E10 RON 95 Múc 3", "Lít", "632", "", 24.95m, 20_040, 500_000, "KCT", 0,
                CostItem: "")], 500_000, 0, 500_000, "expense");
        var scenario = new FakeScenario(Purchase: true, ExistingVouchers: [existing], InitialInvoiceFilter: "12671",
            IgnoreInvoiceFilterCommit: true, PurchaseListCellsUseCustomProvider: true);
        return WithFakeAsync(async (misa, harness, token) =>
        {
            var rule = new FuelPurchaseCompany(Invoices.LocThuy.TaxCode, Invoices.LocThuy.Name, "KHH", "1561", "6421");
            var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "205", Invoices.Supplier, Invoices.LocThuy,
                Invoices.Line(0, "VT0005", "Xăng E10 RON 95 Múc 3", "Lít", 24.95m, 20_040, "KCT"));
            var updater = new PurchaseCostUpdater(new MisaWindowDiscovery(), harness.Input, harness.PurchaseProfile);

            var result = await updater.UpdateAsync([invoice], rule, token);

            Assert.Equal((1, 0), (result.Requested, result.Updated));
            Assert.Contains("205", Assert.Single(result.Failed));
            var eventsPath = Path.Combine(misa.Root, "fake-events.log");
            var events = File.Exists(eventsPath) ? File.ReadAllText(eventsPath) : "";
            Assert.DoesNotContain("Sửa chứng từ đã có", events, StringComparison.Ordinal);
            Assert.DoesNotContain("open existing voucher", events, StringComparison.Ordinal);
        }, scenario);
    }

    [Fact]
    public Task ExistingGasolineVoucherPostsAfterYesWarning()
    {
        var existing = new FakeVoucher("MH00429", "15420", "C26MLV", "NCC_PBH", Invoices.Supplier.Name,
            Invoices.Supplier.TaxCode, "13/11/2026", "13/11/2026", "13/11/2026", InvoiceKinds.PurchasePayment,
            [new FakeLine("VT0005", "Xăng E10 RON 95 Múc 3", "Lít", "154", "", 24.95m, 20_040, 500_000, "KCT", 0,
                CostItem: "")], 500_000, 0, 500_000, "expense");
        var scenario = new FakeScenario(Purchase: true, SaveClosesEditor: false, ExistingVouchers: [existing],
            PurchaseListCellsUseCustomProvider: true, PurchaseEditUsesCustomProvider: true,
            PurchaseEditInitiallyDisabled: true);
        return WithFakeAsync(async (misa, harness, token) =>
        {
            var rule = new FuelPurchaseCompany(Invoices.LocThuy.TaxCode, Invoices.LocThuy.Name, "KHH", "1561", "6421");
            var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "15420", Invoices.Supplier, Invoices.LocThuy,
                Invoices.Line(0, "VT0005", "Xăng E10 RON 95 Múc 3", "Lít", 24.95m, 20_040, "KCT"));
            var updater = new PurchaseCostUpdater(new MisaWindowDiscovery(), harness.Input, harness.PurchaseProfile);

            var result = await updater.UpdateAsync([invoice], rule, token);

            Assert.Equal((1, 1), (result.Requested, result.Updated));
            Assert.Empty(result.Failed);
            Assert.Equal("CPBH.XANG", Assert.Single(misa.State().Vouchers).Lines[0].CostItem);
            var eventsPath = Path.Combine(misa.Root, "fake-events.log");
            var events = File.Exists(eventsPath) ? File.ReadAllText(eventsPath) : "";
            Assert.Contains("Bỏ ghi chứng từ đã có", events, StringComparison.Ordinal);
            Assert.Contains("Sửa chứng từ đã có", events, StringComparison.Ordinal);
            Assert.Contains("prompt: TK 154 thiếu Khoản mục chi phí", events, StringComparison.Ordinal);
            Assert.Contains("prompt: Khoản mục chi phí → Yes", events, StringComparison.Ordinal);
        }, scenario);
    }

    [Fact]
    public Task MissingOpenedVoucherStopsBeforeEditCommand()
    {
        var existing = new FakeVoucher("MH00429", "205", "C26MLV", "NCC_PBH", Invoices.Supplier.Name,
            Invoices.Supplier.TaxCode, "02/01/2026", "02/01/2026", "02/01/2026", InvoiceKinds.PurchasePayment,
            [new FakeLine("VT0005", "Xăng E10 RON 95 Múc 3", "Lít", "632", "", 17.49m, 17_738.708m, 310_250, "8%", 24_820,
                CostItem: "")], 310_250, 24_820, 335_070, "expense");
        var scenario = new FakeScenario(Purchase: true, ExistingVouchers: [existing],
            PurchaseListCellsUseCustomProvider: true, PurchaseRowOpenIgnored: true);
        return WithFakeAsync(async (misa, harness, token) =>
        {
            var rule = new FuelPurchaseCompany(Invoices.LocThuy.TaxCode, Invoices.LocThuy.Name, "KHH", "1561", "6421");
            var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "205", Invoices.Supplier, Invoices.LocThuy,
                Invoices.Line(0, "VT0005", "Xăng E10 RON 95 Múc 3", "Lít", 17.49m, 17_738.708m, "8%"));
            var updater = new PurchaseCostUpdater(new MisaWindowDiscovery(), harness.Input, harness.PurchaseProfile);

            var result = await updater.UpdateAsync([invoice], rule, token);

            Assert.Equal((1, 0), (result.Requested, result.Updated));
            Assert.Contains("SCREEN_NOT_OPEN", Assert.Single(result.Failed));
            var eventsPath = Path.Combine(misa.Root, "fake-events.log");
            var events = File.Exists(eventsPath) ? File.ReadAllText(eventsPath) : "";
            Assert.DoesNotContain("Sửa chứng từ đã có", events, StringComparison.Ordinal);
        }, scenario);
    }
}
