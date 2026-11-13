using System.Text;
using System.Text.Json;

namespace MisaManager.Core;

/// <summary>Fuel-only purchases (Lộc Thủy): diesel → nhập kho <see cref="Stock"/>/<see cref="StockAccount"/>, xăng/nhớt → không qua kho <see cref="ExpenseAccount"/>.</summary>
public sealed record FuelPurchaseRule(string Stock, string StockAccount, string ExpenseAccount);

/// <summary>
/// A company the tool enters invoices for. Each keeps its own catalogs and invoice lists (<see cref="CompanyProfileStore.DataFolder"/>)
/// and its own rules. <see cref="TaxCode"/> is the company's MST: the buyer of its purchase invoices, the seller of its sales.
/// </summary>
public sealed record CompanyProfile(string Id, string Name, string TaxCode = "", FuelPurchaseRule? Fuel = null)
{
    /// <summary>The fuel-only purchase rule, or null for the ordinary purchase flow. Needs the MST: it applies to that buyer only.</summary>
    public FuelPurchaseCompany? FuelRule()
        => Fuel is { } rule && PartyCatalogStore.TaxKey(TaxCode).Length > 0
            ? new(TaxCode.Trim(), Name.Trim(), rule.Stock.Trim(), rule.StockAccount.Trim(), rule.ExpenseAccount.Trim()) : null;

    public string Display => PartyCatalogStore.TaxKey(TaxCode).Length > 0 ? $"{Name} · MST {TaxCode}" : Name;
}

public sealed record CompanyProfileFile(int SchemaVersion, string? Active, IReadOnlyList<CompanyProfile> Companies);

/// <summary>
/// The companies (Lá Việt, Lộc Thủy…) and which one is in use, in <c>companies.json</c> under the app data folder.
/// Each company's catalogs live in <c>companies/&lt;id&gt;</c>; the first use of a default company copies the data the tool
/// kept before companies existed (the root files stay untouched), so nothing loaded earlier is lost.
/// </summary>
public sealed class CompanyProfileStore
{
    public const string MismatchCode = "COMPANY_MISMATCH";
    public const string LaVietId = "la-viet";
    public const string LocThuyId = "loc-thuy";

    public static IReadOnlyList<CompanyProfile> Defaults { get; } =
    [
        new(LaVietId, "Lá Việt"),
        new(LocThuyId, "Lộc Thủy", "3301661802", new FuelPurchaseRule("CTY", "152", "154"))
    ];

    /// <summary>Files and folders of the app data root that belong to one company.</summary>
    public static IReadOnlyList<string> CompanyFiles { get; } =
    [
        "item-catalog.json", "party-catalog.json", "posted-invoices.json", "posted-purchases.json", "item-groups.json",
        "opening-stock.json", "mode.txt", "import", "import-purchase"
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private List<CompanyProfile> companies;
    private string activeId;

    /// <param name="root">App data folder (%LOCALAPPDATA%\MisaManager).</param>
    public CompanyProfileStore(string root)
    {
        Root = Path.GetFullPath(root);
        FilePath = Path.Combine(Root, "companies.json");
        CompanyProfileFile? file = null;
        if (File.Exists(FilePath))
        {
            try { file = JsonSerializer.Deserialize<CompanyProfileFile>(File.ReadAllText(FilePath, Encoding.UTF8), Json); }
            catch (JsonException) { File.Move(FilePath, FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true); }
        }
        companies = (file?.Companies ?? []).Where(company => Valid(company.Id) && company.Name.Trim().Length > 0)
            .GroupBy(company => company.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        if (companies.Count == 0) companies = [.. Defaults];
        activeId = file?.Active is { } chosen && companies.Any(company => company.Id == chosen) ? chosen : FirstUse();
        if (file is null) Save();
    }

    public string Root { get; }
    public string FilePath { get; }
    public IReadOnlyList<CompanyProfile> Companies { get { lock (gate) return companies.ToArray(); } }
    public CompanyProfile Active { get { lock (gate) return companies.First(company => company.Id == activeId); } }

    /// <summary>Folder of the active company's catalogs and invoice lists; created (with the earlier data copied) on first use.</summary>
    public string ActiveFolder => DataFolder(Active);

    public string DataFolder(CompanyProfile company)
    {
        var folder = Path.Combine(Root, "companies", company.Id);
        if (!Directory.Exists(folder)) Seed(folder, legacy: Defaults.Any(standard => standard.Id == company.Id));
        return folder;
    }

    /// <summary>Makes this company the one in use; the caller restarts so every store loads its data.</summary>
    public void Select(string id)
    {
        lock (gate)
        {
            if (!companies.Any(company => company.Id == id)) throw new ArgumentException("Không có công ty " + id, nameof(id));
            var old = activeId;
            activeId = id;
            try { Save(); }
            catch { activeId = old; throw; }
        }
    }

    /// <summary>Adds a company (empty <see cref="CompanyProfile.Id"/>) or updates the one with that id.</summary>
    public CompanyProfile Upsert(CompanyProfile company)
    {
        var name = company.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("Cần tên công ty.", nameof(company));
        var tax = company.TaxCode.Trim();
        if (company.Fuel is not null && PartyCatalogStore.TaxKey(tax).Length == 0)
            throw new ArgumentException("Quy tắc chỉ nhập xăng dầu cần MST của công ty.", nameof(company));
        lock (gate)
        {
            if (companies.FirstOrDefault(other => other.Id != company.Id && SameTax(other.TaxCode, tax)) is { } taken)
                throw new ArgumentException($"MST {tax} đã thuộc công ty {taken.Name}.", nameof(company));
            var id = company.Id.Length > 0 ? company.Id : NewId(name);
            var clean = company with { Id = id, Name = name, TaxCode = tax };
            var old = companies;
            var index = companies.FindIndex(other => other.Id == id);
            companies = index < 0 ? [.. companies, clean] : [.. companies[..index], clean, .. companies[(index + 1)..]];
            try { Save(); }
            catch { companies = old; throw; }
            return clean;
        }
    }

    /// <summary>
    /// Message when the invoice belongs to another company than the active one (its MST is the active company's or no
    /// known company's); null when it may be entered. Entering it would use another company's catalogs and rules.
    /// </summary>
    public static string? Mismatch(InvoiceDocument invoice, CompanyProfile active, IEnumerable<CompanyProfile> companies)
    {
        var own = invoice.IsPurchase() ? invoice.Buyer : invoice.Seller;
        if (Head(own.TaxCode).Length == 0 || SameTax(own.TaxCode, active.TaxCode)) return null;
        var owner = companies.FirstOrDefault(company => company.Id != active.Id && SameTax(company.TaxCode, own.TaxCode));
        if (owner is null && Head(active.TaxCode).Length == 0) return null;
        var role = invoice.IsPurchase() ? "người mua" : "người bán";
        return $"Hóa đơn {invoice.Identity.Number}: {role} '{own.Name.Trim()}' (MST {own.TaxCode.Trim()})"
            + (owner is null ? "" : $" là công ty {owner.Name}") + $", nhưng tool đang chọn công ty {active.Display}. "
            + "Chọn đúng công ty ở góc trên bên phải rồi nạp lại hóa đơn.";
    }

    // A branch MST (3301661802-001) belongs to the company.
    private static string Head(string? taxCode) => PartyCatalogStore.TaxKey(taxCode) is { Length: > 10 } key ? key[..10] : PartyCatalogStore.TaxKey(taxCode);
    private static bool SameTax(string? left, string? right) => Head(left).Length > 0 && Head(left) == Head(right);

    // Before companies existed the data belonged to whichever company was entered last: purchase mode was Lộc Thủy's.
    private string FirstUse()
    {
        try
        {
            var mode = Path.Combine(Root, "mode.txt");
            if (File.Exists(mode) && File.ReadAllText(mode).Trim() == "Purchase"
                && companies.Any(company => company.Id == LocThuyId))
                return LocThuyId;
        }
        catch (IOException) { }
        return companies[0].Id;
    }

    // Lá Việt and Lộc Thủy start from the data kept before companies existed; a company added later starts empty.
    private void Seed(string folder, bool legacy)
    {
        var staging = folder + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var name in legacy ? CompanyFiles : [])
            {
                var source = Path.Combine(Root, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(staging, name));
                else if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(staging, name));
            }
            Directory.Move(staging, folder);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private string NewId(string name)
    {
        var stem = string.Join('-', BuyerCodes.Ascii(name).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (stem.Length == 0) stem = "cong-ty";
        if (stem.Length > 40) stem = stem[..40];
        var id = stem;
        for (var number = 2; companies.Any(company => string.Equals(company.Id, id, StringComparison.OrdinalIgnoreCase)); number++)
            id = stem + "-" + number;
        return id;
    }

    private static bool Valid(string? id) => !string.IsNullOrWhiteSpace(id) && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private void Save()
    {
        Directory.CreateDirectory(Root);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new CompanyProfileFile(1, activeId, companies), Json);
                stream.Flush(flushToDisk: true);
            }
            StateFiles.Commit(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
