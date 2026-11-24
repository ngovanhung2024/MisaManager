using MisaManager.Automation;
using MisaManager.Core;

namespace MisaManager.Integration.Tests;

public sealed class PurchaseCostUpdaterTests : UiTest
{
    [Fact]
    public async Task WindowsProviderTimeoutIsReportedAsAReadFailure()
    {
        using var dispatcher = new NativeDispatcher();
        var token = TestContext.Current.CancellationToken;
        var providerError = new System.ComponentModel.Win32Exception(unchecked((int)0x800705B4));
        var error = await Assert.ThrowsAsync<AutomationException>(() => dispatcher.RunAsync<bool>(() => throw providerError, token));
        Assert.Equal("NATIVE_READ_TIMEOUT", error.Code);
        Assert.False(error.InputMayHaveBeenSent);
        Assert.Same(providerError, error.InnerException);
        Assert.True(await dispatcher.RunAsync(() => true, token));
    }

    [Fact]
    public Task PurchaseSaveWaitsWhenMisaIsBusy()
    {
        var existing = new FakeVoucher("MH00228", "14383", "C26MPD", "NCC_PBH", Invoices.Supplier.Name,
            Invoices.Supplier.TaxCode, "09/03/2026", "09/03/2026", "09/03/2026", InvoiceKinds.PurchasePayment,
            [new FakeLine("VT00003", "Xăng Ron 95-III", "Lít", "632", "", 25, 20_000, 500_000, "KCT", 0)],
            500_000, 0, 500_000, "expense");
        var scenario = new FakeScenario(Purchase: true, SaveClosesEditor: false, ExistingVouchers: [existing],
            PurchaseListCellsUseCustomProvider: true, SaveBusyMilliseconds: 4500);
        return WithFakeAsync(async (misa, harness, token) =>
        {
            var rule = new FuelPurchaseCompany(Invoices.LocThuy.TaxCode, Invoices.LocThuy.Name, "KHH", "1561", "6421");
            var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "14383", Invoices.Supplier, Invoices.LocThuy,
                Invoices.Line(0, "VT00003", "Xăng Ron 95-III", "Lít", 25, 20_000, "KCT"));
            var updater = new PurchaseCostUpdater(new MisaWindowDiscovery(), harness.Input, harness.PurchaseProfile);
            var assigned = new List<InvoiceIdentity>();
            var result = await updater.UpdateAsync([invoice], rule, token, onAssigned: saved =>
            {
                assigned.Add(saved.Identity);
                return Task.CompletedTask;
            });

            Assert.True(result.Failed.Count == 0, string.Join(" | ", result.Failed));
            Assert.Equal(1, result.Updated);
            Assert.Equal(invoice.Identity, Assert.Single(assigned));
            Assert.Equal("CPBH.XANG", Assert.Single(misa.State().Vouchers).Lines[0].CostItem);
            var events = File.ReadAllLines(Path.Combine(misa.Root, "fake-events.log"));
            Assert.Single(events, line => line.Contains("Cất (saved=", StringComparison.Ordinal));
            Assert.Single(events, line => line.Contains("Đóng (dirty=False, saved=True)", StringComparison.Ordinal));
        }, scenario);
    }

    [Fact]
    public Task EmptyPurchaseRegisterSkipsMissingInvoiceAndContinues()
    {
        var existing = new FakeVoucher("MH00429", "441", "C26MLV", "NCC_PBH", Invoices.Supplier.Name,
            Invoices.Supplier.TaxCode, "03/01/2026", "03/01/2026", "03/01/2026", InvoiceKinds.PurchasePayment,
            [new FakeLine("VT0005", "Xăng RON 95 Mức 3", "Lít", "632", "", 25, 20_000, 500_000, "KCT", 0)],
            500_000, 0, 500_000, "expense");
        var scenario = new FakeScenario(Purchase: true, ExistingVouchers: [existing], InitialInvoiceFilter: "441",
            PurchaseListCellsUseCustomProvider: true, EmptyPurchaseRegisterExposesRow: true,
            PurchaseFilterRevertsOnRefocus: true, PurchaseFilterCommitDelayMilliseconds: 300);
        return WithFakeAsync(async (misa, harness, token) =>
        {
            var rule = new FuelPurchaseCompany(Invoices.LocThuy.TaxCode, Invoices.LocThuy.Name, "KHH", "1561", "6421");
            InvoiceDocument Invoice(string number) => Invoices.BuildPurchaseFor(TempDirectory(misa), number, Invoices.Supplier,
                Invoices.LocThuy, Invoices.Line(0, "VT0005", "Xăng RON 95 Mức 3", "Lít", 25, 20_000, "KCT"));
            var missing = Invoice("120581");
            var next = Invoice("441");
            var updater = new PurchaseCostUpdater(new MisaWindowDiscovery(), harness.Input, harness.PurchaseProfile);
            var assigned = new List<InvoiceIdentity>();
            var result = await updater.UpdateAsync([missing, next], rule, token, onAssigned: invoice =>
            {
                assigned.Add(invoice.Identity);
                return Task.CompletedTask;
            });

            Assert.True(result.Failed.Count == 0, string.Join(" | ", result.Failed));
            Assert.Equal((2, 1), (result.Requested, result.Updated));
            Assert.Equal("120581", Assert.Single(result.Skipped));
            Assert.Equal(next.Identity, Assert.Single(assigned));
            var saved = Assert.Single(misa.State().Vouchers);
            Assert.Equal("441", saved.InvoiceNumber);
            Assert.Equal("CPBH.XANG", saved.Lines[0].CostItem);
        }, scenario);
    }

    [Fact]
    public Task PurchaseFilterCommitsWithoutRefocusingEditor()
    {
        var existing = new FakeVoucher("MH00429", "441", "C26MLV", "NCC_PBH", Invoices.Supplier.Name,
            Invoices.Supplier.TaxCode, "03/01/2026", "03/01/2026", "03/01/2026", InvoiceKinds.PurchasePayment,
            [new FakeLine("VT0005", "Xăng E10 RON 95 Múc 3", "Lít", "632", "", 24.95m, 20_040, 500_000, "KCT", 0,
                CostItem: "")], 500_000, 0, 500_000, "expense");
        var other = existing with { VoucherNumber = "MH00428", InvoiceNumber = "205" };
        var scenario = new FakeScenario(Purchase: true, ExistingVouchers: [other, existing], InitialInvoiceFilter: "205",
            PurchaseFilterRevertsOnRefocus: true, PurchaseFilterCommitDelayMilliseconds: 300,
            PurchaseListCellsUseCustomProvider: true, PurchaseRowBoundsLostAfterRead: true, SaveClosesEditor: false);
        return WithFakeAsync(async (misa, harness, token) =>
        {
            var rule = new FuelPurchaseCompany(Invoices.LocThuy.TaxCode, Invoices.LocThuy.Name, "KHH", "1561", "6421");
            var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "441", Invoices.Supplier, Invoices.LocThuy,
                Invoices.Line(0, "VT0005", "Xăng E10 RON 95 Múc 3", "Lít", 24.95m, 20_040, "KCT"));
            var next = Invoices.BuildPurchaseFor(TempDirectory(misa), "205", Invoices.Supplier, Invoices.LocThuy,
                Invoices.Line(0, "VT0005", "Xăng E10 RON 95 Múc 3", "Lít", 24.95m, 20_040, "KCT"));
            var updater = new PurchaseCostUpdater(new MisaWindowDiscovery(), harness.Input, harness.PurchaseProfile);

            var assigned = new List<InvoiceIdentity>();
            var result = await updater.UpdateAsync([invoice, next], rule, token, onAssigned: saved =>
            {
                assigned.Add(saved.Identity);
                return Task.CompletedTask;
            });

            Assert.True(result.Failed.Count == 0, string.Join(" | ", result.Failed));
            Assert.Equal((2, 2), (result.Requested, result.Updated));
            Assert.Equal(new[] { invoice.Identity, next.Identity }, assigned);
            var changed = misa.State().Vouchers;
            Assert.Equal(new[] { "205", "441" }, changed.Select(voucher => voucher.InvoiceNumber).Order(StringComparer.Ordinal));
            Assert.All(changed, voucher => Assert.Equal("CPBH.XANG", voucher.Lines[0].CostItem));
            var events = File.ReadAllText(Path.Combine(misa.Root, "fake-events.log"));
            Assert.Contains("register invoice cell read", events, StringComparison.Ordinal);
            var opened441 = events.IndexOf("open existing voucher 441", StringComparison.Ordinal);
            var closed441 = events.IndexOf("Đóng (dirty=False, saved=True)", StringComparison.Ordinal);
            var opened205 = events.IndexOf("open existing voucher 205", StringComparison.Ordinal);
            Assert.True(opened441 >= 0 && opened441 < closed441 && closed441 < opened205, events);
        }, scenario);
    }

    [Fact]
    public Task HiddenCostItemListAcceptsDirectText()
    {
        var existing = new FakeVoucher("MH00197", "205", "C26MPD", "NCC_PBH", Invoices.Supplier.Name,
            Invoices.Supplier.TaxCode, "02/01/2026", "02/01/2026", "02/01/2026", InvoiceKinds.PurchasePayment,
            [new FakeLine("VT0005", "Xăng RON 95 Mức 3", "Lít", "154", "", 17.49m, 17_738.708m, 310_250, "8%", 24_820)],
            310_250, 24_820, 335_070, "expense");
        var scenario = new FakeScenario(Purchase: true, ExistingVouchers: [existing],
            PurchaseListCellsUseCustomProvider: true, CostItemListDrawnAsCustomText: true);
        return WithFakeAsync(async (misa, harness, token) =>
        {
            var rule = new FuelPurchaseCompany(Invoices.LocThuy.TaxCode, Invoices.LocThuy.Name, "KHH", "1561", "6421");
            var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "205", Invoices.Supplier, Invoices.LocThuy,
                Invoices.Line(0, "VT0005", "Xăng RON 95 Mức 3", "Lít", 17.49m, 17_738.708m, "8%"));
            var updater = new PurchaseCostUpdater(new MisaWindowDiscovery(), harness.Input, harness.PurchaseProfile);
            var result = await updater.UpdateAsync([invoice], rule, token);

            Assert.True((1, 1) == (result.Requested, result.Updated), string.Join(" | ", result.Failed));
            Assert.Empty(result.Failed);
            Assert.Equal("CPBH.XANG", Assert.Single(misa.State().Vouchers).Lines[0].CostItem);
        }, scenario);
    }

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
    public Task ExistingGasolineVoucherSelectsDrawnCostItemAfterScrolling()
    {
        var existing = new FakeVoucher("MH00429", "12731", "C26MLV", "NCC_PBH", Invoices.Supplier.Name,
            Invoices.Supplier.TaxCode, "13/11/2026", "13/11/2026", "13/11/2026", InvoiceKinds.PurchasePayment,
            [new FakeLine("VT0005", "Xăng E10 RON 95 Múc 3", "Lít", "632", "", 24.95m, 20_040, 500_000, "KCT", 0,
                CostItem: "")], 500_000, 0, 500_000, "expense");
        var scenario = new FakeScenario(Purchase: true, SaveClosesEditor: false, ExistingVouchers: [existing],
            PurchaseListCellsUseCustomProvider: true, CostItemListDrawnAsCustomText: true);
        return WithFakeAsync(async (misa, harness, token) =>
        {
            var rule = new FuelPurchaseCompany(Invoices.LocThuy.TaxCode, Invoices.LocThuy.Name, "KHH", "1561", "6421");
            var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "12731", Invoices.Supplier, Invoices.LocThuy,
                Invoices.Line(0, "VT0005", "Xăng E10 RON 95 Múc 3", "Lít", 24.95m, 20_040, "KCT"));
            var updater = new PurchaseCostUpdater(new MisaWindowDiscovery(), harness.Input, harness.PurchaseProfile);

            var result = await updater.UpdateAsync([invoice], rule, token);

            Assert.Equal((1, 1), (result.Requested, result.Updated));
            Assert.Empty(result.Failed);
            Assert.Equal("CPBH.XANG", Assert.Single(misa.State().Vouchers).Lines[0].CostItem);
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
            Assert.Empty(result.Skipped);
            var eventsPath = Path.Combine(misa.Root, "fake-events.log");
            var events = File.Exists(eventsPath) ? File.ReadAllText(eventsPath) : "";
            Assert.DoesNotContain("Sửa chứng từ đã có", events, StringComparison.Ordinal);
            Assert.DoesNotContain("open existing voucher", events, StringComparison.Ordinal);
        }, scenario);
    }

    [Fact]
    public Task ExistingGasolineVoucherSavesAfterYesWarning()
    {
        var existing = new FakeVoucher("MH00429", "15420", "C26MLV", "NCC_PBH", Invoices.Supplier.Name,
            Invoices.Supplier.TaxCode, "13/11/2026", "13/11/2026", "13/11/2026", InvoiceKinds.PurchasePayment,
            [new FakeLine("VT0005", "Xăng E10 RON 95 Múc 3", "Lít", "154", "", 24.95m, 20_040, 500_000, "KCT", 0,
                CostItem: "")], 500_000, 0, 500_000, "expense");
        var scenario = new FakeScenario(Purchase: true, SaveClosesEditor: false, ExistingVouchers: [existing],
            PurchaseListCellsUseCustomProvider: true, PurchaseEditUsesCustomProvider: true,
            PurchaseEditInitiallyDisabled: true, PurchaseEditRemainsDisabledAfterSave: true,
            CloseSwallowedOnceAfterSave: true);
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
            Assert.Contains("Cất (saved=False", events, StringComparison.Ordinal);
            Assert.Contains("prompt: TK 154 thiếu Khoản mục chi phí", events, StringComparison.Ordinal);
            Assert.Contains("prompt: Khoản mục chi phí → Yes", events, StringComparison.Ordinal);
            Assert.Contains("Đóng bị nuốt", events, StringComparison.Ordinal);
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
