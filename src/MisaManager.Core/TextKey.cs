using System.Globalization;
using System.Text;

namespace MisaManager.Core;

/// <summary>
/// Comparison keys for names typed in Excel and names exported/shown by MISA. Two spellings of the same
/// text produce the same key; the key is for equality only and is never shown or typed into MISA.
/// </summary>
public static class TextKey
{
    // Vietnamese tone marks in decomposed form. Vowel-shape marks (circumflex, breve, horn) stay on their letter.
    private static bool IsTone(char c) => c is '\u0300' or '\u0301' or '\u0303' or '\u0309' or '\u0323'
        // Combining forms some legacy keyboards/fonts emit for the same tones.
        or '\u0340' or '\u0341' or '\u0342';

    private static char Fold(char c) => c switch
    {
        // Eth (Icelandic) and similar look-alikes are routinely typed for Vietnamese Đ.
        '\u00D0' or '\u0189' => 'Đ',
        '\u00F0' or '\u0256' => 'đ',
        '\u2018' or '\u2019' or '\u201A' or '\u201B' or '\u2032' or '\u00B4' or '`' => '\'',
        '\u201C' or '\u201D' or '\u201E' or '\u201F' or '\u2033' => '"',
        '\u00D7' or '\u2715' or '\u2716' => 'x',
        '\u2212' => '-',
        _ when CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.DashPunctuation => '-',
        _ => c
    };

    /// <summary>
    /// Uppercase, compatibility-folded (full-width, ligatures, NBSP), invisible characters removed, look-alike
    /// punctuation unified, decimal comma between digits read as a dot, whitespace collapsed to single spaces,
    /// and the Vietnamese tone mark moved to the end of its syllable so old/new tone placement
    /// ("hoà"/"hòa", "thuỷ"/"thủy", "khoẻ"/"khỏe") and precomposed/combining input compare equal.
    /// </summary>
    public static string Canonical(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value.Normalize(NormalizationForm.FormKC);
        var folded = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.Format || char.IsControl(raw) && !char.IsWhiteSpace(raw))
                continue;
            folded.Append(char.IsWhiteSpace(raw) ? ' ' : Fold(raw));
        }
        var decomposed = folded.ToString().Normalize(NormalizationForm.FormD).ToUpperInvariant();
        var result = new StringBuilder(decomposed.Length);
        var tones = new StringBuilder();
        var pendingSpace = false;
        void EndSyllable()
        {
            result.Append(tones);
            tones.Clear();
        }
        for (var index = 0; index < decomposed.Length; index++)
        {
            var c = decomposed[index];
            if (IsTone(c)) { tones.Append(c == '\u0340' ? '\u0300' : c == '\u0341' ? '\u0301' : c == '\u0342' ? '\u0303' : c); continue; }
            var mark = CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark;
            if (char.IsLetter(c) || mark) { if (pendingSpace && result.Length > 0) result.Append(' '); pendingSpace = false; result.Append(c); continue; }
            EndSyllable();
            if (c == ' ') { pendingSpace = true; continue; }
            if (pendingSpace && result.Length > 0) result.Append(' ');
            pendingSpace = false;
            var betweenDigits = index > 0 && index + 1 < decomposed.Length && char.IsDigit(decomposed[index - 1]) && char.IsDigit(decomposed[index + 1]);
            result.Append(c == ',' && betweenDigits ? '.' : c);
        }
        EndSyllable();
        return result.ToString();
    }

    /// <summary><see cref="Canonical"/> without any spaces ("Chai 90 ml" = "Chai 90ml").</summary>
    public static string Compact(string? value) => Canonical(value).Replace(" ", "", StringComparison.Ordinal);

    /// <summary>Letters (with their marks) and digits only: ignores spacing and punctuation entirely.</summary>
    public static string LettersAndDigits(string? value)
        => new(Canonical(value).Where(c => char.IsLetterOrDigit(c)
            || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark).ToArray());
}
