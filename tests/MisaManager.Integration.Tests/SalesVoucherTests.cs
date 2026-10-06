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
        Assert.DoesNotContain("F3 item lookup", File.ReadAllText(Path.Combine(misa.Root, "fake-events.log")), StringComparison.Ordinal);
    }, new FakeScenario(ItemLookupUsesVoucherColumns: true), catalog: Catalog);

    [Fact]
    public Task NewItemCodeStartsWithItsGroupCode() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Groups.Replace([new("DV", "Dịch vụ"), new("HH", "Hàng hóa"), new("NVL", "Nguyên vật liệu")], "Nhom_vat_tu.xlsx");
        var invoice = Invoices.Build(TempDirectory(misa), "1004", Invoices.AV,
            Invoices.Line(0, "", "Phí vận chuyển", "Lần", 1, 200_000, "10%"),
            Invoices.Line(1, "", "Phân bón lá Siêu Kali 1kg", "Gói", 4, 45_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var state = misa.State();
        Assert.Equal(["DV_01", "HH_11"], state.CreatedItems.Select(item => item.Code));
        Assert.Equal(["DV_01", "HH_11"], Assert.Single(state.Vouchers).Lines.Select(line => line.Code));
    }, new FakeScenario(ItemLookupUsesVoucherColumns: true), catalog: Catalog);

    [Fact]
    public Task Invoice1657UsesImportedCodeInsteadOfRetainedGeneratedMapping() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Catalog.Add(new("HH_340", "Sông Gianh HC-15 bột (25kg)", "kg"));
        var invoice = Invoices.Build(TempDirectory(misa), "1657", Invoices.Consumer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 1, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 1, 16_000, "8%"),
            Invoices.Line(2, "", "Sông Gianh HC-15 bột (25kg)", "Kg", 350, 3_047.619m, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var line = Assert.Single(misa.State().Vouchers).Lines[2];
        Assert.Equal(("HH_177", "kg", 350m), (line.Code, line.Unit, line.Quantity));
        Assert.Empty(misa.State().CreatedItems);
        var events = File.ReadAllText(Path.Combine(misa.Root, "fake-events.log"));
        Assert.DoesNotContain("code <- 'HH_340'", events, StringComparison.Ordinal);
        Assert.DoesNotContain("F3 item lookup", events, StringComparison.Ordinal);
        Assert.DoesNotContain("F9 new item", events, StringComparison.Ordinal);
    }, new FakeScenario(ItemLookupUsesVoucherColumns: true, AdditionalItems:
        [new("HH_177", "Sông Gianh HC-15 bột (bao 25kg)", "kg", "5%"),
         new("HH_340", "Hàng khác trong MISA", "kg", "5%")]),
        catalog: [new("HH_177", "Sông Gianh HC-15 bột (bao 25kg)", "Tấn", "kg")]);

    [Fact]
    public Task Invoice1665UsesImportedPunctuationVariantWithoutCacheAmbiguity() => WithFakeAsync(async (misa, harness, token) =>
    {
        const string name = "Phân NPK 16-16-8+13S HCQN (BM-BV)";
        harness.Catalog.Add(new("HH_342", name, "kg"));
        var invoice = Invoices.Build(TempDirectory(misa), "1665", Invoices.Consumer,
            Invoices.Line(0, "", name, "Kg", 150, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal("HH_94", Assert.Single(Assert.Single(misa.State().Vouchers).Lines).Code);
        Assert.Empty(misa.State().CreatedItems);
        var events = File.ReadAllText(Path.Combine(misa.Root, "fake-events.log"));
        Assert.DoesNotContain("code <- 'HH_342'", events, StringComparison.Ordinal);
        Assert.DoesNotContain("F3 item lookup", events, StringComparison.Ordinal);
        Assert.DoesNotContain("F9 new item", events, StringComparison.Ordinal);
    }, new FakeScenario(AdditionalItems: [new("HH_94", "Phân NPK16.16.8 + 13S HCQN (BM-BV)", "kg", "5%")]),
        catalog: [new("HH_94", "Phân NPK16.16.8 + 13S HCQN (BM-BV)", "kg")]);

    [Fact]
    public Task MultipleExportedCodesRemainAmbiguousBeforeEnteringItemCode() => WithFakeAsync(async (misa, harness, token) =>
    {
        const string name = "Phân NPK 16-16-8+13S HCQN (BM-BV)";
        var invoice = Invoices.Build(TempDirectory(misa), "1666", Invoices.Consumer,
            Invoices.Line(0, "", name, "Kg", 150, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.Equal("ITEM_AMBIGUOUS", result.Context.Snapshot.LastErrorCode);
        Assert.Contains("HH_94", result.Context.Snapshot.LastMessage);
        Assert.Contains("HH_342", result.Context.Snapshot.LastMessage);
        Assert.Empty(misa.State().Vouchers);
        Assert.Empty(misa.State().CreatedItems);
        var events = File.ReadAllText(Path.Combine(misa.Root, "fake-events.log"));
        Assert.DoesNotContain("code <-", events, StringComparison.Ordinal);
        Assert.DoesNotContain("F9 new item", events, StringComparison.Ordinal);
    }, catalog: [new("HH_94", "Phân NPK16.16.8 + 13S HCQN (BM-BV)", "kg"),
        new("HH_342", "Phân NPK 16-16-8+13S HCQN (BM-BV)", "kg")]);

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
        Assert.DoesNotContain("F3 item lookup", File.ReadAllText(Path.Combine(misa.Root, "fake-events.log")), StringComparison.Ordinal);
    }, new FakeScenario(ItemLookupUsesVoucherColumns: true), catalog: Catalog);

    [Fact]
    public Task Invoice1667IgnoresUnverifiedCacheAndCreatesFuArmyOnce() => WithFakeAsync(async (misa, harness, token) =>
    {
        const string name = "Fu-army 40EC 100ml (ĐN)";
        harness.Catalog.Add(new("HH_341", name, "Chai"));
        var invoice = Invoices.Build(TempDirectory(misa), "1667", Invoices.Consumer,
            Invoices.Line(0, "HH_01", "Chery 15g", "Gói", 1, 10_000, "5%"),
            Invoices.Line(1, "HH_02", "Phân NPK 16.16.8.13S QN", "kg", 1, 10_000, "5%"),
            Invoices.Line(2, "HH_03", "Phân kali", "kg", 1, 10_000, "5%"),
            Invoices.Line(3, "HH_04", "Phân ĐT Lót thúc 17.12.5", "kg", 1, 10_000, "5%"),
            Invoices.Line(4, "HH_05", "Phân NPK 16.16.8.6S", "kg", 1, 10_000, "5%"),
            Invoices.Line(5, "", name, "Chai", 19, 67_857.14m, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var item = Assert.Single(misa.State().CreatedItems);
        Assert.Equal(("HH_342", name, "Chai"), (item.Code, item.Name, item.Unit));
        Assert.Equal("HH_342", Assert.Single(misa.State().Vouchers).Lines[5].Code);
        Assert.Equal("HH_342", new ItemCatalogStore(harness.Catalog.FilePath).Find(name, "Chai")!.Code);
        var second = Invoices.Build(TempDirectory(misa), "1668", Invoices.Consumer,
            Invoices.Line(0, "", name, "Chai", 1, 67_857.14m, "5%"));
        Assert.Equal("saved", (await harness.RunAsync(second, misa, token)).Outcome);
        Assert.Single(misa.State().CreatedItems);
        var events = File.ReadAllText(Path.Combine(misa.Root, "fake-events.log"));
        Assert.DoesNotContain("code <- 'HH_341'", events, StringComparison.Ordinal);
        Assert.DoesNotContain("F3 item lookup", events, StringComparison.Ordinal);
        Assert.Equal(1, events.Split("F9 new item", StringSplitOptions.None).Length - 1);
    }, new FakeScenario(AdditionalItems: [new("HH_341", "COMPASS 60OD-100CC PET", "Chai", "5%")]), catalog: Catalog);

    [Theory]
    [InlineData("1665", 4, "HH_340", "Sông Gianh HC-15 bột (25kg)", "kg")]
    [InlineData("1667", 6, "HH_341", "Fu-army 40EC 100ml (ĐN)", "Chai")]
    public Task StaleCatalogNameStopsWithoutOpeningItemLookup(string number, int failingRow, string staleCode,
        string name, string unit) => WithFakeAsync(async (misa, harness, token) =>
    {
        var source = Enumerable.Range(0, failingRow - 1)
            .Select(index => Invoices.Line(index, "HH_" + (index + 1).ToString("00"),
                new[] { "Chery 15g", "Phân NPK 16.16.8.13S QN", "Phân kali", "Phân ĐT Lót thúc 17.12.5", "Phân NPK 16.16.8.6S" }[index],
                index == 0 ? "Gói" : "kg", 1, 10_000, "5%"))
            .Append(Invoices.Line(failingRow - 1, "", name, unit, 2, 10_000, "5%"));
        var invoice = Invoices.Build(TempDirectory(misa), number, Invoices.Consumer, source.ToArray());

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.NotEqual("saved", result.Outcome);
        Assert.Equal("ITEM_CATALOG_STALE", result.Context.Snapshot.LastErrorCode);
        Assert.Contains("NativeWorkflowGateway.EnterLines", result.Context.Snapshot.ErrorChain);
        Assert.Equal(result.Context.Snapshot.ErrorChain, result.Verification.ErrorChain);
        Assert.Empty(misa.State().CreatedItems);
        Assert.Empty(misa.State().Vouchers);
        Assert.DoesNotContain("catalog query", File.ReadAllText(Path.Combine(misa.Root, "fake-events.log")), StringComparison.Ordinal);
    }, new FakeScenario(AdditionalItems: [new(staleCode, "Hàng khác trong MISA", unit, "5%")]),
        catalog: [new(staleCode, name, unit)]);

    [Fact]
    public Task ImportedCodeAbsentFromMisaDoesNotReplayOrCreateAnItem() => WithFakeAsync(async (misa, harness, token) =>
    {
        var first = Invoices.Build(TempDirectory(misa), "1673", Invoices.Consumer,
            Invoices.Line(0, "", "Sông Gianh HC-15 bột (25kg)", "kg", 2, 10_000, "5%"));
        var one = await harness.RunAsync(first, misa, token);

        Assert.NotEqual("saved", one.Outcome);
        Assert.Empty(misa.State().CreatedItems);
        Assert.Empty(misa.State().Vouchers);
        var events = File.ReadAllText(Path.Combine(misa.Root, "fake-events.log"));
        Assert.DoesNotContain("F3 item lookup", events, StringComparison.Ordinal);
        Assert.DoesNotContain("F9 new item", events, StringComparison.Ordinal);
        Assert.Equal(1, events.Split("code <- 'HH_340'", StringSplitOptions.None).Length - 1);
        Assert.Empty(harness.Catalog.PendingSnapshot());
    }, catalog: [new("HH_340", "Sông Gianh HC-15 bột (25kg)", "kg")]);

    [Fact]
    public Task NativeCatalogNameResolutionUsesTheSamePunctuationRulesOnEveryLine() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1675", Invoices.Consumer,
            Invoices.Line(0, "", "Rồng đỏ 200SL Chai 100ml", "Chai", 2, 10_000, "5%"),
            Invoices.Line(1, "", "Hesman 60WG-18g", "Gói", 3, 16_000, "8%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Empty(misa.State().CreatedItems);
        Assert.Equal(["HH_06", "HH_07"], Assert.Single(misa.State().Vouchers).Lines.Select(line => line.Code));
    }, catalog: Catalog);

    [Fact]
    public Task StaleCatalogDoesNotUseLiveLookupOrCreateADuplicate() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1668", Invoices.Consumer,
            Invoices.Line(0, "", "Sông Gianh HC-15 bột (25kg)", "kg", 2, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.NotEqual("saved", result.Outcome);
        Assert.Equal("ITEM_CATALOG_STALE", result.Context.Snapshot.LastErrorCode);
        Assert.Empty(misa.State().CreatedItems);
        Assert.Empty(misa.State().Vouchers);
        Assert.DoesNotContain("catalog query", File.ReadAllText(Path.Combine(misa.Root, "fake-events.log")), StringComparison.Ordinal);
    }, new FakeScenario(AdditionalItems: [new("HH_340", "Hàng khác", "kg", "5%"),
        new("HH_350", "Sông Gianh HC-15 bột (25kg)", "kg", "5%")]),
        catalog: [new("HH_340", "Sông Gianh HC-15 bột (25kg)", "kg")]);

    [Fact]
    public Task ExplicitWrongCodeDoesNotCreateAReplacement() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1669", Invoices.Consumer,
            Invoices.Line(0, "HH_340", "Sông Gianh HC-15 bột (25kg)", "kg", 2, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.Equal("READBACK_MISMATCH", result.Context.Snapshot.LastErrorCode);
        Assert.Empty(misa.State().CreatedItems);
        Assert.Empty(misa.State().Vouchers);
        Assert.DoesNotContain("catalog query", File.ReadAllText(Path.Combine(misa.Root, "fake-events.log")), StringComparison.Ordinal);
    }, new FakeScenario(AdditionalItems: [new("HH_340", "Hàng khác", "kg", "5%")]), catalog: Catalog);

    [Fact]
    public Task UnresolvedItemSaveDoesNotCreateAnotherItem() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Catalog.BeginPending(new("HH_340", "Sông Gianh HC-15 bột (25kg)", "kg", DateTimeOffset.UtcNow));
        var invoice = Invoices.Build(TempDirectory(misa), "1670", Invoices.Consumer,
            Invoices.Line(0, "", "Sông Gianh HC-15 bột (25kg)", "kg", 2, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.Equal("ITEM_CREATE_UNCERTAIN", result.Context.Snapshot.LastErrorCode);
        Assert.Single(harness.Catalog.PendingSnapshot());
        Assert.Empty(misa.State().CreatedItems);
        Assert.Empty(misa.State().Vouchers);
        Assert.DoesNotContain("F9 new item", File.ReadAllText(Path.Combine(misa.Root, "fake-events.log")), StringComparison.Ordinal);
    }, new FakeScenario(AdditionalItems: [new("HH_340", "Hàng khác", "kg", "5%")]), catalog: Catalog);

    [Fact]
    public Task ImportedCatalogIsAuthoritativeWithoutLiveLookup() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1671", Invoices.Consumer,
            Invoices.Line(0, "", "Sông Gianh HC-15 bột (25kg)", "kg", 2, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.Equal("saved", result.Outcome);
        Assert.Empty(misa.State().CreatedItems);
        Assert.Equal("HH_340", Assert.Single(Assert.Single(misa.State().Vouchers).Lines).Code);
        Assert.Equal("HH_340", harness.Catalog.Find("Sông Gianh HC-15 bột (25kg)", "kg")!.Code);
        Assert.DoesNotContain("F9 new item", File.ReadAllText(Path.Combine(misa.Root, "fake-events.log")), StringComparison.Ordinal);
        Assert.DoesNotContain("F3 item lookup", File.ReadAllText(Path.Combine(misa.Root, "fake-events.log")), StringComparison.Ordinal);
    }, new FakeScenario(IncompleteItemLookup: true, ItemLookupUsesVoucherColumns: true,
        AdditionalItems: [new("HH_340", "Sông Gianh HC-15 bột (25kg)", "kg", "5%")]),
        catalog: [new("HH_340", "Sông Gianh HC-15 bột (25kg)", "kg")]);

    [Fact]
    public Task CreatedItemIsNotConfirmedUntilTheSavedRowMatches() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1672", Invoices.Consumer,
            Invoices.Line(0, "", "Sông Gianh HC-15 bột (25kg)", "kg", 2, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.Equal("READBACK_MISMATCH", result.Context.Snapshot.LastErrorCode);
        Assert.Null(harness.Catalog.Find("Sông Gianh HC-15 bột (25kg)", "kg"));
        Assert.Single(harness.Catalog.PendingSnapshot());
        Assert.Single(misa.State().CreatedItems);
        var retry = await harness.RunAsync(invoice, misa, token);
        Assert.Equal("ITEM_CREATE_UNCERTAIN", retry.Context.Snapshot.LastErrorCode);
        Assert.Single(misa.State().CreatedItems);
        Assert.Single(harness.Catalog.PendingSnapshot());
        Assert.Empty(misa.State().Vouchers);
    }, new FakeScenario(CreatedItemNameOverride: "Tên lưu khác trong MISA"), catalog: Catalog);

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

    // Invoice 1654 (0.2.61): the user moved the mouse during Cất; the Đóng click missed and the run
    // stopped with EDITOR_STILL_OPEN although MISA had saved the voucher.
    [Fact]
    public Task MouseMovedAfterSaveDoesNotRedirectTheCloseClick() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1654", Invoices.Consumer,
            Invoices.Line(0, "HH_07", "Hesman 60WG 18g", "Gói", 27, 16_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Single(misa.State().Vouchers);
        Assert.DoesNotContain("missed", File.ReadAllText(Path.Combine(misa.Root, "fake-events.log")), StringComparison.Ordinal);
    }, new FakeScenario(SaveClosesEditor: false, UserMouseNudge: "after_save"));

    [Fact]
    public Task MouseMovingDuringTheWholeWorkflowDoesNotRedirectClicks() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1655", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 38, 16_000, "8%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal(["HH_06", "HH_07"], Assert.Single(misa.State().Vouchers).Lines.Select(line => line.Code));
    }, new FakeScenario(SaveClosesEditor: false, UserMouseNudge: "always"));

    [Fact]
    public Task UserSwitchingTabsMidEntryIsRestoredWithoutReplayingInput() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1656", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 38, 16_000, "8%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal([22m, 38m], voucher.Lines.Select(line => line.Quantity));
        var log = File.ReadAllText(Path.Combine(misa.Root, "fake-events.log"));
        Assert.Contains("user switched tabs", log, StringComparison.Ordinal);
        // Each value reached MISA exactly once.
        Assert.Equal(2, log.Split('\n').Count(entry => entry.Contains(" quantity <- ", StringComparison.Ordinal)));
    }, new FakeScenario(UserSwitchesTabs: true));

    [Fact]
    public Task CloseSwallowedByAStrayClickIsRetriedOnlyWhileSaved() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1657", Invoices.Consumer,
            Invoices.Line(0, "HH_07", "Hesman 60WG 18g", "Gói", 3, 16_000, "8%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Single(misa.State().Vouchers);
        var log = File.ReadAllText(Path.Combine(misa.Root, "fake-events.log"));
        Assert.Equal(1, log.Split('\n').Count(entry => entry.Contains(" Cất (saved=False", StringComparison.Ordinal)));
    }, new FakeScenario(SaveClosesEditor: false, CloseSwallowedOnceAfterSave: true));

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
    public Task TaxPromptWithALargeDifferenceIsStillAnsweredNo() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1016", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 38, 16_000, "8%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal(invoice.TaxTotal, Assert.Single(misa.State().Vouchers).TaxTotal);
    }, new FakeScenario(TaxRoundingPrompt: true, TaxRoundingDifference: 25));

    [Fact]
    public Task TaxPromptWhoseTextIsALabelWithoutPatternsIsAnsweredNo() => WithFakeAsync(async (misa, harness, token) =>
    {
        // Report 0.2.68 (hóa đơn 1657): READBACK_UNSUPPORTED while scanning the prompt's text elements.
        var invoice = Invoices.Build(TempDirectory(misa), "1017", Invoices.Consumer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 38, 16_000, "8%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal(invoice.TaxTotal, Assert.Single(misa.State().Vouchers).TaxTotal);
    }, new FakeScenario(TaxRoundingPrompt: true, TaxPromptAsLabel: true));

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
    public Task StoppedCheckpointDoesNotBlockANewSessionAfterMisaChanges() => WithFakeAsync(async (misa, harness, token) =>
    {
        var first = Invoices.Build(TempDirectory(misa), "1040", Invoices.Consumer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));
        var started = await harness.Runner.StartAsync(first, "", misa.WindowIdentityText, token);
        await harness.Runner.PauseAsync(token);
        using var nextMisa = await FakeMisa.StartAsync(token: token);
        var second = Invoices.Build(TempDirectory(nextMisa), "1041", Invoices.Consumer,
            Invoices.Line(0, "HH_07", "Hesman 60WG 18g", "Gói", 1, 16_000, "8%"));

        var result = await harness.RunNewSessionAsync(second, nextMisa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.NotEqual(started.Snapshot.RunId, result.Context.Snapshot.RunId);
        Assert.Equal("1041", Assert.Single(nextMisa.State().Vouchers).InvoiceNumber);
        Assert.Empty(misa.State().Vouchers);
    });

    [Fact]
    public Task NewSessionChecksTheOpenEditorWithoutUsingTheStoppedCheckpoint() => WithFakeAsync(async (misa, harness, token) =>
    {
        var first = Invoices.Build(TempDirectory(misa), "1042", Invoices.Consumer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));
        var second = Invoices.Build(TempDirectory(misa), "1043", Invoices.Consumer,
            Invoices.Line(0, "HH_07", "Hesman 60WG 18g", "Gói", 1, 16_000, "8%"));
        var entered = await harness.EnterWithoutSavingAsync(first, misa, token);
        var sequence = harness.Input.WorkflowInputSequence;

        var blocked = await harness.RunNewSessionAsync(second, misa, token);

        Assert.Equal("1043", blocked.Context.Invoice.Identity.Number);
        Assert.NotEqual(entered.Context.Snapshot.RunId, blocked.Context.Snapshot.RunId);
        Assert.Equal("EXISTING_EDITOR", blocked.Context.Snapshot.LastErrorCode);
        Assert.Equal(sequence, harness.Input.WorkflowInputSequence);
        Assert.Empty(misa.State().Vouchers);
        await misa.CloseVoucherAsUserAsync(token);

        var result = await harness.RunNewSessionAsync(second, misa, token);
        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal("1043", Assert.Single(misa.State().Vouchers).InvoiceNumber);
    });

    [Fact]
    public Task DetachedSaveUncertaintyIsResolvedWithoutEnteringOrSavingTheInvoiceAgain() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1044", Invoices.Consumer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));
        var saved = await harness.RunAsync(invoice, misa, token);
        Assert.True(saved.Outcome == "saved", Describe(saved));
        var uncertain = saved.Context with
        {
            Snapshot = saved.Context.Snapshot with
            {
                Status = WorkflowStatus.SaveUncertain,
                InputMayHaveBeenSent = true,
                LastInputDisposition = InputDisposition.Uncertain
            }
        };
        var state = new JsonStateStore(Path.Combine(misa.Root, "state"));
        await state.WriteAsync("workflow-session", "session.json", uncertain, token);
        await using var reopened = new AutomationHarness(misa.Root);

        var result = await reopened.RunNewSessionAsync(invoice, misa, token);

        Assert.True(result.Outcome == "existing", Describe(result));
        Assert.NotEqual(uncertain.Snapshot.RunId, result.Context.Snapshot.RunId);
        Assert.Equal(0, reopened.Input.WorkflowInputSequence);
        Assert.Single(misa.State().Vouchers);
        var history = await reopened.Runner.ReadHistoryAsync(token);
        Assert.Equal(WorkflowStatus.Saved, history.Single(run => run.Snapshot.RunId == uncertain.Snapshot.RunId).Snapshot.Status);
    });

    [Fact]
    public Task NewSessionReleasesSaveUncertainRunOfAnEarlierMisaSessionWithoutReplayingIt() => WithFakeAsync(async (misa, harness, token) =>
    {
        var old = Invoices.Build(TempDirectory(misa), "1654", Invoices.Consumer,
            Invoices.Line(0, "HH_07", "Hesman 60WG 18g", "Gói", 1, 16_000, "8%"));
        var next = Invoices.Build(TempDirectory(misa), "1655", Invoices.Consumer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));
        var entered = await harness.EnterWithoutSavingAsync(old, misa, token);
        await misa.CloseVoucherAsUserAsync(token);
        // The report: Cất was sent in a MISA process that has since been restarted.
        const string earlierSession = "pid=1;start=2026-10-06T00:00:00.0000000Z;hwnd=1;exe=MISA.exe";
        var uncertain = entered.Context with
        {
            WindowIdentity = earlierSession,
            Snapshot = entered.Context.Snapshot with
            {
                WindowIdentity = earlierSession, Status = WorkflowStatus.SaveUncertain,
                InputMayHaveBeenSent = true, LastInputDisposition = InputDisposition.Uncertain
            }
        };
        await new JsonStateStore(Path.Combine(misa.Root, "state")).WriteAsync("workflow-session", "session.json", uncertain, token);
        await using var reopened = new AutomationHarness(misa.Root);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.RunAsync(next, misa, token));
        Assert.Contains("SaveUncertain", blocked.Message);

        var result = await reopened.RunNewSessionAsync(next, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal("1655", Assert.Single(misa.State().Vouchers).InvoiceNumber);
        var journal = (await reopened.Runner.ReadHistoryAsync(token)).Single(run => run.Snapshot.RunId == uncertain.Snapshot.RunId);
        Assert.True(journal.SessionDetached);
        Assert.Equal(WorkflowStatus.SaveUncertain, journal.Snapshot.Status);
        // The released run still guards its own invoice: no input is sent for 1654 again.
        var inputs = reopened.Input.WorkflowInputSequence;
        var guard = await Assert.ThrowsAsync<AutomationException>(() => reopened.RunNewSessionAsync(old, misa, token));
        Assert.Equal("DETACHED_INVOICE_UNCERTAIN", guard.Code);
        Assert.Equal(inputs, reopened.Input.WorkflowInputSequence);
        Assert.Single(misa.State().Vouchers);
    });

    [Fact]
    public Task DetachedUnsavedInvoiceCanStartFromReadyOnceTheRegisterProvesItAbsent() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1045", Invoices.Consumer,
            Invoices.Line(0, "HH_07", "Hesman 60WG 18g", "Gói", 1, 16_000, "8%"));
        var entered = await harness.EnterWithoutSavingAsync(invoice, misa, token);
        await harness.Runner.AbandonStoppedSessionAsync(token);
        await misa.CloseVoucherAsUserAsync(token);

        var result = await harness.RunNewSessionAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.NotEqual(entered.Context.Snapshot.RunId, result.Context.Snapshot.RunId);
        Assert.Equal("1045", Assert.Single(misa.State().Vouchers).InvoiceNumber);
        var history = await harness.Runner.ReadHistoryAsync(token);
        Assert.Equal(WorkflowStatus.Discarded, history.Single(run => run.Snapshot.RunId == entered.Context.Snapshot.RunId).Snapshot.Status);
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
