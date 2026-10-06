using System.Text;
using System.Text.Json;

namespace MisaManager.Core;

/// <summary>An invoice already posted (ghi sổ) in MISA: one row of MISA's "Danh sách bán hàng" export.</summary>
public sealed record PostedInvoiceEntry(string Number, string Series = "", string Voucher = "");

public sealed record PostedInvoiceFile(int SchemaVersion, DateTimeOffset UpdatedAt, string? Source,
    IReadOnlyList<PostedInvoiceEntry> Invoices);

/// <summary>
/// Invoices that MISA already has (menu "Bỏ hóa đơn đã ghi sổ"). They count as saved: the list shows them as such and
/// Thêm vào MISA never enters them. Matching is by invoice number (leading zeros ignored) and series when both sides have one.
/// </summary>
public sealed class PostedInvoiceStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private IReadOnlyList<PostedInvoiceEntry> entries = [];
    private Dictionary<string, string[]> byNumber = new(StringComparer.Ordinal);

    public PostedInvoiceStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        if (!File.Exists(FilePath)) return;
        var file = JsonSerializer.Deserialize<PostedInvoiceFile>(File.ReadAllText(FilePath, Encoding.UTF8), Json)
            ?? throw new InvalidDataException("Danh sách hóa đơn đã ghi sổ bị hỏng: " + FilePath);
        Source = file.Source;
        SetLocked(file.Invoices ?? []);
    }

    public string FilePath { get; }
    public string? Source { get; private set; }
    public int Count { get { lock (gate) return entries.Count; } }
    public event EventHandler? Changed;

    /// <summary>
    /// Series key without the leading form digit: MISA shows "1C26MLV" while a workbook may hold the form ("1") and the
    /// series ("C26MLV") in separate columns.
    /// </summary>
    public static string SeriesKey(string? series) => TextKey.Compact(series).TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    public bool Contains(InvoiceDocument invoice) => Contains(invoice.Identity.Number, invoice.Identity.Series);

    /// <summary>
    /// The invoices still to enter: posted ones are dropped with their issues, so an invoice MISA already has never
    /// shows up as an error.
    /// </summary>
    public (ImportResult Pending, int Posted) WithoutPosted(ImportResult result)
    {
        var posted = result.Invoices.Where(Contains).ToArray();
        if (posted.Length == 0) return (result, 0);
        var ids = posted.Select(invoice => invoice.InvoiceId).ToHashSet(StringComparer.Ordinal);
        var postedIssues = posted.SelectMany(invoice => invoice.Issues.IsDefault ? [] : invoice.Issues).ToHashSet();
        return (result with
        {
            Invoices = result.Invoices.Where(invoice => !ids.Contains(invoice.InvoiceId)).ToArray(),
            Issues = result.Issues.Where(issue => !(issue.InvoiceId is { } id && ids.Contains(id)) && !postedIssues.Contains(issue)).ToArray()
        }, posted.Length);
    }

    /// <summary>A missing series on either side matches any series, so an invoice is never entered twice for lack of it.</summary>
    public bool Contains(string? number, string? series)
    {
        var key = InvoiceLookup.NumberKey(number);
        if (key.Length == 0) return false;
        string[]? posted;
        lock (gate) posted = byNumber.GetValueOrDefault(key);
        if (posted is null) return false;
        var seriesKey = SeriesKey(series);
        return seriesKey.Length == 0 || posted.Any(item => item.Length == 0 || item == seriesKey);
    }

    /// <summary>A MISA sales list export is the register at that time, so it replaces the previous one.</summary>
    public int Replace(IEnumerable<PostedInvoiceEntry> source, string sourceFile)
    {
        var cleaned = source.Where(entry => InvoiceLookup.NumberKey(entry.Number).Length > 0)
            .Select(entry => entry with { Number = entry.Number.Trim(), Series = (entry.Series ?? "").Trim(), Voucher = (entry.Voucher ?? "").Trim() })
            .DistinctBy(entry => (InvoiceLookup.NumberKey(entry.Number), SeriesKey(entry.Series))).ToArray();
        Update(cleaned, sourceFile);
        return cleaned.Length;
    }

    public void Clear() => Update([], null);

    private void Update(IReadOnlyList<PostedInvoiceEntry> next, string? source)
    {
        lock (gate)
        {
            var (oldEntries, oldIndex, oldSource) = (entries, byNumber, Source);
            SetLocked(next);
            Source = source;
            try { SaveLocked(); }
            catch
            {
                (entries, byNumber, Source) = (oldEntries, oldIndex, oldSource);
                throw;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetLocked(IReadOnlyList<PostedInvoiceEntry> next)
    {
        entries = next;
        byNumber = next.GroupBy(entry => InvoiceLookup.NumberKey(entry.Number), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.Select(entry => SeriesKey(entry.Series)).Distinct().ToArray(), StringComparer.Ordinal);
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new PostedInvoiceFile(1, DateTimeOffset.UtcNow, Source, entries), Json);
                stream.Flush(flushToDisk: true);
            }
            StateFiles.Commit(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
