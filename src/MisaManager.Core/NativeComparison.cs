using System.Globalization;
using System.Text;

namespace MisaManager.Core;

public enum NativeValueKind { Exact, Text, Decimal, Date, TaxRate }

public sealed record ValueComparison(string Field, string Expected, string? Actual, bool Matched, string Source);
public sealed record ReconciliationResult(string InvoiceId, int Revision, DateTimeOffset ObservedAt,
    IReadOnlyList<ValueComparison> Values, string? SourceSha256 = null)
{
    public bool Matched => Values.Count > 0 && Values.All(value => value.Matched);
}

public static class NativeComparison
{
    public static bool MatchesDerivedUnitPrice(decimal quantity, decimal amount, string? actual, CultureInfo culture)
    {
        if (quantity <= 0 || amount < 0 || actual is null || !Number(actual, culture, out var price) || price < 0) return false;
        var separator = culture.NumberFormat.NumberDecimalSeparator;
        var parts = actual.Trim().Split(separator, StringSplitOptions.None);
        var scale = parts.Length == 2 ? parts[1].Length : 0;
        if (scale > 28) return false;
        try { return decimal.Round(amount / quantity, scale, MidpointRounding.AwayFromZero) == price; }
        catch (OverflowException) { return false; }
    }

    public static bool Matches(string expected, string? actual, NativeValueKind kind, CultureInfo culture)
    {
        if (actual is null) return false;
        return kind switch
        {
            NativeValueKind.Exact => string.Equals(expected, actual.Trim(), StringComparison.Ordinal),
            NativeValueKind.Text => Normalize(expected) == Normalize(actual),
            NativeValueKind.Decimal => Number(expected, CultureInfo.InvariantCulture, out var left)
                && Number(actual, culture, out var right) && left == right,
            NativeValueKind.TaxRate => Number(expected.TrimEnd('%').Trim(), CultureInfo.InvariantCulture, out var rate)
                && Number(actual.TrimEnd('%').Trim(), culture, out var actualRate) && rate == actualRate,
            NativeValueKind.Date => DateOnly.TryParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day) && TryReadDate(actual, culture, out var actualDay) && day == actualDay,
            _ => false
        };
    }

    private static bool Number(string text, CultureInfo culture, out decimal value)
    {
        value = 0;
        text = text.Trim();
        var format = culture.NumberFormat;
        var parts = text.TrimStart('+', '-').Split(format.NumberDecimalSeparator, StringSplitOptions.None);
        if (parts.Length > 2) return false;
        var groups = parts[0].Split(format.NumberGroupSeparator, StringSplitOptions.None);
        if (groups.Length > 1 && (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(group => group.Length != 3))) return false;
        if (groups.Any(group => group.Length == 0 || group.Any(c => c is < '0' or > '9'))
            || parts.Length == 2 && (parts[1].Length == 0 || parts[1].Any(c => c is < '0' or > '9'))) return false;
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, culture, out value);
    }

    private static bool TryReadDate(string text, CultureInfo culture, out DateOnly day)
    {
        day = default;
        text = text.Trim();
        if (DateOnly.TryParseExact(text, ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"],
                culture, DateTimeStyles.None, out day)) return true;

        // MISA R20 exposes date editors through ValuePattern with a localized time suffix.
        if (!DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) return false;
        day = DateOnly.FromDateTime(parsed);
        return true;
    }

    private static string Normalize(string value) => string.Join(" ", value.Normalize(NormalizationForm.FormC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
