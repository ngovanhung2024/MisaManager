using System.Collections.Immutable;
using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class InvoiceCorrectionTests : IDisposable
{
    private const string Confirmation = "Người dùng xác nhận sửa dữ liệu";
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static InvoiceCorrection Fix(InvoiceDocument invoice, string field, string value, string? confirmation = Confirmation, string provenance = "user_override")
        => new(invoice.InvoiceId, invoice.Revision, field, value, provenance, confirmation);

    private static InvoiceCorrectionResult Apply(InvoiceDocument invoice, string field, string value)
        => InvoiceCorrectionService.Apply(invoice, Fix(invoice, field, value));

    private static InvoiceDocument WithoutBuyerTaxCode()
        => new InvoiceBuilder().WithBuyer(InvoiceBuilder.Organization with { TaxCode = "", TaxCodePresence = ValuePresence.Missing }).Build();

    // ---- preconditions ----

    [Theory]
    [InlineData("system")]
    [InlineData("")]
    [InlineData("User_Override")]
    public void OnlyUserOverrideProvenanceIsAccepted(string provenance)
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Throws<InvalidDataException>(() => InvoiceCorrectionService.Apply(invoice, Fix(invoice, "notes", "x", provenance: provenance)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExplicitConfirmationIsRequired(string? confirmation)
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Throws<InvalidDataException>(() => InvoiceCorrectionService.Apply(invoice, Fix(invoice, "notes", "x", confirmation)));
    }

    [Fact]
    public void StaleRevisionIsRejected()
    {
        var invoice = InvoiceBuilder.Valid();
        var correction = Fix(invoice, "notes", "x") with { ExpectedRevision = invoice.Revision + 1 };

        Assert.Throws<InvalidOperationException>(() => InvoiceCorrectionService.Apply(invoice, correction));
    }

    [Fact]
    public void CorrectionForAnotherInvoiceIsRejected()
    {
        var invoice = InvoiceBuilder.Valid();
        var correction = Fix(invoice, "notes", "x") with { InvoiceId = "ffffffffffffffffffffffff" };

        Assert.Throws<InvalidOperationException>(() => InvoiceCorrectionService.Apply(invoice, correction));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFieldIsRejected(string field)
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Throws<ArgumentException>(() => InvoiceCorrectionService.Apply(invoice, Fix(invoice, field, "x")));
    }

    [Theory]
    [InlineData("dòng 1\nxuống dòng")]
    [InlineData("tab\there")]
    [InlineData("nul\0")]
    public void ControlCharactersInTheValueAreRejected(string value)
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Throws<ArgumentException>(() => InvoiceCorrectionService.Apply(invoice, Fix(invoice, "notes", value)));
    }

    [Fact]
    public void OverlongValuesAreRejected()
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Throws<ArgumentException>(() => InvoiceCorrectionService.Apply(invoice, Fix(invoice, "notes", new string('x', 32769))));
    }

    [Theory]
    [InlineData("buyer.nickname")]
    [InlineData("Notes")]
    [InlineData("lines[0]name")]
    [InlineData("lines[].name")]
    [InlineData("lines[x].name")]
    [InlineData("lines[-1].name")]
    [InlineData("lines[2].name")]
    [InlineData("lines[0].code")]
    [InlineData("lines[0].")]
    public void UnknownFieldsAndBadLineReferencesAreRejected(string field)
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Throws<InvalidDataException>(() => InvoiceCorrectionService.Apply(invoice, Fix(invoice, field, "1")));
    }

    [Fact]
    public void CorrectionThatChangesNothingIsRejected()
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Throws<InvalidDataException>(() => Apply(invoice, "buyer.name", invoice.Buyer.Name));
        Assert.Throws<InvalidDataException>(() => Apply(invoice, "lines[0].unit", invoice.Lines[0].Unit));
        Assert.Throws<InvalidDataException>(() => Apply(invoice, "subtotal", invoice.Subtotal!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmedBeforeComparingAndStoring()
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Throws<InvalidDataException>(() => Apply(invoice, "buyer.name", "  " + invoice.Buyer.Name + "  "));
        Assert.Equal("Nguyễn Văn An", Apply(invoice, "buyer.name", "  Nguyễn Văn An ").Invoice.Buyer.Name);
    }

    // ---- applying corrections ----

    [Fact]
    public void BuyerNameCorrectionUpdatesTheInvoiceAndReportsTheChange()
    {
        var invoice = InvoiceBuilder.Valid();

        var result = Apply(invoice, "buyer.name", "CÔNG TY TNHH NÔNG NGHIỆP XANH");

        Assert.Equal("CÔNG TY TNHH NÔNG NGHIỆP XANH", result.Invoice.Buyer.Name);
        var change = Assert.Single(result.Changes);
        Assert.Equal(new CorrectionChange("buyer.name", invoice.Buyer.Name, "CÔNG TY TNHH NÔNG NGHIỆP XANH", "user_override"), change);
        Assert.Equal(InvoiceBuilder.Organization.Name, invoice.Buyer.Name); // the original document is untouched
    }

    [Fact]
    public void CorrectionRaisesTheRevisionByOne()
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Equal(invoice.Revision + 1, Apply(invoice, "notes", "Giao hàng buổi sáng").Invoice.Revision);
    }

    [Fact]
    public void CorrectionIsRecordedInTheAuditTrail()
    {
        var invoice = InvoiceBuilder.Valid();
        var before = DateTimeOffset.UtcNow;

        var result = Apply(invoice, "notes", "Giao hàng buổi sáng");

        var audit = Assert.Single(result.Invoice.Corrections);
        Assert.Equal(2, audit.Revision);
        Assert.Equal(Confirmation, audit.Confirmation);
        Assert.InRange(audit.OccurredAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("notes", Assert.Single(audit.Changes).Field);
    }

    [Fact]
    public void SuccessiveCorrectionsAccumulateAudits()
    {
        var first = Apply(InvoiceBuilder.Valid(), "notes", "Giao hàng buổi sáng").Invoice;
        var second = Apply(first, "paymentMethod", "Chuyển khoản").Invoice;

        Assert.Equal(3, second.Revision);
        Assert.Equal([2, 3], second.Corrections.Select(audit => audit.Revision));
        Assert.Equal("Chuyển khoản", second.PaymentMethod);
        Assert.Equal("Giao hàng buổi sáng", second.Notes);
    }

    [Fact]
    public void CorrectionStoresAUserOverrideCellForTheHeaderField()
    {
        var result = Apply(InvoiceBuilder.Valid(), "buyer.address", "Số 5, Lê Lợi, Huế");

        var cell = result.Invoice.HeaderCells!["buyer.address"];
        Assert.Equal("Số 5, Lê Lợi, Huế", cell.ParsedValue);
        Assert.Equal("user_override", cell.ValueOrigin);
        Assert.Equal(Confirmation, cell.Confirmation);
        Assert.Null(cell.Formula);
        Assert.Contains(result.Invoice.Cells, item => item.Address == "override:buyer.address");
    }

    [Fact]
    public void TaxCodeCorrectionMarksThePresenceAsProvidedAndUnblocksTheInvoice()
    {
        var invoice = WithoutBuyerTaxCode();
        Assert.True(invoice.IsBlocked);

        var result = Apply(invoice, "buyer.taxCode", "0107286506");

        Assert.Equal("0107286506", result.Invoice.Buyer.TaxCode);
        Assert.Equal(ValuePresence.Provided, result.Invoice.Buyer.TaxCodePresence);
        Assert.False(result.Invoice.IsBlocked);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "BUYER_TAX_STATUS");
    }

    [Fact]
    public void SellerTaxCodeCorrectionAlsoUpdatesTheInvoiceIdentity()
    {
        var result = Apply(InvoiceBuilder.Valid(), "seller.taxCode", "3300123457");

        Assert.Equal("3300123457", result.Invoice.Seller.TaxCode);
        Assert.Equal("3300123457", result.Invoice.Identity.SellerTaxCode);
    }

    [Fact]
    public void SeriesAndNumberCorrectionsUpdateTheIdentity()
    {
        var invoice = InvoiceBuilder.Valid();

        var series = Apply(invoice, "series", "1C26MLW").Invoice;
        var number = Apply(invoice, "number", "1005").Invoice;

        Assert.Equal("1C26MLW", series.Identity.Series);
        Assert.Equal("1005", number.Identity.Number);
        Assert.Equal(invoice.InvoiceId, number.InvoiceId);
    }

    [Fact]
    public void IssueDateCorrectionUpdatesBothTheInvoiceAndItsIdentity()
    {
        var result = Apply(InvoiceBuilder.Valid(), "issued", "2026-10-05").Invoice;

        Assert.Equal(new DateOnly(2026, 10, 5), result.Issued);
        Assert.Equal(new DateOnly(2026, 10, 5), result.Identity.Issued);
    }

    [Fact]
    public void MissingIssueDateCanBeSuppliedByCorrection()
    {
        var invoice = new InvoiceBuilder().WithIssued(null).Build();
        Assert.Contains(invoice.Issues, issue => issue.Code == "DATES_MISSING");

        var result = Apply(invoice, "issued", "2026-10-04");

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "DATES_MISSING");
    }

    [Theory]
    [InlineData("05/10/2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-10-5")]
    [InlineData("hôm nay")]
    public void DatesMustBeIsoFormatted(string value)
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Throws<InvalidDataException>(() => Apply(invoice, "issued", value));
        Assert.Throws<InvalidDataException>(() => Apply(invoice, "voucherDate", value));
        Assert.Throws<InvalidDataException>(() => Apply(invoice, "postedDate", value));
    }

    [Fact]
    public void VoucherAndPostedDatesCanBeCorrected()
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Equal(new DateOnly(2026, 10, 6), Apply(invoice, "voucherDate", "2026-10-06").Invoice.VoucherDate);
        Assert.Equal(new DateOnly(2026, 10, 7), Apply(invoice, "postedDate", "2026-10-07").Invoice.PostedDate);
    }

    [Fact(Skip = "BUG: src/MisaManager.Core/InvoiceCorrection.cs:61-64 (voucherDate/postedDate) always records a change and bumps the revision, even when "
        + "the value is unchanged; every other field rejects a no-op with 'Correction made no change.'.")]
    public void VoucherDateCorrectionWithTheSameValueIsRejectedLikeOtherFields()
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Throws<InvalidDataException>(() => Apply(invoice, "voucherDate", invoice.VoucherDate!.Value.ToString("yyyy-MM-dd")));
    }

    [Theory]
    [InlineData("individual", PartyKind.Individual)]
    [InlineData("Organization", PartyKind.Organization)]
    [InlineData("UNSPECIFIED", PartyKind.Unspecified)]
    public void BuyerKindIsParsedCaseInsensitively(string value, PartyKind expected)
    {
        var other = expected == PartyKind.Organization ? PartyKind.Individual : PartyKind.Organization;
        var invoice = new InvoiceBuilder().WithBuyer(InvoiceBuilder.Organization with { Kind = other }).Build();

        Assert.Equal(expected, Apply(invoice, "buyer.kind", value).Invoice.Buyer.Kind);
    }

    [Theory]
    [InlineData("buyer.kind", "Company")]
    [InlineData("buyer.kind", "42")]
    [InlineData("buyer.taxPresence", "Maybe")]
    [InlineData("buyer.taxPresence", "99")]
    public void UndefinedEnumValuesAreRejected(string field, string value)
        => Assert.Throws<InvalidDataException>(() => Apply(InvoiceBuilder.Valid(), field, value));

    [Fact]
    public void TaxPresenceCanBeSetToNotApplicable()
    {
        var result = Apply(InvoiceBuilder.Valid(), "buyer.taxPresence", "notapplicable");

        Assert.Equal(ValuePresence.NotApplicable, result.Invoice.Buyer.TaxCodePresence);
    }

    [Theory]
    [InlineData("subtotal", "1,5")]
    [InlineData("subtotal", "1e3")]
    [InlineData("subtotal", "abc")]
    [InlineData("subtotal", "0.123456789")]
    [InlineData("grandTotal", "1.000.000")]
    [InlineData("exchangeRate", " ")]
    [InlineData("lines[0].quantity", "1,5")]
    [InlineData("lines[0].amount", "")]
    public void NumbersMustBeInvariantDecimals(string field, string value)
        => Assert.Throws<InvalidDataException>(() => Apply(InvoiceBuilder.Valid(), field, value));

    [Fact]
    public void ExactDecimalScaleIsKept()
    {
        var result = Apply(InvoiceBuilder.Valid(), "exchangeRate", "23456.12345678");

        Assert.Equal(23456.12345678m, result.Invoice.ExchangeRate);
    }

    // ---- line corrections ----

    [Fact]
    public void LineQuantityCorrectionUpdatesOnlyThatLineAndKeepsTheOthers()
    {
        var invoice = InvoiceBuilder.Valid();

        var result = Apply(invoice, "lines[1].quantity", "40");

        Assert.Equal(40m, result.Invoice.Lines[1].Quantity);
        Assert.Equal(invoice.Lines[0], result.Invoice.Lines[0]);
        Assert.Equal("38", Assert.Single(result.Changes).Before);
    }

    [Fact]
    public void LineCorrectionOverridesTheLineCellAndKeepsHeaderCellsUntouched()
    {
        var invoice = InvoiceBuilder.Valid();

        var result = Apply(invoice, "lines[0].name", "Rông đỏ 200SL - Chai 100ml (mới)");

        var cell = result.Invoice.Lines[0].Cells["name"];
        Assert.Equal("user_override", cell.ValueOrigin);
        Assert.Equal("Rông đỏ 200SL - Chai 100ml (mới)", cell.ParsedValue);
        Assert.Equal(Confirmation, cell.Confirmation);
        Assert.Equal(invoice.Lines[0].Cells["name"].Address, cell.Address);
        Assert.Equal(invoice.HeaderCells, result.Invoice.HeaderCells);
    }

    [Fact]
    public void LineCorrectionRemovesAFormulaFlagFromTheOverriddenCell()
    {
        var withFormula = InvoiceBuilder.WithFormula(InvoiceBuilder.Line(0, "Phân kali", "kg", 10, 5_000m), "amount", "=H2*I2");
        var invoice = new InvoiceBuilder().WithLines(withFormula).Build();
        Assert.Contains(invoice.Issues, issue => issue.Code == "FORMULA_UNCONFIRMED");

        var result = Apply(invoice, "lines[0].amount", "49999");

        Assert.False(result.Invoice.Lines[0].HasFormula);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "FORMULA_UNCONFIRMED");
    }

    [Fact]
    public void LineCorrectionTextFieldsAreStoredVerbatim()
    {
        var result = Apply(InvoiceBuilder.Valid(), "lines[0].taxRate", "10%").Invoice;

        Assert.Equal("10%", result.Lines[0].TaxRate);
    }

    [Fact]
    public void QuantityChangeIsRevalidatedAndSurfacesAWarning()
    {
        var result = Apply(InvoiceBuilder.Valid(), "lines[0].quantity", "23");

        var warning = Assert.Single(result.Issues, issue => issue.Code == "UNIT_PRICE_AMOUNT_DIFFERENCE");
        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.False(result.Invoice.IsBlocked);
    }

    [Fact]
    public void AmountChangeThatBreaksTheTotalsBlocksTheInvoice()
    {
        var result = Apply(InvoiceBuilder.Valid(), "lines[0].amount", "1");

        Assert.Contains(result.Issues, issue => issue.Code == "TOTAL_MISMATCH" && issue.Severity == IssueSeverity.Blocking);
        Assert.True(result.Invoice.IsBlocked);
        Assert.Equal(result.Issues, result.Invoice.Issues);
    }

    [Fact]
    public void FixingTheTotalsAfterAnAmountCorrectionClearsTheBlock()
    {
        var invoice = new InvoiceBuilder().WithLines(InvoiceBuilder.Line(0, "Phân kali", "kg", 10, 5_000m)).Build();

        var current = Apply(invoice, "lines[0].amount", "50001").Invoice;
        Assert.True(current.IsBlocked);
        foreach (var (field, value) in new[] { ("lines[0].lineTotal", "54001"), ("subtotal", "50001"), ("grandTotal", "54001") })
            current = Apply(current, field, value).Invoice;

        Assert.False(current.IsBlocked);
        Assert.Equal(5, current.Revision);
    }

    [Fact]
    public void SourceParserIssuesSurviveCorrections()
    {
        var parserIssue = new ImportIssue("NUMBER_INVALID", IssueSeverity.Blocking, "Giá trị số không hợp lệ.", "Hóa đơn", "I2");
        var invoice = InvoiceBuilder.Valid() with { SourceIssues = [parserIssue], Issues = [parserIssue] };

        var result = Apply(invoice, "notes", "Ghi chú mới");

        Assert.Contains(result.Issues, issue => issue.Code == "NUMBER_INVALID");
        Assert.True(result.Invoice.IsBlocked);
    }

    [Fact]
    public void IssuesAreStampedWithTheInvoiceId()
    {
        var parserIssue = new ImportIssue("X", IssueSeverity.Warning, "x", InvoiceId: "other");
        var invoice = InvoiceBuilder.Valid() with { SourceIssues = [parserIssue] };

        Assert.All(Apply(invoice, "notes", "n").Issues, issue => Assert.Equal(invoice.InvoiceId, issue.InvoiceId));
    }

    // ---- field catalogue ----

    public static TheoryData<string, string> HeaderFieldSamples => new()
    {
        { "buyer.code", "KH01" }, { "buyer.name", "Nguyễn Văn An" }, { "buyer.taxCode", "0101234567" }, { "buyer.taxPresence", "Missing" },
        { "buyer.kind", "Individual" }, { "buyer.address", "Số 5, Lê Lợi, Huế" }, { "seller.name", "CÔNG TY TNHH LÁ VIỆT" },
        { "seller.taxCode", "3300123457" }, { "series", "1C26MLW" }, { "number", "1002" }, { "template", "1" },
        { "issued", "2026-10-05" }, { "voucherDate", "2026-10-06" }, { "postedDate", "2026-10-07" }, { "currency", "USD" },
        { "paymentMethod", "Chuyển khoản" }, { "invoiceType", "Hóa đơn GTGT" }, { "exchangeRate", "25000" },
        { "subtotal", "1" }, { "taxTotal", "1" }, { "grandTotal", "1" }, { "notes", "Ghi chú" }
    };

    [Theory]
    [MemberData(nameof(HeaderFieldSamples))]
    public void EveryDocumentedHeaderFieldCanBeCorrected(string field, string value)
    {
        var result = Apply(InvoiceBuilder.Valid(), field, value);

        Assert.Equal(field, Assert.Single(result.Changes).Field);
        Assert.Equal(value, result.Changes[0].After);
    }

    [Fact]
    public void HeaderFieldSamplesCoverTheWholeCatalogue()
        => Assert.Equal(InvoiceCorrectionService.HeaderFields.Order(), HeaderFieldSamples.Select(row => row.Data.Item1).Order());

    public static TheoryData<string, string> LineFieldSamples => new()
    {
        { "name", "Tên mới" }, { "unit", "Thùng" }, { "quantity", "11" }, { "unitPrice", "9000" }, { "amount", "199000" },
        { "taxRate", "10%" }, { "taxAmount", "17" }, { "lineTotal", "237000" }, { "discount", "100" }, { "discountRate", "5" }, { "kind", "Khuyến mại" }
    };

    [Theory]
    [MemberData(nameof(LineFieldSamples))]
    public void EveryDocumentedLineFieldCanBeCorrected(string lineField, string value)
    {
        var result = Apply(InvoiceBuilder.Valid(), $"lines[0].{lineField}", value);

        Assert.Equal($"lines[0].{lineField}", Assert.Single(result.Changes).Field);
    }

    [Fact]
    public void LineFieldSamplesCoverTheWholeCatalogue()
        => Assert.Equal(InvoiceCorrectionService.LineFields.Order(), LineFieldSamples.Select(row => row.Data.Item1).Order());

    // ---- fingerprint ----

    [Fact]
    public void FingerprintIsA64CharLowerCaseSha256()
    {
        var fingerprint = InvoiceFingerprint.Compute(InvoiceBuilder.Valid());

        Assert.Equal(64, fingerprint.Length);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Fact]
    public void FingerprintIsRepeatableForTheSameInvoice()
    {
        var invoice = InvoiceBuilder.Valid();

        Assert.Equal(InvoiceFingerprint.Compute(invoice), InvoiceFingerprint.Compute(invoice));
        Assert.Equal(InvoiceFingerprint.Compute(invoice), InvoiceFingerprint.Compute(InvoiceBuilder.Valid()));
        Assert.Equal(invoice.Fingerprint, InvoiceFingerprint.Compute(invoice));
    }

    [Fact]
    public void FingerprintIgnoresIssuesCorrectionsAndTheStoredFingerprint()
    {
        var invoice = InvoiceBuilder.Valid();
        var noise = invoice with
        {
            Fingerprint = "stale",
            Issues = [new ImportIssue("X", IssueSeverity.Warning, "x")],
            SourceIssues = [new ImportIssue("Y", IssueSeverity.Info, "y")],
            Corrections = [new CorrectionAudit(2, DateTimeOffset.UtcNow, "c", [])]
        };

        Assert.Equal(InvoiceFingerprint.Compute(invoice), InvoiceFingerprint.Compute(noise));
    }

    [Fact]
    public void FingerprintChangesWithBusinessContent()
    {
        var invoice = InvoiceBuilder.Valid();
        var baseline = InvoiceFingerprint.Compute(invoice);

        Assert.NotEqual(baseline, InvoiceFingerprint.Compute(invoice with { Buyer = invoice.Buyer with { Name = "Khác" } }));
        Assert.NotEqual(baseline, InvoiceFingerprint.Compute(invoice with { GrandTotal = invoice.GrandTotal + 1 }));
        Assert.NotEqual(baseline, InvoiceFingerprint.Compute(invoice with { Revision = 2 }));
        Assert.NotEqual(baseline, InvoiceFingerprint.Compute(invoice with { Lines = invoice.Lines.RemoveAt(1) }));
        Assert.NotEqual(baseline, InvoiceFingerprint.Compute(invoice with { Issued = invoice.Issued!.Value.AddDays(1) }));
    }

    [Fact]
    public void FingerprintDistinguishesDecimalScaleOfTheSameValue()
    {
        var invoice = InvoiceBuilder.Valid();

        // The contract writes decimals as exact strings, so 100 and 100.00 are different source texts.
        Assert.NotEqual(InvoiceFingerprint.Compute(invoice with { Subtotal = 100m }), InvoiceFingerprint.Compute(invoice with { Subtotal = 100.00m }));
    }

    [Fact]
    public void CorrectionStoresTheFingerprintOfTheCorrectedInvoice()
    {
        var invoice = InvoiceBuilder.Valid();

        var result = Apply(invoice, "buyer.name", "Nguyễn Văn An").Invoice;

        Assert.NotEqual(invoice.Fingerprint, result.Fingerprint);
        Assert.Equal(InvoiceFingerprint.Compute(result), result.Fingerprint);
    }

    [Fact(Skip = "BUG: src/MisaManager.Core/InvoiceCorrection.cs:143 (InvoiceFingerprint.Compute) serialises ImmutableDictionary<string, SourceCell> "
        + "(InvoiceLine.Cells, HeaderCells) in enumeration order, which depends on the per-process randomised string hash, so the same invoice gets a "
        + "different fingerprint in every process (verified: three runs of the same invoice gave three different SHA-256 values). "
        + "InvoiceDraftStore.ReadAsync then rejects every saved draft after an app restart ('Saved draft revision/hash is invalid').")]
    public void FingerprintDoesNotDependOnDictionaryEnumerationOrder()
    {
        var invoice = InvoiceBuilder.Valid();

        // A different hash seed (another process) is simulated with comparers that order the same keys differently.
        InvoiceDocument Reordered(int seed) => invoice with
        {
            Lines = [.. invoice.Lines.Select(line => line with
            {
                Cells = ImmutableDictionary.CreateRange(new SeededComparer(seed), line.Cells)
            })]
        };

        Assert.Equal(InvoiceFingerprint.Compute(Reordered(1)), InvoiceFingerprint.Compute(Reordered(7919)));
    }

    private sealed class SeededComparer(int seed) : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);
        public int GetHashCode(string value) => value.Aggregate(seed, (hash, character) => unchecked(hash * 31 + character));
    }

    // ---- draft store ----

    private InvoiceDraftStore NewDrafts(out JsonStateStore state)
    {
        state = new JsonStateStore(temp.Root);
        return new InvoiceDraftStore(state);
    }

    [Fact]
    public async Task ReadWithoutASavedDraftReturnsTheSourceInvoice()
    {
        var drafts = NewDrafts(out _);
        var source = InvoiceBuilder.Valid();

        Assert.Same(source, await drafts.ReadAsync(source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CorrectionIsPersistedAndReadBack()
    {
        var drafts = NewDrafts(out _);
        var source = InvoiceBuilder.Valid();

        var result = await drafts.CorrectAsync(source, Fix(source, "buyer.name", "Nguyễn Văn An"), TestContext.Current.CancellationToken);
        var reread = await drafts.ReadAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(2, reread.Revision);
        Assert.Equal("Nguyễn Văn An", reread.Buyer.Name);
        Assert.Equal(result.Invoice.Fingerprint, reread.Fingerprint);
        Assert.Equal(Assert.Single(result.Invoice.Corrections).Changes, Assert.Single(reread.Corrections).Changes);
        Assert.True(File.Exists(temp.Combine("drafts", InvoiceBuilder.InvoiceId + ".json")));
    }

    [Fact]
    public async Task SavedDraftSurvivesANewStoreInstance()
    {
        var source = InvoiceBuilder.Valid();
        await NewDrafts(out _).CorrectAsync(source, Fix(source, "notes", "Giao sáng"), TestContext.Current.CancellationToken);

        var reread = await NewDrafts(out _).ReadAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal("Giao sáng", reread.Notes);
    }

    [Fact]
    public async Task SecondCorrectionBuildsOnTheSavedDraft()
    {
        var drafts = NewDrafts(out _);
        var source = InvoiceBuilder.Valid();

        await drafts.CorrectAsync(source, Fix(source, "notes", "Giao sáng"), TestContext.Current.CancellationToken);
        var second = await drafts.CorrectAsync(source, Fix(source, "paymentMethod", "Chuyển khoản") with { ExpectedRevision = 2 }, TestContext.Current.CancellationToken);

        Assert.Equal(3, second.Invoice.Revision);
        Assert.Equal("Giao sáng", second.Invoice.Notes);
        Assert.Equal(2, second.Invoice.Corrections.Length);
    }

    [Fact]
    public async Task CorrectionAgainstAnOutdatedRevisionIsRejectedAfterTheDraftMoved()
    {
        var drafts = NewDrafts(out _);
        var source = InvoiceBuilder.Valid();
        await drafts.CorrectAsync(source, Fix(source, "notes", "Giao sáng"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => drafts.CorrectAsync(source, Fix(source, "paymentMethod", "Chuyển khoản"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RaceBetweenTwoCorrectionsOfTheSameRevisionLetsExactlyOneWin()
    {
        var drafts = NewDrafts(out _);
        var source = InvoiceBuilder.Valid();

        var attempts = new[] { "Giao sáng", "Giao chiều" }.Select(note => Task.Run(async () =>
        {
            try { await drafts.CorrectAsync(source, Fix(source, "notes", note), TestContext.Current.CancellationToken); return true; }
            catch (InvalidOperationException) { return false; }
        }, TestContext.Current.CancellationToken)).ToArray();

        Assert.Single(await Task.WhenAll(attempts), won => won);
        Assert.Equal(2, (await drafts.ReadAsync(source, TestContext.Current.CancellationToken)).Revision);
    }

    [Fact]
    public async Task DraftFromAnotherImportOfTheWorkbookIsIgnored()
    {
        var drafts = NewDrafts(out _);
        var source = InvoiceBuilder.Valid();
        await drafts.CorrectAsync(source, Fix(source, "notes", "Giao sáng"), TestContext.Current.CancellationToken);

        var otherSource = source with { Source = new SourceReference(source.Source.FilePath, new string('b', 64), source.Source.Worksheet, 1, 3) };
        var reimportedRevision = source with { Source = new SourceReference(source.Source.FilePath, source.Source.Sha256, source.Source.Worksheet, 1, 3, importRevision: 2) };

        Assert.Same(otherSource, await drafts.ReadAsync(otherSource, TestContext.Current.CancellationToken));
        Assert.Same(reimportedRevision, await drafts.ReadAsync(reimportedRevision, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DraftWithATamperedFingerprintIsRejected()
    {
        var drafts = NewDrafts(out var state);
        var source = InvoiceBuilder.Valid();
        var corrected = (await drafts.CorrectAsync(source, Fix(source, "notes", "Giao sáng"), TestContext.Current.CancellationToken)).Invoice;
        await state.WriteAsync("invoice-draft", $"drafts/{source.InvoiceId}.json", corrected with { Notes = "Sửa tay ngoài công cụ" }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => drafts.ReadAsync(source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DraftOlderThanTheSourceRevisionIsRejected()
    {
        var drafts = NewDrafts(out _);
        var source = InvoiceBuilder.Valid();
        await drafts.CorrectAsync(source, Fix(source, "notes", "Giao sáng"), TestContext.Current.CancellationToken);

        var newerSource = source with { Revision = 5 };

        await Assert.ThrowsAsync<InvalidDataException>(() => drafts.ReadAsync(newerSource, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("../../escapexxxxxxxxxxxxxxxxx")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task InvoiceIdsThatAreNotTwentyFourHexCharactersAreRejected(string id)
    {
        var drafts = NewDrafts(out _);
        var source = InvoiceBuilder.Valid() with { InvoiceId = id };

        await Assert.ThrowsAsync<InvalidDataException>(() => drafts.ReadAsync(source, TestContext.Current.CancellationToken));
    }
}
