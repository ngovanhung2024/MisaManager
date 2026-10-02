using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MisaManager.Core;

public sealed record ItemCatalogEntry(string Code, string Name, string Unit, string ConversionUnit = "");

public sealed record ItemCatalogFile(int SchemaVersion, DateTimeOffset UpdatedAt, string? SourceFile,
    IReadOnlyList<ItemCatalogEntry> Items);

/// <summary>
/// Local item-code database: MISA catalog export plus codes created by the tool.
/// Lookups compare names ignoring case and whitespace only.
/// </summary>
public sealed partial class ItemCatalogStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private List<ItemCatalogEntry> items = [];
    private Dictionary<string, ItemCatalogEntry[]> byName = new(StringComparer.Ordinal);

    public ItemCatalogStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        if (!File.Exists(FilePath)) return;
        var file = JsonSerializer.Deserialize<ItemCatalogFile>(File.ReadAllText(FilePath, Encoding.UTF8), Json)
            ?? throw new InvalidDataException("Database mã hàng bị hỏng: " + FilePath);
        SourceFile = file.SourceFile;
        UpdatedAt = file.UpdatedAt;
        Replace(file.Items);
    }

    public string FilePath { get; }
    public string? SourceFile { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public int Count { get { lock (gate) return items.Count; } }
    public event EventHandler? Changed;

    public static string NameKey(string? value)
        => WhitespacePattern().Replace((value ?? "").Normalize(NormalizationForm.FormC), "").ToUpperInvariant();

    public IReadOnlyList<ItemCatalogEntry> Snapshot() { lock (gate) return items.ToArray(); }

    /// <summary>Returns null when the name is unknown; throws when the name and unit stay ambiguous.</summary>
    public ItemCatalogEntry? Find(string name, string unit)
    {
        ItemCatalogEntry[] candidates;
        lock (gate) candidates = byName.GetValueOrDefault(NameKey(name)) ?? [];
        if (candidates.Length <= 1) return candidates.FirstOrDefault();
        var unitKey = NameKey(unit);
        var sameUnit = candidates.Where(item => NameKey(item.Unit) == unitKey || NameKey(item.ConversionUnit) == unitKey).ToArray();
        if (sameUnit.Length == 1) return sameUnit[0];
        throw new AutomationException("ITEM_AMBIGUOUS",
            $"Danh mục mã hàng có {candidates.Length} mã cùng tên '{name}' ({string.Join(", ", candidates.Select(item => item.Code))}); "
            + "hãy giữ lại một mã trong file mã hàng rồi nạp lại.");
    }

    /// <summary>File rows override the same code; codes created earlier by the tool are kept.</summary>
    public int Merge(IEnumerable<ItemCatalogEntry> imported, string sourceFile)
    {
        lock (gate)
        {
            var incoming = imported.GroupBy(item => item.Code, StringComparer.Ordinal).Select(group => group.First()).ToList();
            var codes = incoming.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
            Replace([.. incoming, .. items.Where(item => !codes.Contains(item.Code))]);
            SourceFile = sourceFile;
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Count;
    }

    public void Add(ItemCatalogEntry entry)
    {
        lock (gate)
        {
            if (items.Any(item => string.Equals(item.Code, entry.Code, StringComparison.Ordinal)))
                throw new AutomationException("ITEM_CODE_EXISTS", $"Mã hàng '{entry.Code}' đã có trong database.");
            Replace([.. items, entry]);
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Next free code in the catalog's HH_n series.</summary>
    public string NextCode(string prefix = "HH_")
    {
        lock (gate)
        {
            var pattern = new Regex("^" + Regex.Escape(prefix) + "([0-9]+)$", RegexOptions.CultureInvariant);
            var used = items.Select(item => item.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var next = items.Select(item => pattern.Match(item.Code))
                .Where(match => match.Success && match.Groups[1].Value.Length <= 9)
                .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0).Max() + 1;
            while (used.Contains(prefix + next.ToString("00", CultureInfo.InvariantCulture))) next++;
            return prefix + next.ToString("00", CultureInfo.InvariantCulture);
        }
    }

    private void Replace(IEnumerable<ItemCatalogEntry> entries)
    {
        items = entries.Where(item => !string.IsNullOrWhiteSpace(item.Code) && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => item with { Code = item.Code.Trim(), Name = item.Name.Trim(), Unit = (item.Unit ?? "").Trim(),
                ConversionUnit = (item.ConversionUnit ?? "").Trim() })
            .ToList();
        byName = items.GroupBy(item => NameKey(item.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    }

    private void SaveLocked()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new ItemCatalogFile(1, UpdatedAt.Value, SourceFile, items), Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, FilePath, overwrite: true);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
