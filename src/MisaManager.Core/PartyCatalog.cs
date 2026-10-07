using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MisaManager.Core;

public enum PartyRole { Customer, Supplier }

public sealed record PartyCatalogEntry(string Code, string Name, string TaxCode = "", string Address = "");

public sealed record PartyCatalogFile(int SchemaVersion, DateTimeOffset UpdatedAt, string? CustomerSource, string? SupplierSource,
    IReadOnlyList<PartyCatalogEntry> Customers, IReadOnlyList<PartyCatalogEntry> Suppliers);

/// <summary>A catalog code found for a name; <paramref name="Method"/> says how (shown to the user).</summary>
public sealed record PartyMatch(string Code, string Name, PartyRole Role, string Method);

/// <summary>
/// Customer and supplier codes exported from MISA ("Danh sách khách hàng/nhà cung cấp"). Used to find the MISA
/// code of an invoice buyer by name: every comparison goes through <see cref="TextKey"/> so spelling variants
/// (tone placement, Đ look-alikes, spacing, invisible characters, case) never cause a miss.
/// </summary>
public sealed partial class PartyCatalogStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private Index customers = Index.Empty;
    private Index suppliers = Index.Empty;

    public PartyCatalogStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        if (!File.Exists(FilePath)) return;
        var file = JsonSerializer.Deserialize<PartyCatalogFile>(File.ReadAllText(FilePath, Encoding.UTF8), Json)
            ?? throw new InvalidDataException("Danh mục khách hàng/nhà cung cấp bị hỏng: " + FilePath);
        CustomerSource = file.CustomerSource;
        SupplierSource = file.SupplierSource;
        customers = new(file.Customers ?? []);
        suppliers = new(file.Suppliers ?? []);
    }

    public string FilePath { get; }
    public string? CustomerSource { get; private set; }
    public string? SupplierSource { get; private set; }
    public int CustomerCount { get { lock (gate) return customers.Entries.Count; } }
    public int SupplierCount { get { lock (gate) return suppliers.Entries.Count; } }
    public event EventHandler? Changed;

    /// <summary>Exact comparison key: <see cref="TextKey.Compact"/>.</summary>
    public static string NameKey(string? name) => TextKey.Compact(name);

    /// <summary>
    /// Looser key for a second pass: text in brackets (ID numbers after a person's name) is dropped, common legal-form
    /// abbreviations are spelled out ("CTY CP" = "CÔNG TY CỔ PHẦN") and punctuation is ignored.
    /// </summary>
    public static string RelaxedKey(string? name)
    {
        var text = Brackets().Replace(TextKey.Canonical(name), " ");
        var words = Separators().Split(text).Where(word => word.Length > 0)
            .Select(word => Abbreviations.TryGetValue(word, out var full) ? full : word);
        return TextKey.LettersAndDigits(string.Join(' ', words));
    }

    public static string TaxKey(string? taxCode)
        => new((taxCode ?? "").Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Entry with this exact code (customers before suppliers), or null.</summary>
    public PartyCatalogEntry? FindByCode(string? code)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0) return null;
        Index[] lists;
        lock (gate) lists = [customers, suppliers];
        return lists.SelectMany(list => list.Entries)
            .FirstOrDefault(entry => string.Equals(entry.Code, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A MISA export is the complete list of that kind, so it replaces the previous one.</summary>
    public int Replace(PartyRole role, IEnumerable<PartyCatalogEntry> entries, string sourceFile)
    {
        var cleaned = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Code) && !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry with { Code = entry.Code.Trim(), Name = entry.Name.Trim(), TaxCode = (entry.TaxCode ?? "").Trim(),
                Address = (entry.Address ?? "").Trim() })
            .GroupBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        lock (gate)
        {
            var (oldCustomers, oldSuppliers, oldCustomerSource, oldSupplierSource) = (customers, suppliers, CustomerSource, SupplierSource);
            if (role == PartyRole.Customer) (customers, CustomerSource) = (new Index(cleaned), sourceFile);
            else (suppliers, SupplierSource) = (new Index(cleaned), sourceFile);
            try { SaveLocked(); }
            catch
            {
                (customers, suppliers, CustomerSource, SupplierSource) = (oldCustomers, oldSuppliers, oldCustomerSource, oldSupplierSource);
                throw;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return cleaned.Length;
    }

    public void Clear()
    {
        lock (gate)
        {
            var (oldCustomers, oldSuppliers, oldCustomerSource, oldSupplierSource) = (customers, suppliers, CustomerSource, SupplierSource);
            (customers, suppliers, CustomerSource, SupplierSource) = (Index.Empty, Index.Empty, null, null);
            try { SaveLocked(); }
            catch
            {
                (customers, suppliers, CustomerSource, SupplierSource) = (oldCustomers, oldSuppliers, oldCustomerSource, oldSupplierSource);
                throw;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Finds the code for a party name, preferring the most exact evidence: exact name, then tax code, then the relaxed
    /// name; customers before suppliers at each level. Returns null when nothing matches; throws when the best match
    /// is several different codes, so the tool never guesses.
    /// </summary>
    public PartyMatch? FindCode(string name, string? taxCode = null)
    {
        Index[] lists;
        lock (gate) lists = [customers, suppliers];
        var tax = TaxKey(taxCode);
        var tiers = new (string Method, Func<Index, PartyCatalogEntry[]> Find)[]
        {
            ("trùng tên", index => index.ByName.GetValueOrDefault(NameKey(name)) ?? []),
            ("trùng mã số thuế", index => tax.Length == 0 ? [] : index.ByTax.GetValueOrDefault(tax) ?? []),
            ("tên gần đúng", index => index.ByRelaxedName.GetValueOrDefault(RelaxedKey(name)) ?? [])
        };
        foreach (var (method, find) in tiers)
            for (var role = 0; role < lists.Length; role++)
            {
                var found = find(lists[role]);
                if (found.Length == 0) continue;
                if (found.Length > 1 && tax.Length > 0 && found.Where(entry => TaxKey(entry.TaxCode) == tax).ToArray() is { Length: 1 } narrowed)
                    found = narrowed;
                if (found.Length > 1)
                    throw new AutomationException("CUSTOMER_AMBIGUOUS",
                        $"Danh mục {Label((PartyRole)role)} có {found.Length} mã cùng tên '{name}' ({string.Join(", ", found.Select(entry => entry.Code))}). "
                        + "Hãy ghi đúng mã vào cột Mã khách hàng của file Excel.");
                return new(found[0].Code, found[0].Name, (PartyRole)role, method);
            }
        return null;
    }

    public static string Label(PartyRole role) => role == PartyRole.Customer ? "khách hàng" : "nhà cung cấp";

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new PartyCatalogFile(1, DateTimeOffset.UtcNow, CustomerSource, SupplierSource,
                    customers.Entries, suppliers.Entries), Json);
                stream.Flush(flushToDisk: true);
            }
            StateFiles.Commit(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static readonly Dictionary<string, string> Abbreviations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CTY"] = "CÔNG TY", ["CT"] = "CÔNG TY", ["CP"] = "CỔ PHẦN", ["CPHAN"] = "CỔ PHẦN", ["TNHH"] = "TRÁCH NHIỆM HỮU HẠN",
        ["MTV"] = "MỘT THÀNH VIÊN", ["DNTN"] = "DOANH NGHIỆP TƯ NHÂN", ["HTX"] = "HỢP TÁC XÃ", ["UBND"] = "ỦY BAN NHÂN DÂN",
        ["XNK"] = "XUẤT NHẬP KHẨU", ["TM"] = "THƯƠNG MẠI", ["DV"] = "DỊCH VỤ", ["SX"] = "SẢN XUẤT", ["XD"] = "XÂY DỰNG",
        ["TMDV"] = "THƯƠNG MẠI DỊCH VỤ", ["SXTM"] = "SẢN XUẤT THƯƠNG MẠI", ["NN"] = "NÔNG NGHIỆP", ["CN"] = "CHI NHÁNH"
    }.ToDictionary(pair => TextKey.Canonical(pair.Key), pair => TextKey.Canonical(pair.Value), StringComparer.Ordinal);

    [GeneratedRegex(@"\([^()]*\)|\[[^\[\]]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();

    [GeneratedRegex(@"[^\p{L}\p{Mn}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    private sealed class Index
    {
        public static readonly Index Empty = new([]);

        public Index(IReadOnlyList<PartyCatalogEntry> entries)
        {
            Entries = entries;
            ByName = Group(entries, entry => NameKey(entry.Name));
            ByRelaxedName = Group(entries, entry => RelaxedKey(entry.Name));
            ByTax = Group(entries, entry => TaxKey(entry.TaxCode));
        }

        public IReadOnlyList<PartyCatalogEntry> Entries { get; }
        public Dictionary<string, PartyCatalogEntry[]> ByName { get; }
        public Dictionary<string, PartyCatalogEntry[]> ByRelaxedName { get; }
        public Dictionary<string, PartyCatalogEntry[]> ByTax { get; }

        private static Dictionary<string, PartyCatalogEntry[]> Group(IEnumerable<PartyCatalogEntry> entries, Func<PartyCatalogEntry, string> key)
            => entries.Select(entry => (Key: key(entry), Entry: entry)).Where(item => item.Key.Length > 0)
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Entry).ToArray(), StringComparer.Ordinal);
    }
}

/// <summary>
/// The MISA customer code the tool types for an invoice buyer and where it came from. <paramref name="CatalogName"/> is
/// the name MISA fills in for that code when it was found in the customer/supplier list.
/// </summary>
public sealed record BuyerCode(string Code, string Source, string? CatalogName = null);

public static class BuyerCodes
{
    public const string ConsumerCode = "NTD";
    /// <summary>No MISA customer code is known for the buyer: the invoice is left unfinished, not reported as an error.</summary>
    public const string MissingCode = "CUSTOMER_CODE_MISSING";
    /// <summary>MISA customer used for buyers who gave no details (accountants' convention in the posted vouchers).</summary>
    public const string RetailCustomerName = "Khách lẻ";
    private static readonly string ConsumerKey = TextKey.LettersAndDigits("BÁN CHO NGƯỜI TIÊU DÙNG");
    private static readonly string[] AnonymousKeys =
        [Plain("Khách hàng không cung cấp thông tin"), Plain("Người mua không cung cấp thông tin")];

    public static bool IsConsumer(string? name) => TextKey.LettersAndDigits(name) == ConsumerKey;

    /// <summary>E-invoice placeholder for a buyer without name/MST, e.g. "Khách hàng không cung cấp thông tin".</summary>
    public static bool IsAnonymous(string? name) => AnonymousKeys.Contains(Plain(name));

    // Organisations must carry an MST; any other named buyer is taken as an individual, who may have none.
    private static readonly string[] OrganizationWords =
    [
        "CONG TY", "CTY", "HOP TAC XA", "HTX", "DOANH NGHIEP", "DNTN", "CHI NHANH", "TAP DOAN", "XI NGHIEP", "NHA MAY",
        "TRUNG TAM", "VUON QUOC GIA", "UBND", "UY BAN", "BAN QUAN LY", "BENH VIEN", "NGAN HANG", "HIEP HOI", "VAN PHONG"
    ];

    public static bool LooksLikeOrganization(string? name)
    {
        var words = " " + string.Join(' ', TextKey.Canonical(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => Plain(word)).Where(word => word.Length > 0)) + " ";
        return OrganizationWords.Any(phrase => words.Contains(" " + phrase + " ", StringComparison.Ordinal));
    }

    /// <summary>A person rather than an organisation; individuals may carry no MST.</summary>
    public static bool IsIndividual(Party buyer)
        => buyer.Kind != PartyKind.Organization && !LooksLikeOrganization(buyer.Name);

    /// <summary>
    /// The Excel code wins; consumer sales always use NTD; anonymous buyers use the catalog's "Khách lẻ"; otherwise the
    /// customer/supplier catalog is searched by name.
    /// Returns null when no code is known; throws <see cref="AutomationException"/> when the catalog is ambiguous.
    /// </summary>
    public static BuyerCode? Resolve(Party buyer, PartyCatalogStore? catalog)
    {
        if (buyer.Code.Trim().Length > 0) return new(buyer.Code.Trim(), "Excel");
        if (IsConsumer(buyer.Name)) return new(ConsumerCode, "bán cho người tiêu dùng");
        if (IsAnonymous(buyer.Name))
            return catalog?.FindCode(RetailCustomerName) is { } retail
                ? new(retail.Code, $"người mua không cung cấp thông tin → {retail.Name}", retail.Name) : null;
        return catalog?.FindCode(buyer.Name, buyer.TaxCode) is { } match
            ? new(match.Code, $"danh mục {PartyCatalogStore.Label(match.Role)} · {match.Method}", match.Name) : null;
    }

    private static string Plain(string? value) => new(TextKey.LettersAndDigits(value).Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(c => c == 'Đ' ? 'D' : c).ToArray());
}
