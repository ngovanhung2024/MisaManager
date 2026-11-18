using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MisaManager.Core;

/// <summary>Kind of a fuel purchase line: diesel is stocked, gasoline and lubricants are expensed at once.</summary>
public enum FuelKind { Diesel, Gasoline }

/// <summary>How a purchase voucher is posted: "1. … nhập kho" (Kho + TK kho) or "2. … không qua kho" (TK chi phí).</summary>
public enum PurchaseRoute { Stock, Expense }

/// <summary>Posting of one purchase invoice. <see cref="Account"/> is TK kho (Stock) or TK chi phí (Expense).</summary>
public sealed record PurchasePosting(PurchaseRoute Route, string Stock, string Account);

/// <summary>
/// A buyer that only enters its fuel purchases (Lộc Thủy): diesel → nhập kho <see cref="Stock"/>/<see cref="StockAccount"/>,
/// xăng/nhớt → không qua kho <see cref="ExpenseAccount"/>; every other invoice (bia, sửa chữa, điện…) is left out.
/// </summary>
public sealed record FuelPurchaseCompany(string TaxCode, string Name, string Stock, string StockAccount, string ExpenseAccount);

public static partial class FuelPurchases
{
    public const string NotFuelReason = "không phải xăng, dầu diesel, nhớt";
    public const string BothRoutesReason = "có cả dầu diesel (nhập kho) và xăng/nhớt (không qua kho) – tách thành 2 chứng từ, nhập tay";

    /// <summary>
    /// The selected company's fuel-only rule (<see cref="CompanyProfile.FuelRule"/>) when the purchase invoice's buyer is
    /// that company; null for the ordinary purchase flow.
    /// </summary>
    public static FuelPurchaseCompany? Company(InvoiceDocument invoice, FuelPurchaseCompany? rule)
    {
        if (rule is null || !invoice.IsPurchase()) return null;
        var buyer = PartyCatalogStore.TaxKey(invoice.Buyer.TaxCode);
        return buyer.Length > 0 && buyer == PartyCatalogStore.TaxKey(rule.TaxCode) ? rule : null;
    }

    /// <summary>
    /// Fuel kind from the leading words of the item name: "Dầu Điêzen/Diesel/DO …" is diesel, "Xăng …", "Nhớt …",
    /// "Dầu nhờn/thủy lực/động cơ …" and "Dầu 5W30" are gasoline/lubricants. "Lọc nhớt" or "động cơ xăng" are not fuel.
    /// </summary>
    public static FuelKind? Kind(string? name)
    {
        var words = Words(name);
        if (words.Length == 0) return null;
        if (DieselWords.Contains(words[0])) return FuelKind.Diesel;
        if (words[0] is "XANG" or "NHOT") return FuelKind.Gasoline;
        if (words[0] != "DAU" || words.Length < 2) return null;
        if (DieselWords.Contains(words[1]) || words[1] == "DO") return FuelKind.Diesel;
        if (LubricantWords.Contains(words[1]) || Viscosity().IsMatch(words[1])) return FuelKind.Gasoline;
        return null;
    }

    /// <summary>
    /// Posting of an invoice of a fuel-only buyer: all lines diesel → Stock, all lines xăng/nhớt → Expense. Null when the
    /// buyer has no fuel rule or the invoice is not entirely fuel of one route (discount lines aside).
    /// </summary>
    public static PurchasePosting? Posting(InvoiceDocument invoice, FuelPurchaseCompany? rule)
    {
        if (Company(invoice, rule) is not { } company) return null;
        var kinds = invoice.Lines.Where(line => !ItemNatures.IsDiscount(line)).Select(line => Kind(line.Name)).Distinct().ToArray();
        return kinds switch
        {
            [FuelKind.Diesel] => new(PurchaseRoute.Stock, company.Stock, company.StockAccount),
            [FuelKind.Gasoline] => new(PurchaseRoute.Expense, company.Stock, company.ExpenseAccount),
            _ => null
        };
    }

    /// <summary>True when every non-discount line is gasoline/lubricant for the selected fuel company.</summary>
    public static bool IsGasolineOnly(InvoiceDocument invoice, FuelPurchaseCompany? rule)
    {
        if (Company(invoice, rule) is null) return false;
        var lines = invoice.Lines.Where(line => !ItemNatures.IsDiscount(line)).ToArray();
        return lines.Length > 0 && lines.All(line => Kind(line.Name) == FuelKind.Gasoline);
    }

    /// <summary>Lines as the "Dịch vụ" tab lists a fuel-only buyer's invoice that is left out (null when it is entered).</summary>
    public static IReadOnlyList<ServiceLine>? Skipped(InvoiceDocument invoice, FuelPurchaseCompany? rule)
    {
        if (Company(invoice, rule) is null || Posting(invoice, rule) is not null) return null;
        var lines = invoice.Lines.Where(line => !ItemNatures.IsDiscount(line)).ToArray();
        var bothRoutes = lines.All(line => Kind(line.Name) is not null);
        return lines.Select(line => new ServiceLine(line.Name, line.Unit, line.AmountBeforeTax, bothRoutes || Kind(line.Name) is null,
            bothRoutes ? BothRoutesReason : Kind(line.Name) is null ? NotFuelReason : "")).ToArray();
    }

    private static readonly HashSet<string> DieselWords = new(StringComparer.Ordinal)
        { "DIEZEN", "DIEZEL", "DIESEL", "DIEDEN" };
    private static readonly HashSet<string> LubricantWords = new(StringComparer.Ordinal)
        { "NHON", "NHOT", "THUY", "DONG", "MO", "HOP", "PHANH" };

    // "5W30", "15W40", "20W50": SAE grades of engine oil.
    [GeneratedRegex(@"^\d+W\d*$", RegexOptions.CultureInvariant)]
    private static partial Regex Viscosity();

    [GeneratedRegex(@"[A-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    // Upper-case words without accents ("Điêzen" → "DIEZEN").
    private static string[] Words(string? value)
    {
        var text = TextKey.Canonical(value).Replace('Đ', 'D').Replace('đ', 'D');
        var plain = new StringBuilder(text.Length);
        foreach (var c in text)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(c);
        return Word().Matches(plain.ToString().ToUpperInvariant()).Select(match => match.Value).ToArray();
    }
}
