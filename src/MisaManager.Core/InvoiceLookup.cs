namespace MisaManager.Core;

/// <summary>Finds the source invoice that a voucher read from MISA belongs to.</summary>
public static class InvoiceLookup
{
    /// <summary>
    /// The single invoice with MISA's invoice number (leading zeros ignored); the series decides between several
    /// invoices with that number. Null when none or more than one remain: comparing against a guess would report
    /// another invoice's data as mismatches.
    /// </summary>
    public static InvoiceDocument? ByNumber(IEnumerable<InvoiceDocument> invoices, string? number, string? series)
    {
        var key = NumberKey(number);
        if (key.Length == 0) return null;
        var sameNumber = invoices.Where(invoice => NumberKey(invoice.Identity.Number) == key).ToArray();
        if (sameNumber.Length <= 1) return sameNumber.FirstOrDefault();
        var seriesKey = TextKey.Compact(series);
        if (seriesKey.Length == 0) return null;
        var sameSeries = sameNumber.Where(invoice => TextKey.Compact(invoice.Identity.Series) == seriesKey).ToArray();
        return sameSeries.Length == 1 ? sameSeries[0] : null;
    }

    /// <summary>Invoice number for comparison: trimmed, leading zeros of an all-digit number dropped.</summary>
    public static string NumberKey(string? number)
    {
        var text = (number ?? "").Trim();
        return text.Length > 0 && text.All(char.IsAsciiDigit) ? text.TrimStart('0') is { Length: > 0 } digits ? digits : "0" : text;
    }
}
