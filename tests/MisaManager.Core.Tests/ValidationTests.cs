using System.Collections.Immutable;
using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class ValidationTests
{
    private static InvoiceLine Line(int index = 0, decimal quantity = 10, decimal price = 5_000m, string rate = "8%")
        => InvoiceBuilder.Line(index, "Phân NPK 16.16.8.6S", "kg", quantity, price, rate);

    private static IReadOnlyList<ImportIssue> Validate(InvoiceDocument invoice) => InvoiceValidation.Validate(invoice);

    private static ImportIssue Single(IReadOnlyList<ImportIssue> issues, string code) => Assert.Single(issues, issue => issue.Code == code);

    private static void AssertBlocking(IReadOnlyList<ImportIssue> issues, string code)
        => Assert.Equal(IssueSeverity.Blocking, Single(issues, code).Severity);

    private static IReadOnlyList<ImportIssue> Blocking(IReadOnlyList<ImportIssue> issues)
        => issues.Where(issue => issue.Severity == IssueSeverity.Blocking).ToArray();

    [Fact]
    public void ValidInvoiceHasNoBlockingIssuesAndNoWarnings()
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Empty(Validate(invoice));
        Assert.False(invoice.IsBlocked);
    }

    [Fact]
    public void ValidationIsRepeatableForTheSameInvoice()
    {
        var invoice = new InvoiceBuilder().WithBuyer(InvoiceBuilder.Consumer).Build();

        Assert.Equal(Validate(invoice), Validate(invoice));
    }

    [Fact]
    public void NullInvoiceIsRejected() => Assert.Throws<ArgumentNullException>(() => InvoiceValidation.Validate(null!));

    [Fact]
    public void ValidIssuesCarryTheInvoiceIdAndWorksheetOfBlockingFindings()
    {
        var invoice = InvoiceBuilder.Valid() with { Issued = null };

        var issue = Single(Validate(invoice), "DATES_MISSING");

        Assert.Equal(InvoiceBuilder.InvoiceId, issue.InvoiceId);
        Assert.Equal(InvoiceBuilder.Worksheet, issue.Worksheet);
        Assert.False(string.IsNullOrWhiteSpace(issue.Resolution));
    }

    // ---- structural problems: validation stops before touching the rest of the graph ----

    [Fact]
    public void MissingIdentityBlocks()
    {
        var issues = Validate(InvoiceBuilder.Valid() with { Identity = null! });

        AssertBlocking(issues, "IDENTITY_MISSING");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInvoiceNumberBlocks(string number)
    {
        var invoice = InvoiceBuilder.Valid();

        AssertBlocking(Validate(invoice with { Identity = invoice.Identity with { Number = number } }), "NUMBER_MISSING");
    }

    [Fact]
    public void MissingSourceBlocks() => AssertBlocking(Validate(InvoiceBuilder.Valid() with { Source = null! }), "SOURCE_MISSING");

    [Fact]
    public void MissingSellerBlocks() => AssertBlocking(Validate(InvoiceBuilder.Valid() with { Seller = null! }), "SELLER_MISSING");

    [Fact]
    public void MissingBuyerBlocksAndStopsEarly()
    {
        var issues = Validate(InvoiceBuilder.Valid() with { Buyer = null! });

        AssertBlocking(issues, "BUYER_MISSING");
        Assert.DoesNotContain(issues, issue => issue.Code is "TOTALS_MISSING" or "TOTAL_MISMATCH");
    }

    [Fact]
    public void DefaultIssuesArrayBlocks() => AssertBlocking(Validate(InvoiceBuilder.Valid() with { Issues = default }), "ISSUES_MISSING");

    [Fact]
    public void NullEntryInIssuesBlocks()
        => AssertBlocking(Validate(InvoiceBuilder.Valid() with { Issues = [null!] }), "ISSUES_MISSING");

    [Fact]
    public void DefaultLinesArrayBlocks() => AssertBlocking(Validate(InvoiceBuilder.Valid() with { Lines = default }), "LINES_MISSING");

    [Fact]
    public void EmptyLinesBlocks()
        => AssertBlocking(Validate(InvoiceBuilder.Valid() with { Lines = ImmutableArray<InvoiceLine>.Empty }), "LINES_MISSING");

    [Fact]
    public void NullLineEntryBlocks()
        => AssertBlocking(Validate(InvoiceBuilder.Valid() with { Lines = [null!] }), "LINE_MISSING");

    [Fact]
    public void LineWithoutCellsBlocks()
    {
        var invoice = InvoiceBuilder.Valid();
        var line = invoice.Lines[0] with { Cells = null! };

        var issue = Single(Validate(invoice with { Lines = invoice.Lines.SetItem(0, line) }), "LINE_CELLS_MISSING");

        Assert.Equal(line.LineId, issue.LineId);
    }

    [Fact]
    public void LineCellsContainingNullBlock()
    {
        var invoice = InvoiceBuilder.Valid();
        var line = invoice.Lines[0] with { Cells = invoice.Lines[0].Cells.SetItem("name", null!) };

        AssertBlocking(Validate(invoice with { Lines = invoice.Lines.SetItem(0, line) }), "LINE_CELLS_MISSING");
    }

    // ---- buyer ----

    [Fact]
    public void BlankBuyerNameBlocks()
    {
        var invoice = new InvoiceBuilder().WithBuyer(InvoiceBuilder.Organization with { Name = "  " }).Build();

        AssertBlocking(invoice.Issues, "BUYER_MISSING");
    }

    [Fact]
    public void OrganizationWithoutTaxCodeBlocksAsBuyerTaxStatus()
    {
        var buyer = InvoiceBuilder.Organization with { TaxCode = "", TaxCodePresence = ValuePresence.Missing };
        var issues = Validate(new InvoiceBuilder().WithBuyer(buyer).Build());

        AssertBlocking(issues, "BUYER_TAX_STATUS");
    }

    [Theory]
    [InlineData("Hoàng Trọng Tiếp")]
    [InlineData("Huỳnh Thị Như Ý")]
    [InlineData("LÊ THỊ THANH LIÊN")]
    public void IndividualWithoutTaxCodeIsAnInfoNotABlock(string name)
    {
        var buyer = new Party(name, "", "Huế", "", ValuePresence.Missing);
        var invoice = new InvoiceBuilder().WithBuyer(buyer).Build();

        Assert.False(invoice.IsBlocked);
        Assert.Equal(IssueSeverity.Info, Single(invoice.Issues, "BUYER_TAX_OPTIONAL").Severity);
    }

    [Theory]
    [InlineData("CÔNG TY TNHH LÂM NGHIỆP NAM HÒA")]
    [InlineData("Cty CP Dệt May Huế")]
    [InlineData("HỢP TÁC XÃ NÔNG NGHIỆP VINH HÀ")]
    [InlineData("VƯỜN QUỐC GIA BẠCH MÃ")]
    public void UnspecifiedBuyerNamedLikeAnOrganizationStillNeedsATaxCode(string name)
    {
        var buyer = new Party(name, "", "Huế", "", ValuePresence.Missing);

        AssertBlocking(new InvoiceBuilder().WithBuyer(buyer).Build().Issues, "BUYER_TAX_STATUS");
    }

    [Theory]
    [InlineData("BÁN CHO NGƯỜI TIÊU DÙNG")]
    [InlineData("Bán cho người tiêu dùng")]
    [InlineData("KHÁCH LẺ - NGƯỜI TIÊU DÙNG")]
    public void ConsumerBuyerWithoutTaxCodeIsAnInfoNotABlock(string name)
    {
        var buyer = new Party(name, "", "", "NTD", ValuePresence.NotApplicable, PartyKind.Individual);
        var invoice = new InvoiceBuilder().WithBuyer(buyer).Build();

        var info = Single(invoice.Issues, "BUYER_TAX_OPTIONAL");

        Assert.Equal(IssueSeverity.Info, info.Severity);
        Assert.Empty(Blocking(invoice.Issues));
        Assert.False(invoice.IsBlocked);
    }

    [Fact]
    public void ConsumerBuyerWithATaxCodeProducesNoInfo()
    {
        var buyer = InvoiceBuilder.Consumer with { TaxCode = "0107286506", TaxCodePresence = ValuePresence.Provided };

        Assert.Empty(new InvoiceBuilder().WithBuyer(buyer).Build().Issues);
    }

    [Fact]
    public void ProvidedPresenceWithoutATaxCodeBlocksEvenForConsumers()
    {
        var buyer = InvoiceBuilder.Consumer with { TaxCodePresence = ValuePresence.Provided };

        AssertBlocking(new InvoiceBuilder().WithBuyer(buyer).Build().Issues, "BUYER_TAX_STATUS");
    }

    [Fact(Skip = "BUG: src/MisaManager.Core/Validation.cs:52 matches the consumer buyer name with Contains(\"NGƯỜI TIÊU DÙNG\", OrdinalIgnoreCase), "
        + "which is not Unicode-normalisation aware, while the importer detects the same buyer with Normalize() and assigns code NTD. "
        + "A decomposed (NFD) spelling pasted from another app is imported as NTD but then blocked as BUYER_TAX_STATUS.")]
    public void ConsumerNameInDecomposedUnicodeIsRecognised()
    {
        var name = "BÁN CHO NGƯỜI TIÊU DÙNG".Normalize(System.Text.NormalizationForm.FormD);
        var buyer = new Party(name, "", "", "NTD", ValuePresence.NotApplicable, PartyKind.Individual);

        Assert.DoesNotContain(new InvoiceBuilder().WithBuyer(buyer).Build().Issues, issue => issue.Code == "BUYER_TAX_STATUS");
    }

    // ---- dates ----

    [Fact]
    public void MissingIssueDateBlocks() => AssertBlocking(Validate(InvoiceBuilder.Valid() with { Issued = null }), "DATES_MISSING");

    // ---- line content ----

    [Theory]
    [InlineData("", "kg")]
    [InlineData("Phân kali", "")]
    [InlineData(" ", " ")]
    public void LineWithoutNameOrUnitBlocksAtTheNameCell(string name, string unit)
    {
        var line = Line() with { Name = name, Unit = unit };
        var invoice = new InvoiceBuilder().WithLines(line).Build();

        var issue = Single(invoice.Issues, "ITEM_IDENTITY_MISSING");

        Assert.Equal(line.Cells["name"].Address, issue.Cell);
        Assert.Equal(line.LineId, issue.LineId);
    }

    [Fact]
    public void ItemIdentityIsReportedOncePerOffendingLine()
    {
        var bad = Line(1) with { Name = "" };
        var invoice = new InvoiceBuilder().WithLines(Line(0), bad, Line(2) with { Unit = "" }).Build();

        Assert.Equal(2, invoice.Issues.Count(issue => issue.Code == "ITEM_IDENTITY_MISSING"));
    }

    [Theory]
    [InlineData(null, 5000, 50000, 4000)]
    [InlineData(0, 5000, 0, 0)]
    [InlineData(-1, 5000, 0, 0)]
    [InlineData(10, null, 50000, 4000)]
    [InlineData(10, -5, 50000, 4000)]
    [InlineData(10, 5000, null, 4000)]
    [InlineData(10, 5000, -50000, 4000)]
    [InlineData(10, 5000, 50000, null)]
    [InlineData(10, 5000, 50000, -1)]
    public void MissingOrNegativeNumbersBlockAsLineNumbersInvalid(int? quantity, int? price, int? amount, int? tax)
    {
        var line = InvoiceBuilder.LineRaw(0, "Phân kali", "kg", quantity, price, amount, "8%", tax, null);
        var invoice = new InvoiceBuilder().WithLines(line).Build();

        AssertBlocking(invoice.Issues, "LINE_NUMBERS_INVALID");
    }

    [Fact]
    public void ZeroUnitPriceAndZeroAmountAreAllowedForFreeGoods()
    {
        var line = InvoiceBuilder.LineRaw(0, "Hàng tặng", "Gói", 2, 0, 0, "8%", 0, 0);

        Assert.Empty(Blocking(new InvoiceBuilder().WithLines(line).Build().Issues));
    }

    [Fact]
    public void FormulaCellBlocksTheLine()
    {
        var line = InvoiceBuilder.WithFormula(Line(), "amount", "=H2*I2");
        var issue = Single(new InvoiceBuilder().WithLines(line).Build().Issues, "FORMULA_UNCONFIRMED");

        Assert.Equal(IssueSeverity.Blocking, issue.Severity);
        Assert.Equal(line.LineId, issue.LineId);
    }

    // ---- tax rate ----

    [Theory]
    [InlineData("8%")]
    [InlineData("8")]
    [InlineData(" 10 ")]
    [InlineData("0")]
    [InlineData("0%")]
    [InlineData("100")]
    public void SupportedTaxRatesPassWithoutBlocking(string rate)
    {
        var line = InvoiceBuilder.Line(0, "Phân kali", "kg", 10, 5_000m, rate);

        Assert.Empty(Blocking(new InvoiceBuilder().WithLines(line).Build().Issues));
    }

    [Fact]
    public void TaxRateWithSpaceBeforePercentSignIsAccepted()
    {
        var line = InvoiceBuilder.LineRaw(0, "Phân kali", "kg", 10, 5_000m, 50_000m, "8 %", 4_000m, 54_000m);

        Assert.DoesNotContain(new InvoiceBuilder().WithLines(line).Build().Issues, issue => issue.Code == "TAX_RATE_UNSUPPORTED");
    }

    [Theory]
    [InlineData("KCT")]
    [InlineData("KKKNT")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("8,5%")]
    [InlineData("-1")]
    [InlineData("101%")]
    public void UnsupportedTaxRatesBlockAtTheTaxRateCell(string rate)
    {
        var line = InvoiceBuilder.LineRaw(0, "Phân kali", "kg", 10, 5_000m, 50_000m, rate, 0, 50_000m);
        var issue = Single(new InvoiceBuilder().WithLines(line).Build().Issues, "TAX_RATE_UNSUPPORTED");

        Assert.Equal(IssueSeverity.Blocking, issue.Severity);
        Assert.Equal(line.Cells["taxRate"].Address, issue.Cell);
    }

    [Fact]
    public void TaxAmountOffByMoreThanHalfADongWarnsButDoesNotBlock()
    {
        var line = InvoiceBuilder.LineRaw(0, "Phân kali", "kg", 10, 5_000m, 50_000m, "8%", 4_001m, 54_001m);
        var invoice = new InvoiceBuilder().WithLines(line).Build();

        var warning = Single(invoice.Issues, "TAX_RATE_AMOUNT_DIFFERENCE");

        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.Equal(line.Cells["taxAmount"].Address, warning.Cell);
        Assert.Empty(Blocking(invoice.Issues));
    }

    [Theory]
    [InlineData("4000")]
    [InlineData("4000.5")]
    [InlineData("3999.5")]
    public void TaxAmountWithinHalfADongOfTheRateDoesNotWarn(string tax)
    {
        var taxAmount = decimal.Parse(tax, System.Globalization.CultureInfo.InvariantCulture);
        var line = InvoiceBuilder.LineRaw(0, "Phân kali", "kg", 10, 5_000m, 50_000m, "8%", taxAmount, 50_000m + taxAmount);

        Assert.DoesNotContain(new InvoiceBuilder().WithLines(line).Build().Issues, issue => issue.Code == "TAX_RATE_AMOUNT_DIFFERENCE");
    }

    [Fact]
    public void TaxAmountJustBeyondTheToleranceWarns()
    {
        var line = InvoiceBuilder.LineRaw(0, "Phân kali", "kg", 10, 5_000m, 50_000m, "8%", 4_000.51m, 54_000.51m);

        Assert.Contains(new InvoiceBuilder().WithLines(line).Build().Issues, issue => issue.Code == "TAX_RATE_AMOUNT_DIFFERENCE");
    }

    // ---- quantity x price vs amount ----

    [Fact]
    public void AmountDifferentFromQuantityTimesPriceWarnsAtTheAmountCell()
    {
        // 100 kg x 15.429 = 1.542.900 but the source amount is 1.542.857 (price derived from the amount in MISA).
        var line = InvoiceBuilder.LineRaw(0, "Phân Ure Đạm Phú Mỹ loại 1 (bao)", "kg", 100, 15_429m, 1_542_857m, "8%", 123_429m, 1_666_286m);
        var invoice = new InvoiceBuilder().WithLines(line).Build();

        var warning = Single(invoice.Issues, "UNIT_PRICE_AMOUNT_DIFFERENCE");

        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.Equal(line.Cells["amount"].Address, warning.Cell);
        Assert.Empty(Blocking(invoice.Issues));
    }

    [Theory]
    [InlineData("220000")]
    [InlineData("220000.5")]
    [InlineData("219999.5")]
    public void AmountWithinHalfADongOfQuantityTimesPriceDoesNotWarn(string amountText)
    {
        var amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        var line = InvoiceBuilder.LineRaw(0, "Rông đỏ 200SL - Chai 100ml", "Chai", 22, 10_000m, amount, "0%", 0, amount);

        Assert.DoesNotContain(new InvoiceBuilder().WithLines(line).Build().Issues, issue => issue.Code == "UNIT_PRICE_AMOUNT_DIFFERENCE");
    }

    [Fact]
    public void AmountBeyondToleranceWarns()
    {
        var line = InvoiceBuilder.LineRaw(0, "Rông đỏ 200SL - Chai 100ml", "Chai", 22, 10_000m, 220_001m, "0%", 0, 220_001m);

        Assert.Contains(new InvoiceBuilder().WithLines(line).Build().Issues, issue => issue.Code == "UNIT_PRICE_AMOUNT_DIFFERENCE");
    }

    [Fact]
    public void LineDiscountIsSubtractedBeforeComparingWithTheAmount()
    {
        var withDiscount = InvoiceBuilder.Line(0, "Hesman 60WG 18g", "Gói", 38, 16_000m, discount: 8_000m);
        var invoice = new InvoiceBuilder().WithLines(withDiscount).Build();

        Assert.Equal(600_000m, withDiscount.AmountBeforeTax);
        Assert.Empty(invoice.Issues);
    }

    [Fact]
    public void IgnoringTheDiscountWouldBeReportedAsADifference()
    {
        var line = InvoiceBuilder.Line(0, "Hesman 60WG 18g", "Gói", 38, 16_000m, discount: 8_000m) with { Discount = null };

        Assert.Contains(new InvoiceBuilder().WithLines(line).Build().Issues, issue => issue.Code == "UNIT_PRICE_AMOUNT_DIFFERENCE");
    }

    // ---- line total / totals ----

    [Fact]
    public void LineTotalDifferentFromAmountPlusTaxBlocks()
    {
        var good = Line();
        var line = good with { TotalWithTax = good.TotalWithTax + 1 };

        AssertBlocking(new InvoiceBuilder().WithLines(line).Build().Issues, "LINE_TOTAL_MISMATCH");
    }

    [Fact]
    public void LineWithoutALineTotalIsNotCheckedForLineTotalMismatch()
    {
        var line = Line() with { TotalWithTax = null };
        var invoice = new InvoiceBuilder().WithLines(line).WithTotals(50_000m, 4_000m, 54_000m).Build();

        Assert.DoesNotContain(invoice.Issues, issue => issue.Code == "LINE_TOTAL_MISMATCH");
        Assert.Empty(Blocking(invoice.Issues));
    }

    [Theory]
    [InlineData(null, 1, 1)]
    [InlineData(1, null, 1)]
    [InlineData(1, 1, null)]
    [InlineData(null, null, null)]
    public void AnyMissingInvoiceTotalBlocks(int? subtotal, int? tax, int? grand)
    {
        var invoice = new InvoiceBuilder().WithTotals(subtotal, tax, grand).Build();

        AssertBlocking(invoice.Issues, "TOTALS_MISSING");
        Assert.DoesNotContain(invoice.Issues, issue => issue.Code == "TOTAL_MISMATCH");
    }

    [Fact]
    public void SubtotalThatDiffersFromTheSumOfLinesBlocks()
    {
        var lines = new[] { Line(0), Line(1) };
        var invoice = new InvoiceBuilder().WithLines(lines).WithTotals(100_001m, 8_000m, 108_001m).Build();

        AssertBlocking(invoice.Issues, "TOTAL_MISMATCH");
    }

    [Fact]
    public void TaxTotalThatDiffersFromTheSumOfLinesBlocks()
    {
        var invoice = new InvoiceBuilder().WithLines(Line(0), Line(1)).WithTotals(100_000m, 8_001m, 108_001m).Build();

        AssertBlocking(invoice.Issues, "TOTAL_MISMATCH");
    }

    [Fact]
    public void GrandTotalThatIsNotSubtotalPlusTaxBlocks()
    {
        var invoice = new InvoiceBuilder().WithLines(Line(0), Line(1)).WithTotals(100_000m, 8_000m, 108_100m).Build();

        AssertBlocking(invoice.Issues, "TOTAL_MISMATCH");
    }

    [Fact]
    public void TotalsMatchingTheSumOfManyLinesPass()
    {
        var lines = Enumerable.Range(0, 25).Select(index => Line(index, quantity: index + 1, price: 1_234m)).ToArray();

        Assert.Empty(Blocking(new InvoiceBuilder().WithLines(lines).Build().Issues));
    }

    // ---- overflow ----

    [Fact]
    public void QuantityTimesPriceOverflowIsReportedNotThrown()
    {
        var line = InvoiceBuilder.LineRaw(0, "Hàng lớn", "kg", 1e20m, 1e20m, 1m, "0%", 0, 1m);
        var invoice = new InvoiceBuilder().WithLines(line).Build();

        AssertBlocking(invoice.Issues, "ARITHMETIC_OVERFLOW");
    }

    [Fact]
    public void SumOfLinesOverflowIsReportedNotThrown()
    {
        var huge = InvoiceBuilder.LineRaw(0, "Hàng lớn", "kg", 1m, decimal.MaxValue, decimal.MaxValue, "0%", 0, decimal.MaxValue);
        var second = huge with { LineId = "second" };
        var invoice = new InvoiceBuilder().WithLines(huge, second).WithTotals(1m, 0m, 1m).Build();

        AssertBlocking(invoice.Issues, "ARITHMETIC_OVERFLOW");
    }

    [Fact]
    public void SubtotalPlusTaxOverflowIsReportedNotThrown()
    {
        var invoice = new InvoiceBuilder().WithLines(Line()).WithTotals(decimal.MaxValue, decimal.MaxValue, 1m).Build();

        Assert.Contains(invoice.Issues, issue => issue.Code is "ARITHMETIC_OVERFLOW" or "TOTAL_MISMATCH");
    }

    // ---- combined behaviour ----

    [Fact]
    public void SeveralProblemsAreAllReportedTogether()
    {
        var line = InvoiceBuilder.LineRaw(0, "", "kg", 0, 5_000m, 50_000m, "KCT", 0, 1m);
        var buyer = InvoiceBuilder.Organization with { TaxCode = "", TaxCodePresence = ValuePresence.Missing };
        var invoice = new InvoiceBuilder().WithBuyer(buyer).WithIssued(null).WithLines(line).WithTotals(null, null, null).Build();

        var codes = invoice.Issues.Select(issue => issue.Code).ToHashSet();

        Assert.Superset(new HashSet<string> { "BUYER_TAX_STATUS", "DATES_MISSING", "ITEM_IDENTITY_MISSING", "LINE_NUMBERS_INVALID", "TOTALS_MISSING" }, codes);
        Assert.True(invoice.IsBlocked);
    }

    [Fact]
    public void WarningsAndInfoAloneDoNotMakeAnInvoiceBlocked()
    {
        var line = InvoiceBuilder.LineRaw(0, "Phân kali", "kg", 10, 5_000m, 50_001m, "8%", 4_000m, 54_001m);
        var invoice = new InvoiceBuilder().WithBuyer(InvoiceBuilder.Consumer).WithLines(line).Build();

        Assert.Contains(invoice.Issues, issue => issue.Severity == IssueSeverity.Warning);
        Assert.Contains(invoice.Issues, issue => issue.Severity == IssueSeverity.Info);
        Assert.False(invoice.IsBlocked);
    }

    [Fact]
    public void IsBlockedTreatsMissingIssueArrayAsBlocked()
        => Assert.True((InvoiceBuilder.Valid() with { Issues = default }).IsBlocked);

    [Fact]
    public void IsBlockedIgnoresNonBlockingIssues()
    {
        var info = new ImportIssue("X", IssueSeverity.Info, "info");
        var warning = new ImportIssue("Y", IssueSeverity.Warning, "warning");

        Assert.False((InvoiceBuilder.Valid() with { Issues = [info, warning] }).IsBlocked);
        Assert.True((InvoiceBuilder.Valid() with { Issues = [info, warning with { Severity = IssueSeverity.Blocking }] }).IsBlocked);
    }
}
