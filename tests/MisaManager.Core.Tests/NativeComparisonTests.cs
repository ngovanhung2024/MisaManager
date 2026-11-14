using System.Globalization;

namespace MisaManager.Core.Tests;

public sealed class NativeComparisonTests
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static bool Same(string expected, string? actual, NativeValueKind kind, CultureInfo? culture = null)
        => NativeComparison.Matches(expected, actual, kind, culture ?? Vi);

    // ---- tax rate ----

    [Theory]
    [InlineData("5", "5")]
    [InlineData("5", "5%")]
    [InlineData("5%", "5,0000")]
    [InlineData("5", "5,0000 %")]
    [InlineData("5%", " 5 % ")]
    [InlineData("8", "8,00")]
    [InlineData("10%", "10")]
    [InlineData("0", "0%")]
    [InlineData("0", "0,0000")]
    [InlineData("5.5%", "5,5")]
    public void EquivalentTaxRateSpellingsMatch(string expected, string actual)
        => Assert.True(Same(expected, actual, NativeValueKind.TaxRate));

    [Theory]
    [InlineData("5", "8")]
    [InlineData("8", "10")]
    [InlineData("8", "0")]
    [InlineData("5.5", "5")]
    [InlineData("KCT", "0")]
    [InlineData("KCT", "KKKNT")]
    [InlineData("0", "KCT")]
    public void DifferentTaxRatesDoNotMatch(string expected, string actual)
        => Assert.False(Same(expected, actual, NativeValueKind.TaxRate));

    [Theory]
    [InlineData("KCT", "kct")]
    [InlineData("KKKNT", "KKKNT")]
    [InlineData("Không chịu thuế", "KHÔNG CHỊU THUẾ")]
    public void TextualTaxRatesMatchIgnoringCase(string expected, string actual)
        => Assert.True(Same(expected, actual, NativeValueKind.TaxRate));

    [Theory]
    [InlineData("5", "5")]
    [InlineData("5%", "5")]
    [InlineData("5,0000", "5")]
    [InlineData("5.0", "5")]
    [InlineData("0.0000", "0")]
    [InlineData(" 10 % ", "10")]
    [InlineData("5,5%", "5.5")]
    [InlineData("KCT", "KCT")]
    [InlineData("kkknt", "KKKNT")]
    public void TaxRateKeyIsCanonical(string text, string expected)
        => Assert.Equal(expected, NativeComparison.TaxRateKey(text, Vi));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%")]
    public void BlankTaxRateHasNoKey(string? text)
        => Assert.Null(NativeComparison.TaxRateKey(text, Vi));

    [Fact]
    public void TaxRateDisplayedByMisaWithCommaWorksForEnglishAndVietnameseCultures()
    {
        Assert.True(Same("8", "8,0000", NativeValueKind.TaxRate, En));
        Assert.True(Same("8", "8.0000", NativeValueKind.TaxRate, Vi));
    }

    // ---- decimals ----

    // Report 0.2.195, HĐ 205: 17,487 lít shown as 17,49 by a company keeping 2 số lẻ for Số lượng.
    [Theory]
    [InlineData("17.487", "17,49", 2)]
    [InlineData("17.487", "17,490", 2)]
    [InlineData("17.487", "17", 0)]
    [InlineData("17.487", "17,487", null)]
    [InlineData("17.487", "17,48", null)]
    [InlineData("20", "20,00", null)]
    public void QuantityRoundedByMisaNamesItsDecimals(string source, string actual, int? decimals)
        => Assert.Equal(decimals, NativeComparison.RoundedDecimals(
            decimal.Parse(source, CultureInfo.InvariantCulture), actual, CultureInfo.GetCultureInfo("vi-VN")));

    [Theory]
    [InlineData("1234567", "1.234.567")]
    [InlineData("1234567.5", "1.234.567,5")]
    [InlineData("1234567.50", "1.234.567,500")]
    [InlineData("0.5", "0,5")]
    [InlineData("1542900", "1.542.900 ₫")]
    [InlineData("1542900", "1.542.900 VND")]
    [InlineData("1542900", "1.542.900 VNĐ")]
    [InlineData("1542900", "1.542.900đ")]
    [InlineData("1542900", "VND 1.542.900")]
    [InlineData("-1000", "(1.000)")]
    [InlineData("-1000", "-1.000")]
    [InlineData("-1000.5", "(1.000,5)")]
    [InlineData("0", "0")]
    [InlineData("0", "0,00")]
    [InlineData("22", "22,000")]
    [InlineData("10000", "10.000,00")]
    [InlineData("608000", "608.000")]
    public void EquivalentNumberSpellingsMatchInVietnameseCulture(string expected, string actual)
        => Assert.True(Same(expected, actual, NativeValueKind.Decimal));

    [Theory]
    [InlineData("1234567", "1,234,567")]
    [InlineData("1234567.5", "1,234,567.5")]
    [InlineData("1542900", "1,542,900 VND")]
    public void EquivalentNumberSpellingsMatchInEnglishCulture(string expected, string actual)
        => Assert.True(Same(expected, actual, NativeValueKind.Decimal, En));

    [Theory]
    [InlineData("1234567", "1.234.568")]
    [InlineData("1234567", "1.234.567,01")]
    [InlineData("1000", "-1.000")]
    [InlineData("1000", "(1.000)")]
    [InlineData("1000", "100")]
    [InlineData("1000", "1")]
    [InlineData("0.1", "0,10001")]
    [InlineData("22", "220")]
    public void DifferentAmountsDoNotMatch(string expected, string actual)
        => Assert.False(Same(expected, actual, NativeValueKind.Decimal));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("12.34.567")]
    [InlineData("1.000.00")]
    [InlineData("--1")]
    [InlineData("(-1.000)")]
    [InlineData("()")]
    [InlineData("1 000 000 000 a")]
    public void UnparseableNumbersNeverMatch(string actual)
        => Assert.False(Same("1000", actual, NativeValueKind.Decimal));

    [Fact]
    public void NullActualNeverMatchesAnyKind()
    {
        foreach (var kind in Enum.GetValues<NativeValueKind>())
            Assert.False(Same("1", null, kind));
    }

    [Fact]
    public void ExpectedValueIsAlwaysReadWithTheInvariantCulture()
    {
        // The source side is invariant ("1.000" = one), the display side follows the UI culture ("1.000" = one thousand in vi-VN).
        Assert.True(Same("1.000", "1,000", NativeValueKind.Decimal));
        Assert.False(Same("1.000", "1.000", NativeValueKind.Decimal));
    }

    [Theory]
    [InlineData("1.234.567", "1234567")]
    [InlineData("1.234.567,5", "1234567.5")]
    [InlineData("1234567", "1234567")]
    [InlineData("0,5", "0.5")]
    [InlineData("1,5", "1.5")]
    [InlineData("(1.000)", "-1000")]
    [InlineData("1.542.900 ₫", "1542900")]
    [InlineData("  22  ", "22")]
    public void TryNumberParsesVietnameseDisplayText(string text, string expected)
    {
        Assert.True(NativeComparison.TryNumber(text, Vi, out var value));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("1.23", "1.23")]
    [InlineData("1,234.5", "1234.5")]
    public void TryNumberFallsBackToTheOtherSeparatorConvention(string text, string expected)
    {
        Assert.True(NativeComparison.TryNumber(text, Vi, out var value));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), value);
    }

    [Fact]
    public void FullWidthDigitsAreAcceptedAsNumbers()
    {
        Assert.True(NativeComparison.TryNumber("１.２３４", Vi, out var value));
        Assert.Equal(1234m, value);
    }

    // ---- dates ----

    [Theory]
    [InlineData("2026-10-04", "04/10/2026")]
    [InlineData("2026-10-04", "4/10/2026")]
    [InlineData("2026-10-04", "04-10-2026")]
    [InlineData("2026-10-04", "04.10.2026")]
    [InlineData("2026-10-04", "2026-10-04")]
    [InlineData("04/10/2026", "04/10/2026")]
    [InlineData("2026-10-04", "04/10/2026 00:00:00")]
    [InlineData("2026-10-04", "4/10/2026 0:00:00")]
    [InlineData("2026-10-04T00:00:00", "04/10/2026")]
    [InlineData("2026-01-05", " 05/01/2026 ")]
    public void EquivalentDateFormatsMatch(string expected, string actual)
        => Assert.True(Same(expected, actual, NativeValueKind.Date));

    [Theory]
    [InlineData("2026-10-04", "05/10/2026")]
    [InlineData("2026-10-04", "04/11/2026")]
    [InlineData("2026-10-04", "04/10/2025")]
    [InlineData("2026-10-04", "10/04/2026")]
    [InlineData("2026-10-04", "31/02/2026")]
    [InlineData("2026-10-04", "")]
    [InlineData("2026-10-04", "ngày mai")]
    [InlineData("không phải ngày", "04/10/2026")]
    public void DifferentOrInvalidDatesDoNotMatch(string expected, string actual)
        => Assert.False(Same(expected, actual, NativeValueKind.Date));

    [Fact]
    public void DayFirstDisplayIsNeverReadAsMonthFirst()
        => Assert.False(Same("2026-04-10", "04/10/2026", NativeValueKind.Date));

    // ---- text and exact ----

    [Theory]
    [InlineData("Chai 90ml", "chai 90 ml")]
    [InlineData("Chai 90ml", "CHAI 90ML")]
    [InlineData("Phân NPK 16-16-8", "Phân NPK 16–16–8")]
    [InlineData("Phân  NPK", "Phân NPK")]
    [InlineData("Bán cho người tiêu dùng", "BÁN CHO NGƯỜI TIÊU DÙNG")]
    [InlineData("Rông đỏ\u200B 200SL", "Rông đỏ 200SL")]
    public void TextComparisonIgnoresCaseSpacingAndDashVariants(string expected, string actual)
        => Assert.True(Same(expected, actual, NativeValueKind.Text));

    [Theory]
    [InlineData("Chai 90ml", "Chai 900ml")]
    [InlineData("Chai 90ml", "Chai 90 ml x")]
    [InlineData("Phân kali", "Phân Kali Trắng")]
    [InlineData("Hoá đơn", "Hoa đơn")]
    [InlineData("Gói", "Goi")]
    public void TextComparisonKeepsDifferentWordsApart(string expected, string actual)
        => Assert.False(Same(expected, actual, NativeValueKind.Text));

    [Theory]
    [InlineData("1C26MLV", "1C26MLV", true)]
    [InlineData("1C26MLV", "  1C26MLV  ", true)]
    [InlineData("1C26MLV", "1c26mlv", false)]
    [InlineData("0107286506", "107286506", false)]
    [InlineData("PT02320", "PT 02320", false)]
    public void ExactComparisonOnlyTrimsOuterWhitespace(string expected, string actual, bool matches)
        => Assert.Equal(matches, Same(expected, actual, NativeValueKind.Exact));

    [Fact]
    public void UnknownKindNeverMatches()
        => Assert.False(NativeComparison.Matches("1", "1", (NativeValueKind)99, Vi));

    // ---- derived unit price ----

    [Theory]
    [InlineData("3", "10000", "3.333,33", true)]
    [InlineData("3", "10000", "3.333", true)]
    [InlineData("3", "10000", "3.333,3333", true)]
    [InlineData("3", "10000", "3.334", false)]
    // 3.333,34 × 3 = 10.000,02: still books 10.000 đồng, so it is MISA's price for that amount.
    [InlineData("3", "10000", "3.333,34", true)]
    [InlineData("3", "10000", "3.333,50", false)]
    [InlineData("100", "1542857", "15.428,57", true)]
    [InlineData("100", "1542857", "15.429", true)]
    [InlineData("100", "1542857", "15.428", false)]
    [InlineData("2", "1", "0,5", true)]
    [InlineData("2", "1", "1", true)]
    [InlineData("2", "1", "0", false)]
    // Report 0.2.147 (HĐ 244): 11571429 / 1000 shown as 11.571,43, read back as "11571,4300".
    [InlineData("1000", "11571429", "11571,4300", true)]
    [InlineData("1000", "11571429", "11.571,4290", true)]
    [InlineData("1000", "11571429", "11571,4400", false)]
    [InlineData("10", "100004", "10000,0000", false)]
    // Report 0.2.148 (HĐ 1065): 57.619.048 / 5 shown by R20 as 11.523.809,67.
    [InlineData("5", "57619048", "11523809,6700", true)]
    [InlineData("5", "57619048", "11523809,8000", false)]
    public void DerivedUnitPriceIsRoundedToTheDisplayedScale(string quantity, string amount, string actual, bool expected)
    {
        var matched = NativeComparison.MatchesDerivedUnitPrice(decimal.Parse(quantity, CultureInfo.InvariantCulture),
            decimal.Parse(amount, CultureInfo.InvariantCulture), actual, Vi);

        Assert.Equal(expected, matched);
    }

    [Theory]
    [InlineData("0", "100", "1")]
    [InlineData("-1", "100", "1")]
    [InlineData("1", "-100", "1")]
    [InlineData("1", "100", "-1")]
    [InlineData("1", "100", "abc")]
    [InlineData("1", "100", null)]
    public void DerivedUnitPriceRejectsInvalidInput(string quantity, string amount, string? actual)
        => Assert.False(NativeComparison.MatchesDerivedUnitPrice(decimal.Parse(quantity, CultureInfo.InvariantCulture),
            decimal.Parse(amount, CultureInfo.InvariantCulture), actual, Vi));

    // ---- tax rounding (user: 1 đồng of VAT difference is accepted, Cất's prompt answered Có) ----

    [Theory]
    [InlineData("Tổng tiền thuế GTGT <156.096> khác (Tiền hàng - Tiền chiết khấu) * Thuế suất <156.095>, chênh lệch <1>. Bạn có muốn", "1")]
    [InlineData("Tổng tiền thuế GTGT &lt;11.000&gt; khác ... chênh lệch &lt;-1&gt;. Bạn có muốn", "-1")]
    [InlineData("Tổng tiền thuế GTGT <1.000.025> khác ... chênh lệch <25>.", "25")]
    [InlineData("Tổng tiền thuế GTGT <156.096> khác ... chênh lệch <1.250>.", "1250")]
    public void TaxPromptDifferenceIsRead(string message, string expected)
        => Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), NativeComparison.TaxPromptDifference(message));

    [Theory]
    [InlineData("Tổng tiền thuế GTGT khác thuế suất, có chênh lệch.")]
    [InlineData("")]
    public void TaxPromptWithoutAmountHasNoDifference(string message) => Assert.Null(NativeComparison.TaxPromptDifference(message));

    private const string PerRatePrompt = "Tiền thuế GTGT của từng loại thuế suất đang khác với công thức: (Tiền hàng - Tiền chiết khấu) * Thuế suất. "
        + "- Thuế suất <5%>: Chênh lệch <1> Bạn có muốn chương trình tự điều chỉnh số tiền chênh lệch này vào mặt hàng đầu tiên có tiền thuế không?";

    [Theory]
    [InlineData("Tổng tiền thuế GTGT <156.096> khác (Tiền hàng - Tiền chiết khấu) * Thuế suất <156.095>, chênh lệch <1>. Bạn có muốn")]
    [InlineData(PerRatePrompt)]
    [InlineData("Tiền thuế GTGT của từng loại thuế suất đang khác với công thức: - Thuế suất &lt;10%&gt;: Chênh lệch &lt;-2&gt;")]
    public void TaxPromptIsRecognisedAndAccepted(string message)
    {
        Assert.True(NativeComparison.IsTaxPrompt(message));
        Assert.True(NativeComparison.TaxPromptAcceptable(message));
    }

    [Theory]
    [InlineData("Tiền thuế GTGT của từng loại thuế suất đang khác với công thức: - Thuế suất <5%>: Chênh lệch <1> - Thuế suất <10%>: Chênh lệch <3>")]
    [InlineData("Tổng tiền thuế GTGT <1.000.025> khác ... chênh lệch <25>.")]
    [InlineData("Tổng tiền thuế GTGT khác thuế suất, có chênh lệch.")]
    [InlineData("Tiền thuế GTGT của từng loại thuế suất đang khác với công thức: - Thuế suất <5%>: Chênh lệch <abc>")]
    public void TaxPromptOverToleranceOrUnreadableIsNotAccepted(string message) => Assert.False(NativeComparison.TaxPromptAcceptable(message));

    [Theory]
    [InlineData("Số hóa đơn đã tồn tại. Bạn có muốn tự động tăng số hóa đơn không?")]
    [InlineData("Tiền thuế GTGT không hợp lệ.")]
    public void OtherPromptsAreNotTaxPrompts(string message) => Assert.False(NativeComparison.IsTaxPrompt(message));

    [Theory]
    [InlineData("Doanh nghiệp mã số thuế <3301493428> có khả năng <NNT ngừng hoạt động nhưng chưa hoàn thành thủ tục chấm dứt hiệu lực MST>. "
        + "Bạn nên kiểm tra lại tình trạng hoạt động mới nhất của doanh nghiệp trên web của Tổng cục thuế để đảm bảo tính đúng đắn. Bạn có muốn tiếp tục Cất không?")]
    [InlineData("Doanh nghiệp mã số thuế &lt;3301493428&gt; có khả năng ... Bạn nên kiểm tra lại tình trạng hoạt  động mới nhất ... Bạn có muốn tiếp tục Cất không?")]
    public void TaxpayerStatusWarningIsRecognised(string message)
    {
        Assert.True(NativeComparison.IsTaxpayerStatusPrompt(message));
        Assert.False(NativeComparison.IsTaxPrompt(message));
    }

    [Theory]
    [InlineData(PerRatePrompt)]
    [InlineData("Số hóa đơn đã tồn tại. Bạn có muốn tự động tăng số hóa đơn không?")]
    [InlineData("Mã số thuế không hợp lệ. Bạn có muốn tiếp tục không?")]
    public void OtherPromptsAreNotTaxpayerStatusWarnings(string message) => Assert.False(NativeComparison.IsTaxpayerStatusPrompt(message));

    [Theory]
    [InlineData("156096", "156.096", true)]
    [InlineData("156096", "156.095", true)]
    [InlineData("156096", "156.097", true)]
    [InlineData("156096", "156.094", true)]
    [InlineData("156096", "156.093", false)]
    [InlineData("156096", null, false)]
    public void TaxWithinOneDongMatches(string expected, string? actual, bool matched)
        => Assert.Equal(matched, NativeComparison.MatchesTaxRounding(expected, actual, Vi));

    // ---- reconciliation result ----

    [Fact]
    public void ReconciliationNeedsAtLeastOneValueAndAllMustMatch()
    {
        var ok = new ValueComparison("buyer", "A", "A", true, "native");
        var bad = new ValueComparison("total", "1", "2", false, "native");

        Assert.False(new ReconciliationResult("id", 1, DateTimeOffset.UtcNow, []).Matched);
        Assert.True(new ReconciliationResult("id", 1, DateTimeOffset.UtcNow, [ok]).Matched);
        Assert.False(new ReconciliationResult("id", 1, DateTimeOffset.UtcNow, [ok, bad]).Matched);
    }
}
