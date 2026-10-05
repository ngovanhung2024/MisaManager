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

    // Export with the "Đơn vị chuyển đổi" column: CM GOLD is registered in Tấn only.
    private static readonly ItemCatalogEntry[] TonCatalog =
    [
        new("CM GOLD 20.10.10", "Phân NPK Cà Mau Gold 20-10-10-25kg", "Tấn"),
        new("HH_08", "Phân Ure Đạm Phú Mỹ loại 1 (bao)", "kg", "Bao")
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

    [Fact]
    public Task CustomerWithoutCodeIsFoundInTheCustomerListByNormalisedName() => WithFakeAsync(async (misa, harness, token) =>
    {
        // The e-invoice abbreviates the legal form; the customer list has MISA's full name.
        var buyer = new Party("Cty CP Dệt May  Huế", "3300100628", "", "", ValuePresence.Provided, PartyKind.Organization);
        var invoice = Invoices.Build(TempDirectory(misa), "1009", buyer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal(("DMH", "CÔNG TY CỔ PHẦN DỆT MAY HUẾ", "3300100628"), (voucher.CustomerCode, voucher.CustomerName, voucher.CustomerTaxCode));
    }, customers: [new("DMH", "CÔNG TY CỔ PHẦN DỆT MAY HUẾ", "3300100628"), new("HPL", "TRUNG TÂM DỊCH VỤ NÔNG NGHIỆP HUYỆN PHÚ LỘC", "3301638391")]);

    [Fact]
    public Task CustomerWithoutAnyKnownCodeStopsBeforeTheVoucherIsOpened() => WithFakeAsync(async (misa, harness, token) =>
    {
        var buyer = new Party("CÔNG TY TNHH CHƯA CÓ MÃ", "3300999999", "", "", ValuePresence.Provided, PartyKind.Organization);
        var invoice = Invoices.Build(TempDirectory(misa), "1010", buyer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 1, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "failed", Describe(result));
        Assert.Equal("CUSTOMER_CODE_MISSING", result.Context.Snapshot.LastErrorCode);
        Assert.True(result.MisaClean, Describe(result));
        Assert.Empty(misa.State().Vouchers);
    }, customers: [new("DMH", "CÔNG TY CỔ PHẦN DỆT MAY HUẾ", "3300100628")]);

    [Fact]
    public Task CompanyMstWithoutAProfileControlIsProvenByTheCustomerList() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1011", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var tax = Assert.Single(result.Verification.Comparison!.Values, value => value.Field == "customer_tax");
        Assert.True(tax.Matched);
        Assert.StartsWith("PartyCatalog:A&V", tax.Source, StringComparison.Ordinal);
        Assert.Equal("0107286506", Assert.Single(misa.State().Vouchers).CustomerTaxCode);
    }, new FakeScenario(CustomerTaxUnlisted: true),
        customers: [new("A&V", "CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V", "0107286506")]);

    [Fact]
    public Task CompanyMstDifferentInTheCustomerListIsNotSaved() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1012", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome is "discarded" or "failed", Describe(result));
        Assert.Empty(misa.State().Vouchers);
    }, new FakeScenario(CustomerTaxUnlisted: true),
        customers: [new("A&V", "CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V", "0100000000")]);

    [Fact]
    public Task UnfinishedRunClosedByTheUserNoLongerBlocksTheNextInvoice() => WithFakeAsync(async (misa, harness, token) =>
    {
        var first = Invoices.Build(TempDirectory(misa), "1013", Invoices.Consumer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));
        var second = Invoices.Build(TempDirectory(misa), "1014", Invoices.Consumer,
            Invoices.Line(0, "HH_07", "Hesman 60WG 18g", "Gói", 1, 16_000, "8%"));
        var entered = await harness.EnterWithoutSavingAsync(first, misa, token);
        Assert.Equal(WorkflowStatus.AwaitingSave, entered.Context.Snapshot.Status);

        // Still open in MISA: nothing can prove its outcome, so the next invoice must not start.
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(second, misa, token));
        Assert.Contains("1013", blocked.Message, StringComparison.Ordinal);

        await misa.CloseVoucherAsUserAsync(token);
        var result = await harness.RunAsync(second, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal(["1014"], misa.State().Vouchers.Select(voucher => voucher.InvoiceNumber));
        var history = await harness.Runner.ReadHistoryAsync(token);
        Assert.Equal(WorkflowStatus.Discarded, history.Single(run => run.Invoice.Identity.Number == "1013").Snapshot.Status);
    });

    [Fact]
    public Task KgLineOfATonOnlyItemIsConvertedWithoutOpeningTheUnitList() => WithFakeAsync(async (misa, harness, token) =>
    {
        // The fake's unit list does contain "kg": a saved Tấn line proves the list was never opened.
        var invoice = Invoices.Build(TempDirectory(misa), "1030", Invoices.AV,
            Invoices.Line(0, "", "Phân NPK Cà Mau Gold 20-10-10-25kg", "kg", 250, 14_400, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var line = Assert.Single(Assert.Single(misa.State().Vouchers).Lines);
        Assert.Equal(("CM GOLD 20.10.10", "Tấn", 0.25m, 3_600_000m), (line.Code, line.Unit, line.Quantity, line.Amount));
        Assert.StartsWith("ItemCatalog:CM GOLD 20.10.10:", result.Context.NativeValues!["unit_absence:" + invoice.Lines[0].LineId]);
    }, catalog: TonCatalog);
}
