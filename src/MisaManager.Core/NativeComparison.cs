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
    /// <summary>
    /// MISA computes "Đơn giá" itself from the typed amount; the tool never types it. Its value matches when it is
    /// amount / quantity at the displayed scale, or when price × quantity rounds to the amount in whole đồng: R20 phiếu
    /// nhập showed 11.523.809,67 for 57.619.048 / 5 (HĐ 1065), which still books exactly that amount.
    /// </summary>
    public static bool MatchesDerivedUnitPrice(decimal quantity, decimal amount, string? actual, CultureInfo culture)
    {
        if (quantity <= 0 || amount < 0 || actual is null || !Number(actual, culture, out var price) || price < 0) return false;
        try { if (decimal.Round(price * quantity, 0, MidpointRounding.AwayFromZero) == amount) return true; }
        catch (OverflowException) { return false; }
        var separator = culture.NumberFormat.NumberDecimalSeparator;
        var parts = actual.Trim().Split(separator, StringSplitOptions.None);
        var scale = parts.Length == 2 ? parts[1].Length : 0;
        if (scale > 28) return false;
        // R20 phiếu nhập keeps the price at 2 decimals but reports it padded to 4 ("11571,4300" for 11.571,43); the zero
        // padding beyond 2 decimals is not precision. Two decimals stay significant: "10000,0000" is not 10000,4.
        if (scale > 2) scale = Math.Max(2, parts[1].TrimEnd('0').Length);
        try { return decimal.Round(amount / quantity, scale, MidpointRounding.AwayFromZero) == price; }
        catch (OverflowException) { return false; }
    }

    /// <summary>VAT rounding MISA may differ by (its per-line/per-rate rounding vs the e-invoice); accepted by the user.</summary>
    public const decimal TaxRoundingTolerance = 2m;

    /// <summary>
    /// The amount of MISA's Cất prompt "... Thuế suất &lt;156.095&gt;, chênh lệch &lt;1&gt;. Bạn có muốn ..." (1); null when the
    /// prompt states no readable amount.
    /// </summary>
    public static decimal? TaxPromptDifference(string? message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            System.Net.WebUtility.HtmlDecode(message ?? "").Normalize(NormalizationForm.FormC), @"chênh\s*lệch\s*<\s*([^<>]+?)\s*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && TryNumber(match.Groups[1].Value, CultureInfo.GetCultureInfo("vi-VN"), out var value) ? value : null;
    }

    /// <summary>Tax amount or total equal to the source within <see cref="TaxRoundingTolerance"/> (2 đồng).</summary>
    public static bool MatchesTaxRounding(string expected, string? actual, CultureInfo culture)
        => actual is not null && TryNumber(expected, CultureInfo.InvariantCulture, out var left) && TryNumber(actual, culture, out var right)
            && Math.Abs(left - right) <= TaxRoundingTolerance;

    /// <summary>
    /// Compares a source value with MISA's display. Only presentation differences are ignored
    /// (case, spacing such as "90ml"/"90 ml", decimal/group separators, currency symbols,
    /// "5"/"5%"/"5,0000", date formats); any different business value still mismatches.
    /// </summary>
    public static bool Matches(string expected, string? actual, NativeValueKind kind, CultureInfo culture)
    {
        if (actual is null) return false;
        return kind switch
        {
            NativeValueKind.Exact => string.Equals(expected.Trim(), actual.Trim(), StringComparison.Ordinal),
            NativeValueKind.Text => Normalize(expected) == Normalize(actual),
            NativeValueKind.Decimal => TryNumber(expected, CultureInfo.InvariantCulture, out var left)
                && TryNumber(actual, culture, out var right) && left == right,
            NativeValueKind.TaxRate => TaxRateKey(expected, CultureInfo.InvariantCulture) is { } rate
                && TaxRateKey(actual, culture) == rate,
            NativeValueKind.Date => TryReadDate(expected, CultureInfo.InvariantCulture, out var day)
                && TryReadDate(actual, culture, out var actualDay) && day == actualDay,
            _ => false
        };
    }

    /// <summary>Canonical tax-rate text: "5", "5%", "5,0000" → "5"; KCT/KKKNT stay textual.</summary>
    public static string? TaxRateKey(string? text, CultureInfo culture)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim().TrimEnd('%').Trim();
        if (TryNumber(trimmed, culture, out var rate))
            return decimal.Round(rate, 4).ToString("0.####", CultureInfo.InvariantCulture);
        var word = Normalize(trimmed);
        return word.Length == 0 ? null : word;
    }

    /// <summary>
    /// Parses a number using the display culture first, then the other common separator
    /// convention when the text is not valid in the first one. Currency marks are ignored.
    /// </summary>
    public static bool TryNumber(string text, CultureInfo culture, out decimal value)
    {
        value = 0;
        var cleaned = CleanNumber(text, out var negative);
        if (cleaned.Length == 0) return false;
        var alternate = culture.NumberFormat.NumberDecimalSeparator == "," ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("vi-VN");
        if (!Number(cleaned, culture, out value) && !Number(cleaned, alternate, out value)) return false;
        if (negative) value = -value;
        return true;
    }

    private static string CleanNumber(string text, out bool negative)
    {
        var value = text.Normalize(NormalizationForm.FormKC).Trim();
        negative = false;
        // Accounting displays negatives as "(1.000)".
        if (value.Length > 2 && value[0] == '(' && value[^1] == ')') { negative = true; value = value[1..^1].Trim(); }
        foreach (var mark in new[] { "VNĐ", "VND", "₫", "đ", "Đ" })
            value = value.Replace(mark, "", StringComparison.Ordinal);
        value = new string(value.Where(character => !char.IsWhiteSpace(character)
            && CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.Format).ToArray());
        if (value.StartsWith('-') && negative) return "";
        return value;
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

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy",
        "dd/MM/yyyy HH:mm:ss", "d/M/yyyy H:mm:ss", "dd/MM/yyyy h:mm:ss tt", "yyyy-MM-ddTHH:mm:ss"];

    private static bool TryReadDate(string text, CultureInfo culture, out DateOnly day)
    {
        day = default;
        text = text.Trim();
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return true;
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
        {
            day = DateOnly.FromDateTime(exact);
            return true;
        }
        // MISA R20 exposes date editors through ValuePattern with a localized time suffix.
        if (!DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) return false;
        day = DateOnly.FromDateTime(parsed);
        return true;
    }

    /// <summary>Case- and spacing-insensitive text; spaces between digits and letters ("90 ml") are presentation only.</summary>
    public static string Normalize(string value)
    {
        var text = new string(value.Normalize(NormalizationForm.FormC)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.Format)
            .Select(character => char.IsWhiteSpace(character) ? ' '
                : CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.DashPunctuation ? '-' : character)
            .ToArray());
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder(text.Length);
        for (var index = 0; index < words.Length; index++)
        {
            if (index > 0 && !(char.IsDigit(words[index - 1][^1]) && char.IsLetter(words[index][0])))
                result.Append(' ');
            result.Append(words[index]);
        }
        return result.ToString().ToUpperInvariant();
    }
}
