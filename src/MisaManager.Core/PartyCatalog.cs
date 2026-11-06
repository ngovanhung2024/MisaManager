using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MisaManager.Core;

public enum PartyRole { Customer, Supplier }

public sealed record PartyCatalogEntry(string Code, string Name, string TaxCode = "", string Address = "");

/// <param name="Created">Customers the tool created in MISA ("Thêm Khách hàng") since the customer list was last loaded.</param>
public sealed record PartyCatalogFile(int SchemaVersion, DateTimeOffset UpdatedAt, string? CustomerSource, string? SupplierSource,
    IReadOnlyList<PartyCatalogEntry> Customers, IReadOnlyList<PartyCatalogEntry> Suppliers,
    IReadOnlyList<PartyCatalogEntry>? Created = null);

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
    private IReadOnlyList<PartyCatalogEntry> created = [];
    // Export plus created customers: what MISA has as far as the tool knows.
    private Index customerLookup = Index.Empty;

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
        created = (file.Created ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry.Code)).ToArray();
        RebuildLookup();
    }

    public string FilePath { get; }
    public string? CustomerSource { get; private set; }
    public string? SupplierSource { get; private set; }
    public int CustomerCount { get { lock (gate) return customers.Entries.Count; } }
    public int SupplierCount { get { lock (gate) return suppliers.Entries.Count; } }
    public int CreatedCount { get { lock (gate) return created.Count; } }
    public IReadOnlyList<PartyCatalogEntry> CreatedSnapshot() { lock (gate) return created.ToArray(); }
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

    // Household-business forms put before the owner's name on invoices but not in the MISA list (ASCII-folded words).
    // Company forms (Công ty TNHH/CP, Cửa hàng, Đại lý…) stay: "CÔNG TY TNHH ABC" and "CÔNG TY CP ABC" are different parties.
    private static readonly string[][] FormPrefixes = new[]
    {
        "HO KINH DOANH CA THE", "HO KINH DOANH", "HO KD", "H K D", "HKD", "HO CA THE"
    }.Select(phrase => phrase.Split(' ')).OrderByDescending(words => words.Length).ToArray();

    private const int MinCoreLength = 5;

    private static int SkipForms(string[] words)
    {
        var folded = words.Select(word => BuyerCodes.Ascii(word).Replace(" ", "", StringComparison.Ordinal)).ToArray();
        var start = 0;
        while (FormPrefixes.FirstOrDefault(phrase => start + phrase.Length <= words.Length
                   && phrase.Select((word, index) => folded[start + index] == word).All(same => same)) is { } matched)
            start += matched.Length;
        return start;
    }

    /// <summary>
    /// Key of the name without leading business forms ("HỘ KINH DOANH LÊ THỊ HƯƠNG" = "LÊ THỊ HƯƠNG"), bracketed text and
    /// punctuation. Empty when too little is left to identify anyone: it is only a fallback after the exact and relaxed keys.
    /// </summary>
    public static string CoreKey(string? name)
    {
        var words = Separators().Split(Brackets().Replace(TextKey.Canonical(name), " ")).Where(word => word.Length > 0).ToArray();
        var key = TextKey.LettersAndDigits(string.Join(' ', words[SkipForms(words)..]));
        return key.Length < MinCoreLength ? "" : key;
    }

    public static string TaxKey(string? taxCode)
        => new((taxCode ?? "").Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Entries carrying this MST: suppliers first, customers only when no supplier has it.</summary>
    public IReadOnlyList<PartyCatalogEntry> FindByTax(string? taxCode)
    {
        var key = TaxKey(taxCode);
        if (key.Length == 0) return [];
        lock (gate)
            return suppliers.ByTax.GetValueOrDefault(key) is { Length: > 0 } found ? found
                : customerLookup.ByTax.GetValueOrDefault(key) ?? [];
    }

    /// <summary>Entry with this exact code (customers before suppliers), or null.</summary>
    public PartyCatalogEntry? FindByCode(string? code)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0) return null;
        Index[] lists;
        lock (gate) lists = [customerLookup, suppliers];
        return lists.SelectMany(list => list.Entries)
            .FirstOrDefault(entry => string.Equals(entry.Code, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A MISA export is the complete list of that kind, so it replaces the previous one; for customers that includes
    /// the customers the tool created earlier.
    /// </summary>
    public int Replace(PartyRole role, IEnumerable<PartyCatalogEntry> entries, string sourceFile)
    {
        var cleaned = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Code) && !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry with { Code = entry.Code.Trim(), Name = entry.Name.Trim(), TaxCode = (entry.TaxCode ?? "").Trim(),
                Address = (entry.Address ?? "").Trim() })
            .GroupBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        lock (gate)
        {
            var (oldCustomers, oldSuppliers, oldCustomerSource, oldSupplierSource, oldCreated) = (customers, suppliers, CustomerSource, SupplierSource, created);
            if (role == PartyRole.Customer)
            {
                (customers, CustomerSource) = (new Index(cleaned), sourceFile);
                created = [];
            }
            else (suppliers, SupplierSource) = (new Index(cleaned), sourceFile);
            RebuildLookup();
            try { SaveLocked(); }
            catch
            {
                (customers, suppliers, CustomerSource, SupplierSource, created) = (oldCustomers, oldSuppliers, oldCustomerSource, oldSupplierSource, oldCreated);
                RebuildLookup();
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
            var (oldCustomers, oldSuppliers, oldCustomerSource, oldSupplierSource, oldCreated) = (customers, suppliers, CustomerSource, SupplierSource, created);
            (customers, suppliers, CustomerSource, SupplierSource, created) = (Index.Empty, Index.Empty, null, null, []);
            RebuildLookup();
            try { SaveLocked(); }
            catch
            {
                (customers, suppliers, CustomerSource, SupplierSource, created) = (oldCustomers, oldSuppliers, oldCustomerSource, oldSupplierSource, oldCreated);
                RebuildLookup();
                throw;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Records a customer MISA accepted from the tool's "Thêm Khách hàng" dialog, so later invoices of the same buyer use
    /// its code until a fresh customer export lists it.
    /// </summary>
    public void AddCreated(PartyCatalogEntry entry)
    {
        var clean = entry with { Code = entry.Code.Trim(), Name = entry.Name.Trim(), TaxCode = (entry.TaxCode ?? "").Trim(),
            Address = (entry.Address ?? "").Trim() };
        if (clean.Code.Length == 0 || clean.Name.Length == 0) throw new ArgumentException("Khách hàng tạo mới cần mã và tên.", nameof(entry));
        lock (gate)
        {
            var old = created;
            created = [.. created.Where(item => !string.Equals(item.Code, clean.Code, StringComparison.OrdinalIgnoreCase)), clean];
            RebuildLookup();
            try { SaveLocked(); }
            catch
            {
                created = old;
                RebuildLookup();
                throw;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Code for a buyer the tool creates in MISA, following the accountants' own lists (never the MST): an organisation
    /// gets the distinctive part of its name, without legal form or line of business, in upper case with diacritics
    /// (CÔNG TY TNHH MỘT THÀNH VIÊN LINH NGỌC = LINH NGỌC); a person gets initials (HTT = Hoàng Trọng Tiếp).
    /// A number is appended when the code is already used. The code depends only on the buyer and the catalog, so a
    /// repeated attempt after an unclear Cất gets the same code and MISA refuses it as a duplicate.
    /// </summary>
    public string NewCustomerCode(Party buyer)
    {
        var stem = BuyerCodes.IsIndividual(buyer) ? Initials(WithoutForms(buyer.Name)) : OrganisationCode(buyer.Name);
        if (stem.Length == 0) stem = "KH";
        // Multi-word codes take " 2" like the lists (TH PHÚ XUÂN 2); initials take "2" (LTH2).
        var separator = stem.Contains(' ') ? " " : "";
        if (FindByCode(stem) is null) return stem;
        for (var number = 2; ; number++)
            if (FindByCode(stem + separator + number.ToString(CultureInfo.InvariantCulture)) is null)
                return stem + separator + number.ToString(CultureInfo.InvariantCulture);
    }

    // Leading words that name the kind of organisation, its line of business or an administrative unit (ASCII-folded).
    private static readonly string[][] OrganisationPrefixes = new[]
    {
        "CONG TY", "CTY", "TONG CONG TY", "CO PHAN", "CP", "TRACH NHIEM HUU HAN", "TNHH", "MOT THANH VIEN", "MTV",
        "HAI THANH VIEN TRO LEN", "NHA NUOC", "DOANH NGHIEP TU NHAN", "DNTN", "DOANH NGHIEP", "HOP TAC XA", "HTX",
        "CHI NHANH", "TAP DOAN", "TRUNG TAM", "VUON QUOC GIA", "VIEN", "TRUONG", "MAM NON", "TIEU HOC",
        "TRUNG HOC CO SO", "TRUNG HOC PHO THONG", "THCS", "THPT", "UBND", "UY BAN NHAN DAN", "NGAN HANG", "KHO BAC",
        "BAN QUAN LY", "SU NGHIEP CONG", "DICH VU", "NONG NGHIEP", "LAM NGHIEP", "SAN XUAT", "THUONG MAI", "XAY DUNG",
        "XAY LAP", "DAU TU", "KINH DOANH", "VAT TU", "XUAT NHAP KHAU", "PHAT TRIEN", "SXTM", "TMDV", "VA",
        "XA", "THI XA", "THI TRAN", "HUYEN", "PHUONG", "QUAN", "TINH", "THANH PHO", "TP"
    }.Select(phrase => phrase.Split(' ')).OrderByDescending(words => words.Length).ToArray();

    private static string OrganisationCode(string? name)
    {
        var words = CodeSeparators().Split(Brackets().Replace((name ?? "").Normalize(NormalizationForm.FormC), " ").ToUpperInvariant())
            .Where(word => word.Length > 0).ToArray();
        var folded = words.Select(word => BuyerCodes.Ascii(word).Replace(" ", "", StringComparison.Ordinal)).ToArray();
        var start = 0;
        while (OrganisationPrefixes.FirstOrDefault(phrase => start + phrase.Length <= words.Length
                   && phrase.Select((word, index) => folded[start + index] == word).All(same => same)) is { } matched)
            start += matched.Length;
        var rest = words[start..];
        if (rest.Length == 0) return Initials(name);
        var code = string.Join(' ', rest);
        return rest.Length <= 3 && code.Length <= 20 ? code : Initials(code);
    }

    // "HỘ KINH DOANH LÊ THỊ HƯƠNG" is coded by the person (LTH), not by the business form (HKDLTH).
    private static string WithoutForms(string? name)
    {
        var words = Brackets().Replace(name ?? "", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = SkipForms(words);
        return start < words.Length ? string.Join(' ', words[start..]) : name ?? "";
    }

    private static string Initials(string? name)
    {
        var words = BuyerCodes.Ascii(Brackets().Replace(name ?? "", " ")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = new string(words.Select(word => word[0]).ToArray());
        // MISA codes are at most 20 characters in the accountants' lists; keep generated ones short.
        return initials.Length > 20 ? initials[..20] : initials;
    }

    private void RebuildLookup()
    {
        var listed = customers.Entries.Select(entry => entry.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = created.Where(entry => !listed.Contains(entry.Code)).ToArray();
        customerLookup = extra.Length == 0 ? customers : new Index([.. customers.Entries, .. extra]);
    }

    /// <summary>
    /// Finds the code for a party name, preferring the most exact evidence: exact name, then tax code, then the relaxed
    /// name, then the name without its business form; customers before suppliers at each level. Returns null when nothing matches; throws when the best match
    /// is several different codes, so the tool never guesses.
    /// </summary>
    public PartyMatch? FindCode(string name, string? taxCode = null)
    {
        Index[] lists;
        lock (gate) lists = [customerLookup, suppliers];
        var tax = TaxKey(taxCode);
        var tiers = new (string Method, Func<Index, PartyCatalogEntry[]> Find)[]
        {
            ("trùng tên", index => index.ByName.GetValueOrDefault(NameKey(name)) ?? []),
            ("trùng mã số thuế", index => tax.Length == 0 ? [] : index.ByTax.GetValueOrDefault(tax) ?? []),
            ("tên gần đúng", index => index.ByRelaxedName.GetValueOrDefault(RelaxedKey(name)) ?? []),
            // A different MST means a different party, so a name without its business form never overrides it.
            ("tên bỏ hình thức kinh doanh", index => (index.ByCoreName.GetValueOrDefault(CoreKey(name)) ?? [])
                .Where(entry => tax.Length == 0 || TaxKey(entry.TaxCode) is var other && (other.Length == 0 || other == tax)).ToArray())
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
                    customers.Entries, suppliers.Entries, created.Count == 0 ? null : created), Json);
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

    // Like Separators but keeps "&" inside a word (A&V).
    [GeneratedRegex(@"[^\p{L}\p{Mn}\p{Nd}&]+", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSeparators();

    private sealed class Index
    {
        public static readonly Index Empty = new([]);

        public Index(IReadOnlyList<PartyCatalogEntry> entries)
        {
            Entries = entries;
            ByName = Group(entries, entry => NameKey(entry.Name));
            ByRelaxedName = Group(entries, entry => RelaxedKey(entry.Name));
            ByCoreName = Group(entries, entry => CoreKey(entry.Name));
            ByTax = Group(entries, entry => TaxKey(entry.TaxCode));
        }

        public IReadOnlyList<PartyCatalogEntry> Entries { get; }
        public Dictionary<string, PartyCatalogEntry[]> ByName { get; }
        public Dictionary<string, PartyCatalogEntry[]> ByRelaxedName { get; }
        public Dictionary<string, PartyCatalogEntry[]> ByCoreName { get; }
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
    public const string RetailCustomerCode = "KL";
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
    /// The buyer has no MISA code even after the customer list was searched, so the tool may create it ("Thêm Khách hàng").
    /// Requires a loaded customer list: without it every buyer would look new and be created twice.
    /// </summary>
    public static bool CanCreate(Party buyer, PartyCatalogStore? catalog)
        => catalog is { CustomerCount: > 0 } && buyer.Code.Trim().Length == 0 && buyer.Name.Trim().Length > 0
            && !IsConsumer(buyer.Name) && !IsAnonymous(buyer.Name);

    /// <summary>A 12-digit personal ID (CCCD) rather than a business MST; MISA keeps it in "Số CMND" for individuals.</summary>
    public static bool IsPersonalId(string? taxCode)
        => PartyCatalogStore.TaxKey(taxCode) is { Length: 12 } key && key.All(char.IsAsciiDigit);

    /// <summary>Upper-case ASCII letters, digits and spaces (tone marks removed, Đ → D).</summary>
    public static string Ascii(string? value) => new(TextKey.Canonical(value).Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
        .Select(c => c == 'Đ' ? 'D' : c).Where(c => c == ' ' || char.IsAsciiLetterOrDigit(c)).ToArray());

    /// <summary>
    /// The Excel code wins; consumer sales always use NTD; anonymous buyers always use KL ("Khách lẻ"); otherwise the
    /// customer/supplier catalog is searched by name.
    /// Returns null when no code is known; throws <see cref="AutomationException"/> when the catalog is ambiguous.
    /// </summary>
    public static BuyerCode? Resolve(Party buyer, PartyCatalogStore? catalog)
    {
        if (buyer.Code.Trim().Length > 0) return new(buyer.Code.Trim(), "Excel");
        if (IsConsumer(buyer.Name)) return new(ConsumerCode, "bán cho người tiêu dùng");
        if (IsAnonymous(buyer.Name))
            return new(RetailCustomerCode, $"người mua không cung cấp thông tin → {RetailCustomerName}", RetailCustomerName);
        return catalog?.FindCode(buyer.Name, buyer.TaxCode) is { } match
            ? new(match.Code, $"danh mục {PartyCatalogStore.Label(match.Role)} · {match.Method}", match.Name) : null;
    }

    private static string Plain(string? value) => new(TextKey.LettersAndDigits(value).Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(c => c == 'Đ' ? 'D' : c).ToArray());
}
