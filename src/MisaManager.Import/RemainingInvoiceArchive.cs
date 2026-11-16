using System.IO.Compression;
using System.Text;
using MisaManager.Core;

namespace MisaManager.Import;

public sealed record RemainingInvoiceArchiveResult(int ExportedFiles, IReadOnlyList<string> UnmatchedInvoices, IReadOnlyList<string> SkippedFiles);

/// <summary>Copies the original HTML pages for invoices that are still pending in the current import list.</summary>
public static class RemainingInvoiceArchive
{
    private const long MaxFileBytes = 20L * 1024 * 1024;

    public static RemainingInvoiceArchiveResult Export(string source, string destination,
        IReadOnlyCollection<InvoiceDocument> pending, string companyTaxCode = "", CancellationToken token = default)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Không tìm thấy file zip nguồn.", source);
        if (!source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("File nguồn phải là zip chứa các trang HTML hóa đơn.");
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File zip kết quả phải khác file zip nguồn.");

        var wanted = pending.DistinctBy(invoice => invoice.Fingerprint).ToArray();
        var byNumber = wanted.GroupBy(invoice => InvoiceLookup.NumberKey(invoice.Identity.Number))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var matches = wanted.ToDictionary(invoice => invoice.Fingerprint,
            _ => new List<(ZipArchiveEntry Entry, PurchaseHtmlInvoice Invoice)>());
        var found = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        var copied = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            using (var input = ZipFile.OpenRead(source))
            {
                var files = 0;
                foreach (var entry in input.Entries.OrderBy(item => item.FullName, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Length == 0 || !IsHtml(entry.Name)) continue;
                    if (++files > 20000) throw new InvalidDataException("File zip có hơn 20.000 file HTML.");
                    if (PurchaseInvoiceArchive.IsCancelledFile(entry.Name)) continue;
                    if (!SafeName(entry.FullName)) { skipped.Add(entry.FullName + ": đường dẫn không hợp lệ"); continue; }
                    if (entry.Length > MaxFileBytes) { skipped.Add(entry.FullName + ": file quá lớn"); continue; }
                    string html;
                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                        html = reader.ReadToEnd();
                    PurchaseHtmlInvoice invoice;
                    try { invoice = PurchaseInvoiceArchive.Parse(html, entry.FullName); }
                    catch (WorkbookImportException error) { skipped.Add(entry.FullName + ": " + error.Message); continue; }
                    if (!byNumber.TryGetValue(InvoiceLookup.NumberKey(invoice.Number), out var candidates)) continue;
                    foreach (var candidate in candidates.Where(candidate => Matches(candidate, invoice, companyTaxCode)))
                        matches[candidate.Fingerprint].Add((entry, invoice));
                }
                using var output = ZipFile.Open(temporary, ZipArchiveMode.Create);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in wanted)
                {
                    token.ThrowIfCancellationRequested();
                    var options = matches[candidate.Fingerprint];
                    // Old sales workbooks omit seller MST. Require one unambiguous HTML identity before copying.
                    if (options.Select(option => (PartyCatalogStore.TaxKey(option.Invoice.SellerTaxCode),
                            TextKey.Compact(option.Invoice.Series), option.Invoice.Issued, option.Invoice.Template,
                            PartyCatalogStore.TaxKey(option.Invoice.BuyerTaxCode))).Distinct().Count() != 1) continue;
                    var entry = options[0].Entry;
                    var name = entry.FullName;
                    while (!names.Add(name)) name = Guid.NewGuid().ToString("N") + "/" + entry.FullName;
                    var copy = output.CreateEntry(name, CompressionLevel.Fastest);
                    using var inputStream = entry.Open();
                    using var outputStream = copy.Open();
                    inputStream.CopyTo(outputStream);
                    copied++;
                    found.Add(candidate.Fingerprint);
                }
            }
            token.ThrowIfCancellationRequested();
            if (copied > 0) File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort cleanup */ }
        }

        return new(copied, wanted.Where(invoice => !found.Contains(invoice.Fingerprint)).Select(Label).ToArray(), skipped);
    }

    private static bool IsHtml(string name)
        => name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(InvoiceDocument expected, PurchaseHtmlInvoice actual, string companyTaxCode)
    {
        var tax = PartyCatalogStore.TaxKey(expected.Identity.SellerTaxCode);
        if (tax.Length == 0 && !expected.IsPurchase()) tax = PartyCatalogStore.TaxKey(companyTaxCode);
        return (tax.Length == 0 || tax == PartyCatalogStore.TaxKey(actual.SellerTaxCode))
            && PostedInvoiceStore.SeriesKey(expected.Identity.Series) == PostedInvoiceStore.SeriesKey(actual.Series)
            && (expected.Issued is not { } date || date == actual.Issued)
            && (expected.Template.Length == 0 || TextKey.Compact(expected.Template) == TextKey.Compact(actual.Template))
            && (PartyCatalogStore.TaxKey(expected.Buyer.TaxCode) is not { Length: > 0 } buyer
                || buyer == PartyCatalogStore.TaxKey(actual.BuyerTaxCode) || buyer == PartyCatalogStore.TaxKey(actual.BuyerIdNumber));
    }

    private static bool SafeName(string name)
        => !name.StartsWith('/') && !name.StartsWith('\\') && !name.Contains(':')
            && !name.Replace('\\', '/').Split('/').Any(part => part is ".." or ".");

    private static string Label(InvoiceDocument invoice)
        => $"{invoice.Identity.Series} {invoice.Identity.Number}".Trim() + (invoice.Issued is { } date ? $" ({date:dd/MM/yyyy})" : "");
}
