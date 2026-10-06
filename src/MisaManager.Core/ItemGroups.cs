using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MisaManager.Core;

/// <summary>One row of MISA's "Danh sách nhóm vật tư, hàng hóa, dịch vụ" export (HH, DV, NVL…).</summary>
public sealed record ItemGroupEntry(string Code, string Name);

public sealed record ItemGroupFile(int SchemaVersion, DateTimeOffset UpdatedAt, string? Source, IReadOnlyList<ItemGroupEntry> Groups);

/// <summary>
/// Item groups loaded from MISA. New item codes start with the group code ("HH_12", "DV_03", "NVL_01"), so a group
/// is only used as a prefix when it exists in the loaded list; without a list every new code stays in the HH_ series.
/// </summary>
public sealed partial class ItemGroupStore
{
    public const string Goods = "HH";
    public const string Service = "DV";
    public const string Material = "NVL";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private IReadOnlyList<ItemGroupEntry> groups = [];

    public ItemGroupStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        if (!File.Exists(FilePath)) return;
        var file = JsonSerializer.Deserialize<ItemGroupFile>(File.ReadAllText(FilePath, Encoding.UTF8), Json)
            ?? throw new InvalidDataException("Danh sách nhóm vật tư, hàng hóa bị hỏng: " + FilePath);
        Source = file.Source;
        groups = file.Groups ?? [];
    }

    public string FilePath { get; }
    public string? Source { get; private set; }
    public int Count { get { lock (gate) return groups.Count; } }
    public event EventHandler? Changed;

    public IReadOnlyList<ItemGroupEntry> Snapshot() { lock (gate) return groups; }

    /// <summary>Group of a new item judged from its name and unit; falls back to HH when the guessed group is not loaded.</summary>
    public string GroupFor(string name, string unit)
    {
        var group = Classify(name, unit);
        lock (gate)
            return groups.FirstOrDefault(entry => string.Equals(entry.Code, group, StringComparison.OrdinalIgnoreCase))?.Code ?? Goods;
    }

    /// <summary>Prefix for <see cref="ItemCatalogStore.NextCode"/>, e.g. "DV_".</summary>
    public string CodePrefix(string name, string unit) => GroupFor(name, unit) + "_";

    /// <summary>Loaded group whose "CODE_" prefix starts <paramref name="itemCode"/>, or null when the code follows no group.</summary>
    public string? GroupOfCode(string itemCode)
    {
        // "HH _CAI" is how some MISA codes are written; the space does not leave the HH group.
        var code = Regex.Replace(itemCode.Trim(), @"\s+(?=_)", "");
        lock (gate)
            return groups.Select(group => group.Code).OrderByDescending(group => group.Length)
                .FirstOrDefault(group => code.StartsWith(group + "_", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Services are recognised by a time/occurrence unit or a fee/service wording at the start of the name, materials by
    /// "nguyên (vật) liệu"; everything else is goods. Names are compared without marks, so "Phí" matches but "Phân" does not.
    /// </summary>
    public static string Classify(string? name, string? unit)
    {
        var unitKey = Plain(unit).Replace(" ", "", StringComparison.Ordinal);
        if (unitKey is "LAN" or "GIO" or "NGAY" or "PHUT" or "THANG" or "CHUYEN" or "CA" or "CONG") return Service;
        var nameKey = Plain(name);
        if (ServiceName().IsMatch(nameKey)) return Service;
        if (MaterialName().IsMatch(nameKey)) return Material;
        return Goods;
    }

    /// <summary>A MISA export is the full group list at that time, so it replaces the previous one.</summary>
    public int Replace(IEnumerable<ItemGroupEntry> source, string sourceFile)
    {
        var cleaned = source.Select(entry => new ItemGroupEntry(entry.Code.Trim(), (entry.Name ?? "").Trim()))
            .Where(entry => entry.Code.Length > 0).DistinctBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase).ToArray();
        Update(cleaned, sourceFile);
        return cleaned.Length;
    }

    public void Clear() => Update([], null);

    private void Update(IReadOnlyList<ItemGroupEntry> next, string? source)
    {
        lock (gate)
        {
            var (oldGroups, oldSource) = (groups, Source);
            (groups, Source) = (next, source);
            try { SaveLocked(); }
            catch
            {
                (groups, Source) = (oldGroups, oldSource);
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
                JsonSerializer.Serialize(stream, new ItemGroupFile(1, DateTimeOffset.UtcNow, Source, groups), Json);
                stream.Flush(flushToDisk: true);
            }
            StateFiles.Commit(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string Plain(string? value)
        => new(TextKey.Canonical(value).Normalize(NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => c == 'Đ' ? 'D' : c).ToArray());

    [GeneratedRegex(@"^(DICH VU|CHI PHI|LE PHI|PHI|CUOC|VAN CHUYEN|TIEN CONG|NHAN CONG|THUE|SUA CHUA|BAO DUONG|LAP DAT|GIA CONG)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"^(NGUYEN VAT LIEU|NGUYEN LIEU|VAT LIEU|NVL)\b", RegexOptions.CultureInvariant)]
    private static partial Regex MaterialName();
}
