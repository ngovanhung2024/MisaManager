using System.Text.RegularExpressions;

namespace MisaManager.Core;

/// <summary>
/// Removes the MISA licence/data company from exported text: the user works for several companies and
/// reports must not reveal which one. Customer/seller names of the invoice are data and stay.
/// </summary>
public static partial class CompanyRedaction
{
    public const string Placeholder = "[tên công ty]";

    public static string Redact(string? text, IEnumerable<string> companies)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = WorkingOn().Replace(text, match => match.Groups["label"].Value + Placeholder);
        foreach (var company in companies.Where(name => name.Trim().Length >= 4).OrderByDescending(name => name.Length))
            result = result.Replace(company.Trim(), Placeholder, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    // R20's header shows the open data's company: "Đang làm việc trên: CÔNG TY …".
    [GeneratedRegex(@"(?<label>Đang làm việc trên:\s*)[^\r\n""|]+", RegexOptions.CultureInvariant)]
    private static partial Regex WorkingOn();
}
