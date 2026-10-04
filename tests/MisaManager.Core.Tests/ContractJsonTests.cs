using System.Text.Json;

namespace MisaManager.Core.Tests;

public sealed class ContractJsonTests
{
    private sealed record Amounts(decimal Total, decimal? Optional = null);
    private sealed record Status(WorkflowStatus Value);
    private sealed record Handle(nint Window);
    private sealed record Named(string FullName);

    private static string Write<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Compact);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, ContractJson.Compact)!;

    // ---- exact-string decimals ----

    [Fact]
    public void DecimalsAreWrittenAsInvariantStrings()
        => Assert.Equal("{\"total\":\"1542900\",\"optional\":null}", Write(new Amounts(1_542_900m)));

    [Fact]
    public void DecimalScaleIsPreservedOnWrite()
        => Assert.Equal("100.50", JsonDocument.Parse(Write(new Amounts(100.50m))).RootElement.GetProperty("total").GetString());

    [Theory]
    [InlineData("1542900", 1_542_900)]
    [InlineData("-1000", -1000)]
    [InlineData("0", 0)]
    [InlineData("0.5", 0.5)]
    [InlineData("12345.12345678", 12345.12345678)]
    public void ExactDecimalStringsAreRead(string text, double expected)
        => Assert.Equal((decimal)expected, Read<Amounts>($"{{\"total\":\"{text}\"}}").Total);

    [Fact]
    public void DecimalRoundTripKeepsTheScale()
    {
        var back = Read<Amounts>(Write(new Amounts(1234.5000m, 0.10m)));

        Assert.Equal("1234.5000", back.Total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("0.10", back.Optional!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1000")]        // JSON number, not a string
    [InlineData("1000.5")]
    [InlineData("null")]
    [InlineData("true")]
    public void DecimalMustBeAStringToken(string token)
        => Assert.Throws<JsonException>(() => Read<Amounts>($"{{\"total\":{token}}}"));

    [Theory]
    [InlineData("1,5")]
    [InlineData("1.000,5")]
    [InlineData("1e3")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("--1")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0.123456789")]
    [InlineData("1111111111111111111111111")]
    [InlineData("12345678901234567.12345678")]
    [InlineData("１２３")]
    public void MalformedDecimalStringsAreRejected(string text)
        => Assert.Throws<JsonException>(() => Read<Amounts>($"{{\"total\":\"{text}\"}}"));

    [Theory]
    [InlineData("0", true)]
    [InlineData("-0", true)]
    [InlineData("007", true)]
    [InlineData("0.00000001", true)]
    [InlineData("999999999999999999999999", true)]
    [InlineData("99999999999999999999.9999", true)]
    [InlineData("-123.45", true)]
    [InlineData("0.000000001", false)]
    [InlineData("1000000000000000000000000", false)]
    [InlineData("1,5", false)]
    [InlineData("1e5", false)]
    [InlineData("1.", false)]
    [InlineData("", false)]
    public void DecimalValueEnforcesTwentyFourDigitsAndScaleEight(string text, bool accepted)
        => Assert.Equal(accepted, DecimalValue.TryParseInvariant(text, out _));

    [Fact]
    public void DecimalValueReturnsTheParsedNumber()
    {
        Assert.True(DecimalValue.TryParseInvariant("-1542857.25", out var value));
        Assert.Equal(-1542857.25m, value);
    }

    // ---- options ----

    [Fact]
    public void PropertyNamesAreCamelCaseAndCaseSensitive()
    {
        Assert.Equal("{\"fullName\":\"Nguyễn\"}".Replace("ễ", "\\u1EC5"), Write(new Named("Nguyễn")));
        Assert.Throws<JsonException>(() => Read<Named>("{\"FullName\":\"x\"}"));
    }

    [Fact]
    public void UnknownPropertiesAreRejected()
        => Assert.Throws<JsonException>(() => Read<Named>("{\"fullName\":\"x\",\"extra\":1}"));

    [Fact]
    public void EnumsUseSnakeCaseNamesAndRefuseIntegers()
    {
        Assert.Equal("{\"value\":\"sales_surface\"}", Write(new Status(WorkflowStatus.SalesSurface)));
        Assert.Equal("{\"value\":\"input_uncertain\"}", Write(new Status(WorkflowStatus.InputUncertain)));
        Assert.Equal(WorkflowStatus.SaveUncertain, Read<Status>("{\"value\":\"save_uncertain\"}").Value);
        Assert.Throws<JsonException>(() => Read<Status>("{\"value\":3}"));
        Assert.Throws<JsonException>(() => Read<Status>("{\"value\":\"3\"}"));
        Assert.Throws<JsonException>(() => Read<Status>("{\"value\":\"SALES_SURFACE\"}"));
        Assert.Throws<JsonException>(() => Read<Status>("{\"value\":\"nope\"}"));
    }

    [Fact]
    public void SeverityAndPresenceEnumsAreSnakeCase()
    {
        Assert.Equal("\"not_applicable\"", JsonSerializer.Serialize(ValuePresence.NotApplicable, ContractJson.Compact));
        Assert.Equal("\"blocking\"", JsonSerializer.Serialize(IssueSeverity.Blocking, ContractJson.Compact));
    }

    [Fact]
    public void NativeHandlesAreJsonNumbers()
    {
        Assert.Equal("{\"window\":656450}", Write(new Handle(656450)));
        Assert.Equal(656450, Read<Handle>("{\"window\":656450}").Window);
        Assert.Throws<JsonException>(() => Read<Handle>("{\"window\":\"656450\"}"));
    }

    [Fact]
    public void IndentedOptionsAreIndentedAndCompactAreNot()
    {
        var indented = JsonSerializer.Serialize(new Named("x"), ContractJson.Options);

        Assert.Contains(Environment.NewLine, indented);
        Assert.DoesNotContain('\n', Write(new Named("x")));
    }

    [Fact]
    public void BothOptionSetsAreReadOnlyAndShared()
    {
        Assert.True(ContractJson.Options.IsReadOnly);
        Assert.True(ContractJson.Compact.IsReadOnly);
        Assert.Same(ContractJson.Options, ContractJson.Options);
    }

    [Fact]
    public void ImportIssueRoundTrips()
    {
        var issue = new ImportIssue("TOTAL_MISMATCH", IssueSeverity.Blocking, "Tổng các dòng không khớp tổng hóa đơn.", "Hóa đơn", "J7",
            "0123456789abcdef01234567", "line:2", "1.542.900", "1.542.857", "Kiểm tra lại.");

        Assert.Equal(issue, Read<ImportIssue>(Write(issue)));
    }

    [Fact]
    public void ValueComparisonRoundTripsWithVietnameseText()
    {
        var value = new ValueComparison("buyer", "BÁN CHO NGƯỜI TIÊU DÙNG", "BÁN CHO NGƯỜI TIÊU DÙNG", true, "native");

        Assert.Equal(value, Read<ValueComparison>(Write(value)));
    }

    // ---- Money ----

    [Fact]
    public void MoneyKeepsAmountAndCurrency()
    {
        var money = new Money(1_542_900m, "VND");

        Assert.Equal(1_542_900m, money.Amount);
        Assert.Equal("VND", money.Currency);
    }

    [Fact]
    public void MoneyHasValueEquality() => Assert.Equal(new Money(10m, "VND"), new Money(10m, "VND"));

    [Fact]
    public void MoneyDistinguishesCurrencies() => Assert.NotEqual(new Money(10m, "VND"), new Money(10m, "USD"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TOO-LONG-X")]
    public void MoneyRequiresAShortCurrencyCode(string currency)
        => Assert.Throws<ArgumentException>(() => new Money(1m, currency));

    [Fact]
    public void MoneyRejectsAmountsBeyondTheContractPrecision()
    {
        Assert.Throws<ArgumentException>(() => new Money(0.123456789m, "VND"));
        Assert.Throws<ArgumentException>(() => new Money(decimal.MaxValue, "VND"));
        Assert.Throws<ArgumentException>(() => new Money(1_000_000_000_000_000_000_000_000m, "VND"));
    }

    [Theory]
    [InlineData("0.12345678")]
    [InlineData("-99999999999999999999.9999")]
    [InlineData("0")]
    public void MoneyAcceptsAmountsAtTheContractLimits(string amount)
        => Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            new Money(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "VND").Amount);

    [Fact]
    public void ProductIdentityVersionsAreStable()
    {
        Assert.Equal("MisaManager", ProductIdentity.TechnicalName);
        Assert.Equal(1, ProductIdentity.StateSchemaVersion);
        Assert.Equal(1, ProductIdentity.WorkbookSchemaVersion);
    }
}
