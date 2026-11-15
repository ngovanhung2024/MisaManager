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
        // No customer/supplier list loaded: the supplier is looked up by the MST typed into "Nhà cung cấp".
        Assert.Contains(events, line => line.Contains("field customer <- '" + Invoices.Supplier.TaxCode + "'"));
        Assert.Contains(events, line => line.Contains("customer NCC_PBH"));
        // "Ký hiệu HĐ" and "Mẫu số HĐ" of the source invoice are typed; MISA already proposed KHH/1561/331.
        Assert.Equal("1C26MLV", voucher.Series);
        Assert.Contains(events, line => line.Contains("field series <- '1C26MLV'"));
        Assert.Contains(events, line => line.Contains("field template <- '1'"));
        Assert.DoesNotContain(events, line => line.Contains(" stock <-") || line.Contains(" debit_account <-") || line.Contains(" credit_account <-"));
    }, Purchase);

    [Fact]
    public Task SupplierCodeFromLoadedListIsTypedInsteadOfMst() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2003", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 3, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal(("NCC_PBH", Invoices.Supplier.TaxCode), (voucher.CustomerCode, voucher.CustomerTaxCode));
        var events = Events(misa);
        // R20 does not always resolve a typed MST: with the supplier in the loaded list, its code is typed.
        Assert.Contains(events, line => line.Contains("field customer <- 'NCC_PBH'"));
        Assert.DoesNotContain(events, line => line.Contains("field customer <- '" + Invoices.Supplier.TaxCode + "'"));
    }, Purchase, customers: [new PartyCatalogEntry("NCC_PBH", Invoices.Supplier.Name, Invoices.Supplier.TaxCode)]);

    [Fact]
    public Task WarehouseAndAccountsAreLeftToMisaEvenWhenFilledLate() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2004", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 4, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Single(misa.State().Vouchers);
        // Real R20 can fill Kho/TK later than any wait; typing into Kho then shows "Dữ liệu không có trong danh mục.".
        Assert.DoesNotContain(Events(misa), line => line.Contains(" stock <-") || line.Contains(" debit_account <-") || line.Contains(" credit_account <-"));
    }, Purchase with { PurchaseDefaultsDelayMilliseconds = 8000 });

    [Fact]
    public Task ErrorAfterTheFirstLineStopsWithTheVoucherLeftOpen() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2005", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 3, 16_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "failed", Describe(result));
        // User request: on an error stop as is; never Bỏ the half-entered voucher and enter it again.
        Assert.False(result.MisaClean, Describe(result));
        Assert.False(result.AutoRerunSafe);
        Assert.Empty(misa.State().Vouchers);
        var events = Events(misa);
        Assert.DoesNotContain(events, line => line.Contains(" Đóng (", StringComparison.Ordinal));
        Assert.Single(events, line => line.Contains(" code <- 'HH_07'"));
    }, Purchase with { LineErrorRow = 1 });

    // Report 0.2.142 (invoice 633): the readback after "Thành tiền" saw the code cell mid-redraw and stopped with
    // ROW_INPUT_UNCERTAIN "Mã hàng ở dòng đích đã thay đổi" although the row was fine.
    [Fact]
    public Task RowRedrawnAfterTheAmountIsReadAgainNotFailed() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2006", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 5, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 2, 16_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal(["HH_06", "HH_07"], voucher.Lines.Select(line => line.Code));
        var events = Events(misa);
        Assert.Contains(events, line => line.Contains("row redraw", StringComparison.Ordinal));
        // Only re-read: each value is typed once.
        Assert.Single(events, line => line.Contains("row=0 amount <-", StringComparison.Ordinal));
    }, Purchase with { RowRedrawAfterAmountMilliseconds = 400 });

    // Report 0.2.143 (invoice 633): after Thành tiền the grid scrolled sideways, the wide Tên hàng cell lay under the
    // frozen Mã hàng column and was read as the code ("Mã hàng ở dòng đích đã thay đổi") for the whole 10 s wait.
    [Fact]
    public Task GridScrolledSidewaysStillReadsTheFrozenCodeColumn() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2007", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 5, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 2, 16_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal(["HH_06", "HH_07"], Assert.Single(misa.State().Vouchers).Lines.Select(line => line.Code));
        Assert.Single(Events(misa), line => line.Contains("row=0 amount <-", StringComparison.Ordinal));
    }, Purchase with { ScrollSidewaysAfterAmount = true });

    // Report 0.2.144: the last session was a saved Bán hàng invoice; with only Mua hàng open the first purchase invoice
    // stopped with "Cannot prepare the next invoice: Mở màn hình Bán hàng của dữ liệu đã chọn."
    [Fact]
    public Task SavedSalesSessionDoesNotBlockTheFirstPurchaseInvoice() => WithFakeAsync(async (misa, harness, token) =>
    {
        var sale = Invoices.Build(TempDirectory(misa), "1824", Invoices.Consumer,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 1, 10_000, "5%"));
        var runId = Guid.NewGuid().ToString("N");
        var saved = new WorkflowContext(sale,
            new WorkflowSnapshot(runId, sale.InvoiceId, sale.Revision, WorkflowStatus.Saved, "", misa.WindowIdentityText, null,
                DateTimeOffset.UtcNow, "0.2.144.0", ProductIdentity.StateSchemaVersion), "", misa.WindowIdentityText);
        var state = new JsonStateStore(Path.Combine(misa.Root, "state"));
        await state.WriteAsync("workflow-context", Path.Combine("runs", runId, "workflow.json"), saved, token);
        await state.WriteAsync("workflow-session", "session.json", saved, token);
        await using var restarted = new AutomationHarness(misa.Root);
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2008", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));

        var result = await restarted.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal("2008", Assert.Single(misa.State().Vouchers).InvoiceNumber);
    }, Purchase);

    // Report 0.2.145 (invoice 633): every line was entered, then reconciliation waited for "creTotalAmountOC", an ID the
    // R20 phiếu nhập does not have (RECONCILIATION_PENDING). The fake's purchase totals carry IDs the profile never names.
    [Fact]
    public Task PurchaseTotalsAreReadByTheirCaptions() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2009", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 3, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 2, 16_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var totals = result.Verification.Comparison!.Values.Where(value => value.Field is "subtotal" or "tax_total" or "grand_total").ToArray();
        Assert.Equal(3, totals.Length);
        Assert.All(totals, value => Assert.True(value.Matched, value.Field + ": " + value.Actual));
    }, Purchase);

    // Report 0.2.147 (HĐ 244): 11571429 / 1000 kg; MISA keeps 11.571,43 and reports "11571,4300", which is not 11571,4290.
    [Fact]
    public Task PurchaseUnitPricePaddedToFourDecimalsMatches() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "244", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 1000, 11_571.429m, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var price = Assert.Single(result.Verification.Comparison!.Values, value => value.Field.EndsWith(":unit_price", StringComparison.Ordinal));
        Assert.Equal("11.571,4300", price.Actual);
    }, Purchase);

    // Report 0.2.148 (HĐ 1065): 57.619.048 / 5 t; R20 derived 11.523.809,67, not 11.523.809,60. Amount and totals were exact.
    [Fact]
    public Task PurchaseUnitPriceMisaDerivesDifferentlyStillMatches() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "1065", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 5, 11_523_809.6m, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var price = Assert.Single(result.Verification.Comparison!.Values, value => value.Field.EndsWith(":unit_price", StringComparison.Ordinal));
        Assert.Equal("11.523.809,6700", price.Actual);
    }, Purchase with { UnitPriceDrift = 0.07m });

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

    // Lộc Thủy (MST 3301661802) only enters fuel. MISA's catalogue spells the items its own way ("Xăng Ron 95-III").
    private static readonly FakeItem[] FuelItems =
    [
        new("XANG_95", "Xăng Ron 95-III", "Lít", "10%"),
        new("NHOT_CX", "Nhớt Caltex super", "Lít", "10%"),
        new("DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", "10%")
    ];

    [Fact]
    public Task LocThuyPetrolAndOilGoToVoucherWithoutStockAndAccount154() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "3100", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "XANG_95", "Xăng RON 95 Mức 3", "Lít", 20, 20_000, "10%"),
            Invoices.Line(1, "NHOT_CX", "Nhớt Caltex super", "Lít", 5, 90_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal("expense", voucher.VoucherType);
        Assert.StartsWith("MH0", voucher.VoucherNumber);
        Assert.Equal(["XANG_95", "NHOT_CX"], voucher.Lines.Select(line => line.Code));
        // MISA proposed TK chi phí 632 (the item's); the buyer's rule makes it 154. No Kho on this voucher.
        Assert.All(voucher.Lines, line => Assert.Equal(("", "154", "331"), (line.Stock, line.DebitAccount, line.CreditAccount)));
        Assert.Equal((invoice.Subtotal, invoice.TaxTotal, invoice.GrandTotal), (voucher.Subtotal, voucher.TaxTotal, voucher.GrandTotal));

        var events = Events(misa);
        var typeIndex = Array.FindIndex(events, line => line.Contains("field voucher_type <- '2. Mua hàng trong nước không qua kho'"));
        Assert.True(typeIndex >= 0, "Chưa chọn loại chứng từ 2. Mua hàng trong nước không qua kho.");
        Assert.True(typeIndex < Array.FindIndex(events, line => line.Contains("field customer <-")), "Loại chứng từ phải đổi trước khi nhập nhà cung cấp.");
        Assert.Contains(events, line => line.Contains("voucher type → không qua kho"));
        Assert.Contains(events, line => line.Contains("row=0 debit_account <- '154'"));
        Assert.Contains(events, line => line.Contains("row=1 debit_account <- '154'"));
        Assert.DoesNotContain(events, line => line.Contains(" stock <-"));
        // R20 asks whether to post a TK 154 line without Khoản mục chi phí; the tool answers Có.
        Assert.Contains(events, line => line.Contains("prompt: Khoản mục chi phí → Yes"));
    }, Purchase with { AdditionalItems = FuelItems });

    // Kho is MISA's own default for the item (CTY at Lộc Thủy): the tool neither types nor checks it. TK kho 156 → 152.
    [Fact]
    public Task LocThuyDieselKeepsMisaStockAndGetsAccount152() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "3101", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 100, 19_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal("stock", voucher.VoucherType);
        Assert.StartsWith("NK0", voucher.VoucherNumber);
        var line = Assert.Single(voucher.Lines);
        Assert.Equal(("CTY", "152", "331"), (line.Stock, line.DebitAccount, line.CreditAccount));
        var events = Events(misa);
        Assert.DoesNotContain(events, entry => entry.Contains(" stock <-"));
        Assert.Contains(events, entry => entry.Contains("row=0 debit_account <- '152'"));
        Assert.DoesNotContain(events, entry => entry.Contains("field voucher_type"));
        Assert.DoesNotContain(events, entry => entry.Contains("prompt: Khoản mục chi phí"));
    }, Purchase with { AdditionalItems = FuelItems, PurchaseItemStock = "CTY", PurchaseItemStockAccount = "156" });

    [Fact]
    public Task LocThuyDieselAlreadyOnCtyAnd152IsNotRetyped() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "3102", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 50, 19_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var line = Assert.Single(Assert.Single(misa.State().Vouchers).Lines);
        Assert.Equal(("CTY", "152"), (line.Stock, line.DebitAccount));
        Assert.DoesNotContain(Events(misa), entry => entry.Contains(" stock <-") || entry.Contains(" debit_account <-"));
    }, Purchase with { AdditionalItems = FuelItems, PurchaseItemStock = "CTY", PurchaseItemStockAccount = "152" });

    // Report 0.2.188, HĐ 205: R20's "Chứng từ mua hàng" is a drop-down list that takes no focus; typing the type stopped
    // with "Ô đích chưa nhận focus". The arrow is clicked and the option picked from the list, as the user does.
    [Fact]
    public Task VoucherTypeIsPickedFromTheDropDownList() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "205", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "XANG_95", "Xăng RON 95 Mức 3", "Lít", 20, 20_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal("expense", Assert.Single(misa.State().Vouchers).VoucherType);
        Assert.Single(Events(misa), line => line.Contains("field voucher_type <-"));
    }, Purchase with { AdditionalItems = FuelItems, EditorUnowned = true });

    [Fact]
    public Task RememberedExpenseTypeIsKeptWithoutInput() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "206", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "XANG_95", "Xăng RON 95 Mức 3", "Lít", 20, 20_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal("expense", voucher.VoucherType);
        Assert.StartsWith("MH0", voucher.VoucherNumber);
        Assert.DoesNotContain(Events(misa), line => line.Contains("field voucher_type <-"));
    }, Purchase with { AdditionalItems = FuelItems, VoucherTypeRemembered = "2. Mua hàng trong nước không qua kho" });

    // After a xăng invoice R20 reopens "Thêm" as "không qua kho": diesel must go back to "1. … nhập kho" (Kho CTY, TK 152).
    [Fact]
    public Task DieselAfterARememberedExpenseTypeGoesBackToStock() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "207", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 10, 20_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal("stock", voucher.VoucherType);
        Assert.Equal(("CTY", "152"), (voucher.Lines[0].Stock, voucher.Lines[0].DebitAccount));
        var events = Events(misa);
        Assert.Contains(events, line => line.Contains("field voucher_type <- '1. Mua hàng trong nước nhập kho'"));
        Assert.True(Array.FindIndex(events, line => line.Contains("field voucher_type <-"))
            < Array.FindIndex(events, line => line.Contains("field customer <-")), "Loại chứng từ phải đổi trước khi nhập nhà cung cấp.");
    }, Purchase with { AdditionalItems = FuelItems, PurchaseItemStock = "CTY", VoucherTypeRemembered = "2. Mua hàng trong nước không qua kho" });

    // Keep list-selection coverage on KCT; KKKNT now uses text + Enter as confirmed by the user.
    [Fact]
    public Task DieselExemptVatIsPickedFromTheRateList() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "18534", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 40.422m, 35_890m, "KCT"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var line = Assert.Single(Assert.Single(misa.State().Vouchers).Lines);
        Assert.Equal(("KCT", 0m), (line.TaxRate, line.TaxAmount));
        var rateEvent = Assert.Single(Events(misa), entry => entry.Contains(" tax_rate <- "));
        Assert.Contains("'KCT' by None", rateEvent);
    }, Purchase with { AdditionalItems = FuelItems, PurchaseItemStock = "CTY", TaxRateValueIsKey = true, QuantityDecimals = 2,
        EditorUnowned = true });

    // An already dropped list must not be toggled closed; use KCT to exercise the remaining list path.
    [Fact]
    public Task RateListDroppedByTheCellClickIsNotToggledClosed() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "18534", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 40.422m, 35_890m, "KCT"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var line = Assert.Single(Assert.Single(misa.State().Vouchers).Lines);
        Assert.Equal(("KCT", 0m), (line.TaxRate, line.TaxAmount));
        Assert.Contains(Events(misa), entry => entry.Contains("tax_rate list dropped by the cell click"));
        Assert.Contains("'KCT' by None", Assert.Single(Events(misa), entry => entry.Contains(" tax_rate <- ")));
    }, Purchase with { AdditionalItems = FuelItems, PurchaseItemStock = "CTY", TaxRateValueIsKey = true, QuantityDecimals = 2,
        EditorUnowned = true, TaxRateListOpensOnClick = true });

    // Report 0.2.203, HĐ 18534: the rate list was drawn open but UIA gave none of its items (GRID_DROPDOWN_NOT_OPEN). The
    // option is reached with the arrow keys, the cell read back after every key, and taken with Enter.
    [Theory]
    [InlineData(false, "10%", "KCT")]
    [InlineData(true, "10%", "KCT")]
    [InlineData(false, "KKKNT", "KCT")]
    public Task RateListWithoutUiaItemsIsPickedWithArrowKeys(bool opensOnClick, string initialRate, string expectedRate)
        => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "18534", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 40.422m, 35_890m, expectedRate));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var line = Assert.Single(Assert.Single(misa.State().Vouchers).Lines);
        Assert.Equal((expectedRate, 0m), (line.TaxRate, line.TaxAmount));
        var events = Events(misa);
        Assert.Contains(events, entry => entry.Contains($"tax_rate list moved to '{expectedRate}'"));
        Assert.Single(events, entry => entry.Contains($"tax_rate list closed on '{expectedRate}'"));
        Assert.Contains($"'{expectedRate}' by None", Assert.Single(events, entry => entry.Contains(" tax_rate <- ")));
        if (initialRate == "KKKNT") Assert.Contains(events, entry => entry.Contains("tax_rate list key Up"));
    }, Purchase with { AdditionalItems = FuelItems.Select(item => item with { TaxRate = initialRate }).ToArray(),
        PurchaseItemStock = "CTY", TaxRateValueIsKey = true, QuantityDecimals = 2,
        EditorUnowned = true, TaxRateListHiddenFromUia = true, TaxRateListOpensOnClick = opensOnClick });

    [Theory]
    [InlineData("close")]
    [InlineData("ignore-arrows")]
    [InlineData("ignore-enter")]
    [InlineData("reject-selection")]
    public Task UnconfirmedRateListSelectionStopsWithoutSavingOrReplaying(string fault) => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "18534", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 40.422m, 35_890m, "KCT"));

        var result = await harness.EnterWithoutSavingAsync(invoice, misa, token);

        Assert.Equal(WorkflowStatus.InputUncertain, result.Context.Snapshot.Status);
        Assert.Equal("INPUT_UNCERTAIN", result.Context.Snapshot.LastErrorCode);
        Assert.Empty(misa.State().Vouchers);
        var events = Events(misa);
        Assert.DoesNotContain(events, entry => entry.Contains(" Cất ("));
        if (fault is "ignore-enter" or "reject-selection")
            Assert.Single(events, entry => entry.Contains("tax_rate list key Return"));
        else
            Assert.Equal(fault == "close" ? 1 : 2, events.Count(entry => entry.Contains("tax_rate list key ")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runner.AdvanceAsync(token));
        Assert.Equal(events, Events(misa));
    }, Purchase with { AdditionalItems = FuelItems, PurchaseItemStock = "CTY", TaxRateValueIsKey = true, QuantityDecimals = 2,
        EditorUnowned = true, TaxRateListHiddenFromUia = true, TaxRateListKeyFault = fault,
        TaxRateStatusStaleWhileOpen = true });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RateListReadsLiveEditorWhenCommittedStatusStaysAtEightPercent(bool opensOnClick)
        => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "18534", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 40.422m, 35_890m, "KCT"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var line = Assert.Single(Assert.Single(misa.State().Vouchers).Lines);
        Assert.Equal(("KCT", 0m), (line.TaxRate, line.TaxAmount));
        var events = Events(misa);
        Assert.Contains(events, entry => entry.Contains("status frozen at '8%'"));
        Assert.Single(events, entry => entry.Contains("tax_rate list closed on 'KCT'"));
        Assert.Contains("'KCT' by None", Assert.Single(events, entry => entry.Contains(" tax_rate <- ")));
    }, Purchase with { AdditionalItems = FuelItems.Select(item => item with { TaxRate = "8%" }).ToArray(),
        PurchaseItemStock = "CTY", TaxRateValueIsKey = true, QuantityDecimals = 2, EditorUnowned = true,
        TaxRateListHiddenFromUia = true, TaxRateStatusStaleWhileOpen = true, TaxRateListOpensOnClick = opensOnClick });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RateListWithTextLabelsIsClickedWithoutArrowKeys(bool opensOnClick)
        => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "18534", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 40.422m, 35_890m, "KCT"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var line = Assert.Single(Assert.Single(misa.State().Vouchers).Lines);
        Assert.Equal(("KCT", 0m), (line.TaxRate, line.TaxAmount));
        var events = Events(misa);
        Assert.DoesNotContain(events, entry => entry.Contains("tax_rate list key "));
        Assert.Single(events, entry => entry.Contains("tax_rate list closed on 'KCT'"));
        Assert.Contains("'KCT' by None", Assert.Single(events, entry => entry.Contains(" tax_rate <- ")));
    }, Purchase with { AdditionalItems = FuelItems.Select(item => item with { TaxRate = "8%" }).ToArray(),
        PurchaseItemStock = "CTY", TaxRateValueIsKey = true, QuantityDecimals = 2, EditorUnowned = true,
        TaxRateListHiddenFromUia = true, TaxRateStatusStaleWhileOpen = true, TaxRateListOpensOnClick = opensOnClick,
        TaxRateListTextAccessible = true, TaxRateListKeyFault = "close" });

    [Fact]
    public Task ClickedRateLabelStillRequiresCommittedReadbackAndCannotReplay()
        => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "18534", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 40.422m, 35_890m, "KCT"));

        var result = await harness.EnterWithoutSavingAsync(invoice, misa, token);

        Assert.Equal(WorkflowStatus.InputUncertain, result.Context.Snapshot.Status);
        Assert.Equal("INPUT_UNCERTAIN", result.Context.Snapshot.LastErrorCode);
        Assert.Contains("MISA hiện '8%'", result.Context.Snapshot.LastMessage);
        Assert.Contains("cần 'KCT'", result.Context.Snapshot.LastMessage);
        Assert.Empty(misa.State().Vouchers);
        var events = Events(misa);
        Assert.DoesNotContain(events, entry => entry.Contains("tax_rate list key ") || entry.Contains(" Cất ("));
        Assert.Single(events, entry => entry.Contains("tax_rate selection rejected"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runner.AdvanceAsync(token));
        Assert.Equal(events, Events(misa));
    }, Purchase with { AdditionalItems = FuelItems.Select(item => item with { TaxRate = "8%" }).ToArray(),
        PurchaseItemStock = "CTY", TaxRateValueIsKey = true, QuantityDecimals = 2, EditorUnowned = true,
        TaxRateListHiddenFromUia = true, TaxRateStatusStaleWhileOpen = true, TaxRateListOpensOnClick = true,
        TaxRateListTextAccessible = true, TaxRateListKeyFault = "reject-selection" });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task KkkntIsEnteredAsTextAndCommittedWithEnter(bool opensOnClick)
        => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "18534", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 40.422m, 35_890m, "KKKNT"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal("KKKNT", Assert.Single(Assert.Single(misa.State().Vouchers).Lines).TaxRate);
        var events = Events(misa);
        Assert.Single(events, entry => entry.Contains(" tax_rate <- 'KKKNT' by Return"));
        Assert.Contains(events, entry => entry.Contains("tax_rate keyboard text received"));
        Assert.DoesNotContain(events, entry => entry.Contains("tax_rate list key Down")
            || entry.Contains("tax_rate list key Up") || entry.Contains("tax_rate list moved"));
    }, Purchase with { AdditionalItems = FuelItems.Select(item => item with { TaxRate = "8%" }).ToArray(),
        PurchaseItemStock = "CTY", TaxRateValueIsKey = true, QuantityDecimals = 2, EditorUnowned = true,
        TaxRateListHiddenFromUia = true, TaxRateStatusStaleWhileOpen = true, TaxRateListOpensOnClick = opensOnClick,
        TaxRateListKeyFault = "ignore-arrows", TaxRateRequiresKeyboardText = true });

    [Fact]
    public Task RejectedTypedKkkntStopsWithoutSavingOrTryingTheList()
        => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "18534", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 40.422m, 35_890m, "KKKNT"));

        var result = await harness.EnterWithoutSavingAsync(invoice, misa, token);

        Assert.Equal(WorkflowStatus.InputUncertain, result.Context.Snapshot.Status);
        Assert.Empty(misa.State().Vouchers);
        var events = Events(misa);
        Assert.Single(events, entry => entry.Contains(" tax_rate <- 'KKKNT' by Return"));
        Assert.DoesNotContain(events, entry => entry.Contains("tax_rate list moved") || entry.Contains(" Cất ("));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runner.AdvanceAsync(token));
        Assert.Equal(events, Events(misa));
    }, Purchase with { AdditionalItems = FuelItems.Select(item => item with { TaxRate = "8%" }).ToArray(),
        PurchaseItemStock = "CTY", TaxRateValueIsKey = true, QuantityDecimals = 2, EditorUnowned = true,
        TaxRateListHiddenFromUia = true, TaxRateTextIgnored = true, TaxRateRequiresKeyboardText = true });

    // Report 0.2.202, HĐ 18534: "Dữ liệu đã bị thay đổi…" came after the 1.2 s Bỏ waited for it and the unowned form stayed
    // open (EDITOR_STILL_OPEN). A late prompt is still answered Không, once.
    [Fact]
    public Task LateDirtyPromptOnAnUnownedFormIsAnsweredAndTheVoucherIsBo() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "18535", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 38, 16_000, "8%"));

        var result = await harness.RunAsync(invoice, misa, token, discardOnFailure: true);

        Assert.True(result.Outcome == WorkflowRunner.ErrorDiscardedOutcome, Describe(result));
        Assert.Equal(WorkflowStatus.Discarded, result.Context.Snapshot.Status);
        Assert.Empty(misa.State().Vouchers);
        Assert.Contains(Events(misa), entry => entry.Contains("Đóng: hỏi cất sau"));
    }, Purchase with { QuantityIgnoredRow = 1, EditorUnowned = true, DirtyPromptDelayMilliseconds = 3000 });

    // Report 0.2.197, HĐ 392: the unowned form was retitled "… nhập kho …" only after the readback had started; the guard
    // took the new title for another window and the voucher was Bỏ. The retitle naming the chosen type is the same form.
    [Fact]
    public Task VoucherTypeRetitlingTheUnownedFormLaterIsTheSameForm() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "392", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 57.535m, 16_898.1481m, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal("stock", Assert.Single(misa.State().Vouchers).VoucherType);
        Assert.Single(Events(misa), line => line.Contains("field voucher_type <- "));
    }, Purchase with { AdditionalItems = FuelItems, EditorUnowned = true, VoucherTypeRetitleMilliseconds = 400,
        VoucherTypeRemembered = "2. Mua hàng trong nước không qua kho", QuantityDecimals = 2 });

    [Fact]
    public Task OrdinaryPurchaseOnStockTypeSendsNoVoucherTypeInput() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2208", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        Assert.Equal("stock", Assert.Single(misa.State().Vouchers).VoucherType);
        Assert.DoesNotContain(Events(misa), line => line.Contains("field voucher_type <-"));
    }, Purchase);

    // Report 0.2.187, HĐ 205: the voucher form opened as a top-level window without owner; the tool searched only under
    // MISA's main window, waited 60 s for "Editor chưa sẵn sàng" and Bỏ the voucher.
    [Fact]
    public Task VoucherFormOpenedWithoutOwnerIsStillFoundAndSaved() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "205", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "XANG_95", "Xăng RON 95 Mức 3", "Lít", 20, 20_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal("expense", voucher.VoucherType);
        Assert.Equal(("", "154", "331"), (voucher.Lines[0].Stock, voucher.Lines[0].DebitAccount, voucher.Lines[0].CreditAccount));
        // MISA's "Khoản mục chi phí" prompt is owned by the unowned form: it must still be seen (and answered as before).
        Assert.Contains(Events(misa), line => line.Contains("prompt: Khoản mục chi phí → Yes"));
    }, Purchase with { AdditionalItems = FuelItems, EditorUnowned = true });

    // Report 0.2.196, HĐ 205: 17,487 lít; the company data keeps 2 số lẻ for Số lượng and MISA books 17,49 with the exact
    // Thành tiền. That rounding is accepted (unit price checked against 17,49); Số lượng is typed once.
    [Fact]
    public Task QuantityMisaRoundsToItsDecimalsIsSaved() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "205", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "XANG_95", "Xăng RON 95 Mức 3", "Lít", 17.487m, 17_740.7407m, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var line = Assert.Single(Assert.Single(misa.State().Vouchers).Lines);
        Assert.Equal((17.49m, invoice.Lines[0].AmountBeforeTax!.Value), (line.Quantity, line.Amount));
        Assert.Single(Events(misa), entry => entry.Contains(" quantity <- "));
    }, Purchase with { AdditionalItems = FuelItems, EditorUnowned = true, QuantityDecimals = 2 });

    // Report 0.2.199, HĐ 441: 26,096 lít at 2 số lẻ shows 26,10 — the same number as a 1-decimal rounding. It was taken for
    // a 1-decimal setting and the voucher was Bỏ; it is MISA's 2-decimal rounding and is saved.
    [Fact]
    public Task QuantityRoundedToTwoDecimalsEndingInZeroIsSaved() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "441", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "XANG_95", "Xăng RON 95 Mức 3", "Lít", 26.096m, 17_740.7407m, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var line = Assert.Single(Assert.Single(misa.State().Vouchers).Lines);
        Assert.Equal((26.10m, invoice.Lines[0].AmountBeforeTax!.Value), (line.Quantity, line.Amount));
        Assert.Single(Events(misa), entry => entry.Contains(" quantity <- "));
    }, Purchase with { AdditionalItems = FuelItems, EditorUnowned = true, QuantityDecimals = 2 });

    // A coarser rounding (1 số lẻ: 17,487 → 17,5) is not accepted: the row stops with the setting to change, nothing is saved.
    [Fact]
    public Task QuantityMisaRoundsCoarselyIsReportedWithTheDecimalSetting() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "205", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "XANG_95", "Xăng RON 95 Mức 3", "Lít", 17.487m, 17_740.7407m, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.NotEqual("saved", result.Outcome);
        Assert.Empty(misa.State().Vouchers);
        Assert.Equal("ROW_INPUT_UNCERTAIN", result.Context.Snapshot.LastErrorCode);
        Assert.Contains("làm tròn số lượng 17,487 thành '17,500'", result.Context.Snapshot.LastMessage);
        Assert.Contains("1 chữ số lẻ", result.Context.Snapshot.LastMessage);
        Assert.Single(Events(misa), entry => entry.Contains(" quantity <- "));
    }, Purchase with { AdditionalItems = FuelItems, EditorUnowned = true, QuantityDecimals = 1 });

    [Fact]
    public Task DialogOverAnUnownedVoucherFormIsReportedNotTypedThrough() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.BuildPurchase(TempDirectory(misa), "2105", Invoices.Supplier,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 2, 10_000, "5%"),
            Invoices.Line(1, "HH_07", "Hesman 60WG 18g", "Gói", 3, 16_000, "5%"));

        var result = await harness.RunAsync(invoice, misa, token);

        // Same outcome as with an owned form: stop with the voucher left open, nothing typed after the dialog.
        Assert.True(result.Outcome == "failed", Describe(result));
        Assert.Empty(misa.State().Vouchers);
        Assert.Single(Events(misa), line => line.Contains(" code <- 'HH_07'"));
    }, Purchase with { LineErrorRow = 1, EditorUnowned = true });

    // Report 0.2.186, HĐ 392: MISA lists THANH LƯƠNG and TL with the same MST 3300482529.
    private static readonly Party ThanhLuong = new("CÔNG TY CỔ PHẦN XĂNG DẦU THANH LƯƠNG", "3300482529",
        "51 Nguyễn Gia Thiều, Phường Phú Xuân, Thành Phố Huế", "", ValuePresence.Provided);
    private static readonly FakeCustomer[] ThanhLuongCodes =
    [
        new("THANH LƯƠNG", ThanhLuong.Name, ThanhLuong.Address, ThanhLuong.TaxCode),
        new("TL", "Công ty CP xăng dầu Thanh Lương", ThanhLuong.Address, ThanhLuong.TaxCode)
    ];

    private static void LoadThanhLuongSuppliers(AutomationHarness harness, FakeMisa misa)
        => harness.Parties.Replace(PartyRole.Supplier, [.. ThanhLuongCodes.Select(code => new PartyCatalogEntry(code.Code, code.Name, code.TaxCode)),
            new PartyCatalogEntry("NCC_PBH", Invoices.Supplier.Name, Invoices.Supplier.TaxCode)], Path.Combine(misa.Root, "Nha_cung_cap.xlsx"));

    [Fact]
    public Task SupplierMstSharedByTwoCodesStopsBeforeInputUntilACodeIsChosen() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        LoadThanhLuongSuppliers(harness, misa);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "392", ThanhLuong, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 10, 20_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "failed", Describe(result));
        Assert.Equal("SUPPLIER_AMBIGUOUS", result.Context.Snapshot.LastErrorCode);
        Assert.False(result.Context.Snapshot.InputMayHaveBeenSent);
        Assert.Empty(misa.State().Vouchers);
        Assert.DoesNotContain(Events(misa), line => line.Contains("field customer <-"));
    }, Purchase with { AdditionalItems = FuelItems, AdditionalSuppliers = ThanhLuongCodes });

    [Fact]
    public Task ChosenSupplierCodeIsTypedForTheSharedMst() => WithFakeAsync(async (misa, harness, token) =>
    {
        harness.Companies.Select(CompanyProfileStore.LocThuyId);
        LoadThanhLuongSuppliers(harness, misa);
        harness.Parties.SetPreferred([new PartyPreference(PartyRole.Supplier, ThanhLuong.TaxCode, "TL")]);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "392", ThanhLuong, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 10, 20_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        Assert.True(result.Outcome == "saved", Describe(result));
        var voucher = Assert.Single(misa.State().Vouchers);
        Assert.Equal(("TL", ThanhLuong.TaxCode), (voucher.CustomerCode, voucher.CustomerTaxCode));
        Assert.Contains(Events(misa), line => line.Contains("field customer <- 'TL'"));
        Assert.DoesNotContain(Events(misa), line => line.Contains("field customer <- 'THANH LƯƠNG'"));
    }, Purchase with { AdditionalItems = FuelItems, AdditionalSuppliers = ThanhLuongCodes });

    [Fact]
    public Task LocThuyInvoiceWhileLaVietIsSelectedStopsBeforeInput() => WithFakeAsync(async (misa, harness, token) =>
    {
        Assert.Equal(CompanyProfileStore.LaVietId, harness.Companies.Active.Id);
        var invoice = Invoices.BuildPurchaseFor(TempDirectory(misa), "3103", Invoices.Supplier, Invoices.LocThuy,
            Invoices.Line(0, "DAU_DO", "Dầu Điêzen 0,05S Mức 2", "Lít", 10, 20_000, "10%"));

        var result = await harness.RunAsync(invoice, misa, token);

        // Lá Việt has no fuel rule: entering it would post diesel to KHH/1561 instead of CTY/152.
        Assert.True(result.Outcome == "failed", Describe(result));
        Assert.Equal(CompanyProfileStore.MismatchCode, result.Context.Snapshot.LastErrorCode);
        Assert.False(result.Context.Snapshot.InputMayHaveBeenSent);
        Assert.Empty(misa.State().Vouchers);
        Assert.DoesNotContain(Events(misa), line => line.Contains("field customer <-"));
    }, Purchase with { AdditionalItems = FuelItems });
}
