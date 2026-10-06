using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MisaManager.Core;

public sealed record ItemCatalogEntry(string Code, string Name, string Unit, string ConversionUnit = "", bool UnitListIncomplete = false);

/// <summary>An item whose MISA "Cất" was (or may have been) clicked but whose creation is not yet proven.</summary>
public sealed record PendingItemCreation(string Code, string Name, string Unit, DateTimeOffset StartedAt, string? RunId = null);

public sealed record ItemCatalogFile(int SchemaVersion, DateTimeOffset UpdatedAt, string? SourceFile,
    IReadOnlyList<ItemCatalogEntry> Items, IReadOnlyList<PendingItemCreation>? Pending = null,
    IReadOnlyList<string>? ImportedCodes = null, IReadOnlyList<string>? VerifiedCodes = null);

/// <summary>
/// Local item-code database: MISA catalog export plus codes created by the tool.
/// Name lookups use <see cref="ItemNameKey"/>: case, whitespace, invisible characters, look-alike
/// punctuation, full-width forms and Vietnamese tone placement do not affect a match.
/// </summary>
public sealed partial class ItemCatalogStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private List<ItemCatalogEntry> items = [];
    private List<PendingItemCreation> pending = [];
    private HashSet<string>? importedCodes;
    private HashSet<string> verifiedCodes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ItemCatalogEntry[]> byName = new(StringComparer.Ordinal);

    public ItemCatalogStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        if (!File.Exists(FilePath)) return;
        var file = JsonSerializer.Deserialize<ItemCatalogFile>(File.ReadAllText(FilePath, Encoding.UTF8), Json)
            ?? throw new InvalidDataException("Database mã hàng bị hỏng: " + FilePath);
        SourceFile = file.SourceFile;
        UpdatedAt = file.UpdatedAt;
        pending = (file.Pending ?? []).Where(item => !string.IsNullOrWhiteSpace(item.Code)).ToList();
        importedCodes = file.ImportedCodes?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        verifiedCodes = (file.VerifiedCodes ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Replace(file.Items);
    }

    public string FilePath { get; }
    public string? SourceFile { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public int Count { get { lock (gate) return items.Count; } }
    public int PendingCount { get { lock (gate) return pending.Count; } }
    public event EventHandler? Changed;
    public bool HasImportedProvenance { get { lock (gate) return importedCodes is not null; } }

    public static string NameKey(string? value) => TextKey.Compact(value);

    /// <summary>
    /// Name identity: <see cref="TextKey.LettersAndDigits"/> plus wording that MISA and the e-invoice system spell
    /// differently for the same item: the packaging word of "(bao 25kg)", a bare distributor tag ("(ĐN)", trailing "ĐN"),
    /// "bón" of "phân bón" and "khuyến mãi"/"khuyến mại". Weights, sizes and variant descriptions are kept.
    /// </summary>
    public static string ItemNameKey(string? value)
    {
        var text = Regex.Replace(TextKey.Canonical(value), @"(?<=\()\s*BAO\s*(?=\d+(?:\.\d+)?\s*KG\s*\))", "", RegexOptions.CultureInvariant);
        text = DistributorTag().Replace(text, " ");
        text = FertilizerWord.Replace(text, Fertilizer);
        text = PromotionWord.Replace(text, Promotion);
        return TextKey.LettersAndDigits(text);
    }

    [GeneratedRegex(@"\(\s*ĐN\s*\)|(?<![\p{L}\p{Mn}\p{Nd}])ĐN\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DistributorTag();

    // Canonical text is decomposed with tones moved, so these words are matched in that same form.
    private static readonly string Fertilizer = TextKey.Canonical("PHÂN");
    private static readonly Regex FertilizerWord = Word(TextKey.Canonical("PHÂN BÓN"));
    private static readonly string Promotion = TextKey.Canonical("KHUYẾN MẠI");
    private static readonly Regex PromotionWord = Word(TextKey.Canonical("KHUYẾN MÃI"));
    private static Regex Word(string canonical)
        => new(@"(?<![\p{L}\p{Mn}])" + Regex.Escape(canonical) + @"(?![\p{L}\p{Mn}])", RegexOptions.CultureInvariant);

    public IReadOnlyList<ItemCatalogEntry> Snapshot() { lock (gate) return items.ToArray(); }
    public IReadOnlyList<PendingItemCreation> PendingSnapshot() { lock (gate) return pending.ToArray(); }

    public string CodeOrigin(string code)
    {
        lock (gate)
            return importedCodes?.Contains(code) == true ? "catalog_excel" : verifiedCodes.Contains(code) ? "verified_creation"
                : importedCodes is null ? "legacy_unknown" : "local_cache";
    }

    /// <summary>
    /// Returns null when the name is unknown. Throws when the name exists only with
    /// other units or stays ambiguous: the tool must not create a duplicate then.
    /// </summary>
    public ItemCatalogEntry? Find(string name, string unit, bool allowKgToTon = false)
    {
        var candidates = Candidates(name);
        if (candidates.Length == 0) return null;
        var sameUnit = SameUnit(candidates, unit, allowKgToTon);
        if (sameUnit.Length == 1) return sameUnit[0];
        if (sameUnit.Length > 1)
            throw new AutomationException("ITEM_AMBIGUOUS",
                $"Danh mục có {sameUnit.Length} mã cùng tên '{name}' và ĐVT '{unit}' ({string.Join(", ", sameUnit.Select(item => item.Code))}). "
                + "Hãy ghi đúng mã vào cột 'Mã hàng' của file Excel hóa đơn hoặc giữ lại một mã trong danh mục rồi nạp lại.");
        // Old exports may omit the unit column; a single unit-less match is still the same item.
        if (candidates.Length == 1 && candidates[0].Unit.Length == 0 && candidates[0].ConversionUnit.Length == 0)
            return candidates[0];
        throw new AutomationException("ITEM_UNIT_CONFLICT",
            $"Tên hàng '{name}' đã có mã {string.Join(", ", candidates.Select(item => $"{item.Code} (ĐVT {Describe(item)})"))} "
            + $"nhưng khác ĐVT '{unit}'. Tool không tự tạo mã mới để tránh trùng: hãy ghi đúng mã vào cột 'Mã hàng' của file Excel hóa đơn, "
            + "hoặc thêm ĐVT chuyển đổi cho mã đó trong MISA rồi xuất và nạp lại danh mục.");
    }

    /// <summary>
    /// Codes that share the name (by <see cref="ItemNameKey"/>, so near-duplicates such as "FUJI ONE 40EC 100CC PET" and
    /// "FUJI ONE 40EC-100CC PET" count too) and the unit; empty unless there are at least two.
    /// </summary>
    public IReadOnlyList<ItemCatalogEntry> Duplicates(string name, string unit, bool allowKgToTon = false)
    {
        var sameUnit = SameUnit(Candidates(name), unit, allowKgToTon);
        return sameUnit.Length > 1 ? sameUnit : [];
    }

    private ItemCatalogEntry[] Candidates(string name)
    {
        lock (gate)
        {
            var candidates = byName.GetValueOrDefault(ItemNameKey(name)) ?? [];
            var exported = candidates.Where(item => importedCodes?.Contains(item.Code) == true).ToArray();
            if (exported.Length > 0) return exported;
            // Retained legacy rows reserve codes, but cannot prove that MISA still maps them to these names.
            return importedCodes is null ? candidates : candidates.Where(item => verifiedCodes.Contains(item.Code)).ToArray();
        }
    }

    private static ItemCatalogEntry[] SameUnit(ItemCatalogEntry[] candidates, string unit, bool allowKgToTon)
    {
        var unitKey = NameKey(unit);
        bool HasUnit(ItemCatalogEntry item, string key) => NameKey(item.Unit) == key || NameKey(item.ConversionUnit) == key;
        var sameUnit = candidates.Where(item => HasUnit(item, unitKey)).ToArray();
        // The workflow converts Kg to the item's Tấn unit only when the user authorised it.
        if (sameUnit.Length == 0 && allowKgToTon && unitKey == "KG")
            sameUnit = candidates.Where(item => HasUnit(item, NameKey("Tấn"))).ToArray();
        return sameUnit;
    }

    /// <summary>The spelling MISA uses for a unit that differs only in case/spacing (e.g. "kg", "Chai 90 ml").</summary>
    public string? CanonicalUnit(string unit)
    {
        var key = NameKey(unit);
        if (key.Length == 0) return null;
        lock (gate)
            return items.SelectMany(item => new[] { item.Unit, item.ConversionUnit })
                .Where(value => value.Length > 0 && NameKey(value) == key)
                .GroupBy(value => value, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key).FirstOrDefault();
    }

    public ItemCatalogEntry? FindByCode(string? code)
    {
        var key = code?.Trim() ?? "";
        if (key.Length == 0) return null;
        lock (gate)
        {
            var matches = items.Where(item => string.Equals(item.Code.Trim(), key, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }

    /// <summary>
    /// Evidence (from the MISA export) that <paramref name="unit"/> is not among the units registered for
    /// <paramref name="code"/>, or null when the catalog cannot prove it. Only trusted when the export carried the
    /// "Đơn vị chuyển đổi" column (some item lists a conversion unit), otherwise conversions would be invisible.
    /// </summary>
    public string? UnitAbsenceProof(string code, string unit)
    {
        var unitKey = NameKey(unit);
        if (unitKey.Length == 0) return null;
        bool knowsConversions;
        lock (gate) knowsConversions = items.Any(item => item.ConversionUnit.Trim().Length > 0);
        var entry = FindByCode(code);
        if (!knowsConversions || entry is null || entry.UnitListIncomplete || entry.Unit.Trim().Length == 0) return null;
        var units = RegisteredUnits(entry);
        if (units.Any(value => NameKey(value) == unitKey)) return null;
        return $"ItemCatalog:{entry.Code}:" + JsonSerializer.Serialize(units);
    }

    private static string[] RegisteredUnits(ItemCatalogEntry entry)
        => [entry.Unit.Trim(), .. entry.ConversionUnit.Split([',', ';', '|', '\n', '\r'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public PendingItemCreation? FindPending(string name, string unit)
    {
        var nameKey = ItemNameKey(name);
        var unitKey = NameKey(unit);
        lock (gate)
        {
            var matches = pending.Where(item => ItemNameKey(item.Name) == nameKey && NameKey(item.Unit) == unitKey).ToArray();
            if (matches.Length > 1)
                throw new AutomationException("ITEM_CREATE_UNCERTAIN",
                    $"Có {matches.Length} lần tạo mã chưa xác nhận cho '{name}' ({string.Join(", ", matches.Select(item => item.Code))}). "
                    + "Kiểm tra danh mục Vật tư trong MISA, xuất lại danh mục và nạp lại trước khi chạy tiếp.");
            return matches.FirstOrDefault();
        }
    }

    /// <summary>Durably records the code before MISA's "Cất" is clicked so a rerun looks it up instead of creating it again.</summary>
    public void BeginPending(PendingItemCreation entry)
    {
        lock (gate)
        {
            if (items.Any(item => string.Equals(item.Code, entry.Code, StringComparison.OrdinalIgnoreCase))
                || pending.Any(item => string.Equals(item.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
                throw new AutomationException("ITEM_CODE_EXISTS", $"Mã hàng '{entry.Code}' đã có trong database.");
            Transact(() => pending.Add(entry));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Promotes a pending code after MISA proved the item exists.</summary>
    public void ConfirmPending(string code)
    {
        lock (gate)
        {
            var entry = pending.FirstOrDefault(item => string.Equals(item.Code, code, StringComparison.Ordinal))
                ?? throw new AutomationException("ITEM_PENDING_MISSING", $"Không có mã đang tạo '{code}'.");
            Transact(() =>
            {
                pending.Remove(entry);
                if (!items.Any(item => string.Equals(item.Code, code, StringComparison.Ordinal)))
                    Replace([.. items, new ItemCatalogEntry(entry.Code, entry.Name, entry.Unit)]);
                verifiedCodes = new HashSet<string>(verifiedCodes, StringComparer.OrdinalIgnoreCase) { code };
            });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops a pending code only when MISA explicitly rejected the save.</summary>
    public void CancelPending(string code)
    {
        lock (gate)
        {
            if (!pending.Any(item => string.Equals(item.Code, code, StringComparison.Ordinal))) return;
            Transact(() => pending.RemoveAll(item => string.Equals(item.Code, code, StringComparison.Ordinal)));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// File rows override the same code; codes created earlier by the tool are kept.
    /// A fresh MISA export also settles pending creations: present codes are known,
    /// absent ones started before the export was written were never created.
    /// </summary>
    public int Merge(IEnumerable<ItemCatalogEntry> imported, string sourceFile, bool reconcilePending = true)
    {
        DateTimeOffset? exportedAt = File.Exists(sourceFile) ? new DateTimeOffset(File.GetLastWriteTimeUtc(sourceFile), TimeSpan.Zero) : null;
        lock (gate)
        {
            var incoming = imported.Select(item => item with { Code = item.Code.Trim() })
                .GroupBy(item => item.Code, StringComparer.Ordinal).Select(group => group.First()).ToList();
            var codes = incoming.Select(item => item.Code.Trim()).ToHashSet(StringComparer.Ordinal);
            Transact(() =>
            {
                if (reconcilePending)
                    pending.RemoveAll(item => codes.Contains(item.Code) || exportedAt is { } at && at > item.StartedAt);
                Replace([.. incoming, .. items.Where(item => !codes.Contains(item.Code))]);
                importedCodes = codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                SourceFile = sourceFile;
            });
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
            Transact(() => Replace([.. items, entry]));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Empties the database, including codes created by the tool and pending creations: the user re-imports a fresh
    /// MISA export each session, which already contains them. Returns the number of removed rows.
    /// </summary>
    public int Clear()
    {
        int removed;
        lock (gate)
        {
            removed = items.Count;
            Transact(() =>
            {
                Replace([]);
                pending = [];
                verifiedCodes = new(StringComparer.OrdinalIgnoreCase);
                importedCodes = new(StringComparer.OrdinalIgnoreCase);
                SourceFile = null;
            });
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>Replaces cached rows only after a complete live MISA catalog read; unresolved saves stay pending.</summary>
    public void RefreshNative(IEnumerable<ItemCatalogEntry> verified)
    {
        lock (gate) Transact(() =>
        {
            Replace(verified);
            importedCodes = items.Select(item => item.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Next free code in the HH_n series, above every known and pending code. <paramref name="proposed"/>
    /// is MISA's own suggestion, which also reflects codes created by other users or sessions.
    /// </summary>
    public string NextCode(string prefix = "HH_", string? proposed = null)
    {
        lock (gate)
        {
            var used = items.Select(item => item.Code).Concat(pending.Select(item => item.Code))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var next = used.Select(code => CodeNumber(prefix, code)).Where(number => number is not null)
                .Select(number => number!.Value).DefaultIfEmpty(0).Max() + 1;
            if (CodeNumber(prefix, proposed) is { } misa && misa > next) next = misa;
            while (used.Contains(prefix + next.ToString("00", CultureInfo.InvariantCulture))) next++;
            return prefix + next.ToString("00", CultureInfo.InvariantCulture);
        }
    }

    private static long? CodeNumber(string prefix, string? code)
    {
        if (code is null) return null;
        var match = Regex.Match(code.Trim(), "^" + Regex.Escape(prefix) + "([0-9]{1,9})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string Describe(ItemCatalogEntry item)
        => item.ConversionUnit.Length == 0 ? (item.Unit.Length == 0 ? "?" : item.Unit) : item.Unit + "/" + item.ConversionUnit;

    private void Replace(IEnumerable<ItemCatalogEntry> entries)
    {
        items = entries.Where(item => !string.IsNullOrWhiteSpace(item.Code) && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => item with { Code = item.Code.Trim(), Name = item.Name.Trim(), Unit = (item.Unit ?? "").Trim(),
                ConversionUnit = (item.ConversionUnit ?? "").Trim() })
            .ToList();
        byName = items.GroupBy(item => ItemNameKey(item.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    }

    // Memory only changes when the file write succeeded, so a failed save (disk full, AV lock) cannot
    // leave a pending code recorded in memory but not on disk.
    private void Transact(Action change)
    {
        var (oldItems, oldByName, oldPending, oldSource, oldUpdated, oldImported, oldVerified) =
            (items, byName, pending.ToList(), SourceFile, UpdatedAt, importedCodes, verifiedCodes);
        try
        {
            change();
            SaveLocked();
        }
        catch
        {
            (items, byName, pending, SourceFile, UpdatedAt, importedCodes, verifiedCodes) =
                (oldItems, oldByName, oldPending, oldSource, oldUpdated, oldImported, oldVerified);
            throw;
        }
    }

    private void SaveLocked()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new ItemCatalogFile(1, UpdatedAt.Value, SourceFile, items, pending,
                    importedCodes?.Order(StringComparer.Ordinal).ToArray(), verifiedCodes.Order(StringComparer.Ordinal).ToArray()), Json);
                stream.Flush(flushToDisk: true);
            }
            StateFiles.Commit(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
