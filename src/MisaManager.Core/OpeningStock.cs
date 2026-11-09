using System.Text;
using System.Text.Json;

namespace MisaManager.Core;

/// <summary>One row of MISA's "Tổng hợp tồn kho" report: the "Đầu kỳ" quantity and value of a code, in its main unit.</summary>
public sealed record OpeningStockEntry(string Code, string Name, string Unit, decimal Quantity, decimal Value = 0);

public sealed record OpeningStockFile(int SchemaVersion, DateTimeOffset UpdatedAt, string? Source, DateOnly? AsOf,
    IReadOnlyList<OpeningStockEntry> Items);

/// <summary>
/// Stock at the start of the period (e.g. closing 2025 = opening 2026), loaded from MISA's inventory summary. Codes with a
/// positive quantity are the "original" codes whose stock is sold first by <see cref="StockBalancer"/>.
/// </summary>
public sealed class OpeningStockStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private IReadOnlyList<OpeningStockEntry> items = [];

    public OpeningStockStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        if (!File.Exists(FilePath)) return;
        var file = JsonSerializer.Deserialize<OpeningStockFile>(File.ReadAllText(FilePath, Encoding.UTF8), Json)
            ?? throw new InvalidDataException("Dữ liệu tồn kho đầu kỳ bị hỏng: " + FilePath);
        Source = file.Source;
        AsOf = file.AsOf;
        items = file.Items ?? [];
    }

    public string FilePath { get; }
    public string? Source { get; private set; }
    /// <summary>First day of the report period ("Từ ngày"); invoices before it are not part of the balance.</summary>
    public DateOnly? AsOf { get; private set; }
    public int Count { get { lock (gate) return items.Count; } }
    public event EventHandler? Changed;

    public IReadOnlyList<OpeningStockEntry> Snapshot() { lock (gate) return items; }

    /// <summary>A report is the whole stock at that date, so it replaces the previous one. Rows of one code are summed (several warehouses).</summary>
    public int Replace(IEnumerable<OpeningStockEntry> source, DateOnly? asOf, string sourceFile)
    {
        var merged = source.Where(entry => !string.IsNullOrWhiteSpace(entry.Code))
            .GroupBy(entry => entry.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with
            {
                Code = group.Key, Name = group.First().Name.Trim(), Unit = group.First().Unit.Trim(),
                Quantity = group.Sum(entry => entry.Quantity), Value = group.Sum(entry => entry.Value)
            })
            .ToArray();
        Update(merged, asOf, sourceFile);
        return merged.Length;
    }

    public void Clear() => Update([], null, null);

    private void Update(IReadOnlyList<OpeningStockEntry> next, DateOnly? asOf, string? source)
    {
        lock (gate)
        {
            var (oldItems, oldAsOf, oldSource) = (items, AsOf, Source);
            (items, AsOf, Source) = (next, asOf, source);
            try { SaveLocked(); }
            catch
            {
                (items, AsOf, Source) = (oldItems, oldAsOf, oldSource);
                throw;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new OpeningStockFile(1, DateTimeOffset.UtcNow, Source, AsOf, items), Json);
                stream.Flush(flushToDisk: true);
            }
            StateFiles.Commit(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
