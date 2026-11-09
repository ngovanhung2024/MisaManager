using System.Globalization;
using System.Text.RegularExpressions;

namespace MisaManager.Core;

/// <summary>Quantities of one kind of goods in different units: kg, tấn and packs whose name states their weight.</summary>
public static partial class StockUnits
{
    private static readonly string Kilogram = ItemCatalogStore.NameKey("kg");
    private static readonly string Ton = ItemCatalogStore.NameKey("Tấn");
    private static readonly HashSet<string> Packs = new[] { "Gói", "Bao", "Túi" }.Select(ItemCatalogStore.NameKey).ToHashSet(StringComparer.Ordinal);

    public static bool IsKilogram(string? unit) => ItemCatalogStore.NameKey(unit) == Kilogram;
    public static bool IsTon(string? unit) => ItemCatalogStore.NameKey(unit) == Ton;

    /// <summary>Kilograms in one <paramref name="unit"/>: 1 for kg, 1000 for tấn, the stated weight of a pack ("Newcate 2.5WP - 1kg" by Gói).</summary>
    public static decimal? KilogramsPer(string? unit, string? name)
    {
        var key = ItemCatalogStore.NameKey(unit);
        if (key == Kilogram) return 1;
        if (key == Ton) return 1000;
        if (!Packs.Contains(key)) return null;
        var weights = WeightToken().Matches(TextKey.Canonical(name))
            .Select(match => decimal.Parse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture))
            .Where(weight => weight > 0).Distinct().ToArray();
        return weights.Length == 1 ? weights[0] : null;
    }

    /// <summary>
    /// Units of <paramref name="item"/> in one <paramref name="unit"/> of goods named <paramref name="name"/>: 1 for its main unit,
    /// MISA's registered ratio for a conversion unit (MISA converts with it), otherwise by weight; null when not convertible.
    /// </summary>
    public static decimal? Factor(string? unit, string? name, ItemCatalogEntry item)
    {
        if (ItemCatalogStore.NameKey(unit) == ItemCatalogStore.NameKey(item.Unit)) return 1;
        if (RegisteredRate(item, unit) is { } rate) return rate;
        return KilogramsPer(unit, name) is { } from && KilogramsPer(item.Unit, item.Name) is { } to ? from / to : null;
    }

    /// <summary>Main units per one <paramref name="unit"/> when MISA registers it as the item's conversion unit with a ratio.</summary>
    public static decimal? RegisteredRate(ItemCatalogEntry item, string? unit)
    {
        if (item.ConversionRate <= 0 || ItemCatalogStore.NameKey(unit) is not { Length: > 0 } key
            || ItemCatalogStore.NameKey(item.ConversionUnit) != key) return null;
        return item.ConversionOperation.Trim() == "*" ? item.ConversionRate : 1m / item.ConversionRate;
    }

    /// <summary>MISA accepts <paramref name="unit"/> on the item as is: its main unit or a registered conversion unit.</summary>
    public static bool Registered(ItemCatalogEntry item, string? unit)
    {
        var key = ItemCatalogStore.NameKey(unit);
        return key == ItemCatalogStore.NameKey(item.Unit) || item.ConversionUnit
            .Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => ItemCatalogStore.NameKey(value) == key);
    }

    /// <summary>"25kg", "50 Kg", "1,2kg" in canonical text.</summary>
    [GeneratedRegex(@"(?<![\p{Nd}.])(\p{Nd}+(?:\.\p{Nd}+)?)\s*KG(?![\p{L}\p{Mn}])", RegexOptions.CultureInvariant)]
    internal static partial Regex WeightToken();
}

/// <summary>
/// Which catalog codes are the same goods. Tier 1 (<see cref="SameItemKey"/>): equal after dropping the "Phân (bón)" prefix, a
/// short note in brackets (distributor or maker "(ĐN)", "(Nifit)", "(ĐN Nicotex)", packaging "(BM-BV)"), a trailing short tag
/// ("-TN", "PET-M") and, for goods counted by weight, the bag size ("(Bao 25kg)" = "(Bao 50kg)"). Tier 2 (<see cref="Similar"/>):
/// the same numbers with near-equal wording ("Newcat"/"Newcate", "Fuamy"/"Fu-army", "PET"/"PEPA"), used only when tier 1 runs out.
/// </summary>
public static partial class ItemFamilies
{
    // A longer note ("ĐN - chai tím - nhãn tím - nước trong") describes a variant, so it stays part of the name.
    private const int NoteWords = 2;
    // Nutrient marks ending a fertilizer name ("NPK 16-16-8-TE") are part of the product, not a tag.
    private static readonly HashSet<string> KeptTags = new[] { "TE", "S", "N", "P", "K", "MG", "CA", "ZN", "B", "BO", "CU", "FE", "MN", "SI" }
        .Select(TextKey.Canonical).ToHashSet(StringComparer.Ordinal);

    public static string SameItemKey(string? name, string? unit) => Profile(name, unit).Key;

    /// <summary>Same numbers (sizes, concentrations, NPK ratios) and unit kind, with one name's words inside the other's or a 1–2 letter spelling difference.</summary>
    public static bool Similar(string? leftName, string? leftUnit, string? rightName, string? rightUnit)
        => Similar(Profile(leftName, leftUnit), Profile(rightName, rightUnit));

    public static ItemProfile Profile(string? name, string? unit)
    {
        var (numbers, words, weight) = Parts(name, unit, NoteWords);
        var key = string.Join(".", numbers) + "|" + string.Join(" ", words.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            + "|" + (weight ? "W" : ItemCatalogStore.NameKey(unit));
        var (looseNumbers, looseWords, _) = Parts(name, unit, int.MaxValue);
        return new(key, looseNumbers, looseWords, weight, ItemCatalogStore.NameKey(unit));
    }

    public static bool Similar(ItemProfile left, ItemProfile right)
    {
        if (left.Weight != right.Weight || !left.Weight && left.Unit != right.Unit) return false;
        if (left.Numbers.Length == 0 || !left.Numbers.SequenceEqual(right.Numbers, StringComparer.Ordinal)) return false;
        if (left.Words.Length == 0 || right.Words.Length == 0) return false;
        var (small, large) = left.Words.Length <= right.Words.Length ? (left.Words, right.Words) : (right.Words, left.Words);
        if (small.ToHashSet(StringComparer.Ordinal).IsSubsetOf(large)) return true;
        var (a, b) = (string.Concat(left.Words), string.Concat(right.Words));
        var distance = Distance(a, b);
        return distance <= 2 && distance * 5 <= Math.Min(a.Length, b.Length);
    }

    private static (string[] Numbers, string[] Words, bool Weight) Parts(string? name, string? unit, int noteWords)
    {
        var weight = StockUnits.KilogramsPer(unit, name) is not null;
        var text = TextKey.Canonical(name);
        if (weight) text = StockUnits.WeightToken().Replace(text, " ");
        // Innermost brackets first: a short note without numbers goes, anything else stays as plain words.
        for (var previous = ""; previous != text;)
        {
            previous = text;
            text = Bracket().Replace(text, match =>
            {
                var inner = match.Groups[1].Value;
                return !inner.Any(char.IsDigit) && ItemCatalogStore.ItemWords(inner).Words.Length <= noteWords ? " " : " " + inner + " ";
            });
        }
        for (var tag = TrailingTag().Match(text); tag.Success && !KeptTags.Contains(tag.Groups[1].Value); tag = TrailingTag().Match(text))
            text = text[..tag.Index];
        var (numbers, words) = ItemCatalogStore.ItemWords(text);
        return (numbers, words, weight);
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    [GeneratedRegex(@"\(([^()]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Bracket();

    [GeneratedRegex(@"\s*-\s*((?:\p{L}\p{Mn}*){1,3})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingTag();
}

/// <summary>Tier-1 <see cref="Key"/> and the numbers/words <see cref="ItemFamilies.Similar(ItemProfile, ItemProfile)"/> compares.</summary>
public sealed record ItemProfile(string Key, string[] Numbers, string[] Words, bool Weight, string Unit);

public enum StockAllocationKind
{
    /// <summary>The code the name matches, which has the stock.</summary>
    Matched,
    /// <summary>Code written in the invoice Excel ("Mã hàng").</summary>
    Fixed,
    /// <summary>Another code of the same goods (tier 1), e.g. an original code still holding opening stock.</summary>
    Family,
    /// <summary>A code with near-equal name (tier 2), because the same-goods codes ran out.</summary>
    Similar,
    /// <summary>Covered only by goods bought after the sale date: stock is negative until then.</summary>
    Borrowed,
    /// <summary>No code of these goods has enough stock, even counting later purchases.</summary>
    Shortage,
    /// <summary>No catalog code at all: the tool would create one with no stock.</summary>
    Unmatched
}

/// <param name="MatchedCode">Code chosen from the name alone (what the tool did before balancing).</param>
/// <param name="Name">Name, unit, quantity and unit price to enter for <paramref name="Code"/>; converted when its unit differs.</param>
/// <param name="StockQuantity">Quantity in the main unit of <paramref name="Code"/>.</param>
/// <param name="Missing">Stock the code lacked at the sale date (Borrowed/Shortage).</param>
public sealed record StockLineAssignment(string InvoiceId, string LineId, string InvoiceNumber, DateOnly Date,
    string SourceName, string SourceUnit, decimal SourceQuantity, decimal? SourceUnitPrice, string? MatchedCode, string? Code,
    string Name, string Unit, decimal Quantity, decimal? UnitPrice, decimal StockQuantity, StockAllocationKind Kind, decimal Missing = 0)
{
    public bool CodeChanged => !string.Equals(Code, MatchedCode, StringComparison.OrdinalIgnoreCase);
    public bool Converted => Unit != SourceUnit || Quantity != SourceQuantity;
}

public sealed record StockCodeBalance(string Code, string Name, string Unit, decimal Opening, decimal Received, decimal Issued,
    decimal Lowest, DateOnly? LowestOn)
{
    public decimal Closing => Opening + Received - Issued;
}

public sealed record StockBalancePlan(DateOnly? From, IReadOnlyList<StockLineAssignment> Sales, IReadOnlyList<StockCodeBalance> Codes,
    IReadOnlyList<IReadOnlyList<ItemCatalogEntry>> Families, IReadOnlyList<string> Warnings, int Purchases, int UnresolvedPurchases)
{
    public IReadOnlyList<StockCodeBalance> NegativeCodes => [.. Codes.Where(code => code.Lowest < 0)];
}

/// <summary>
/// Chooses the code of every sales line so that no code's stock goes negative: opening stock, then purchases, minus sales, day by day.
/// Codes still holding stock at the start of the period are sold first until they run out (the line's own code first when it is
/// one), then the other codes of the same goods (smallest code first), then near-equal names. A line is never split: it goes to the
/// first code that can cover it entirely. Purchases keep the code the tool gives them, so they must be entered first.
/// </summary>
public static class StockBalancer
{
    private const decimal Tolerance = 0.000001m;

    public static StockBalancePlan Plan(ItemCatalogStore catalog, IReadOnlyList<OpeningStockEntry> opening, DateOnly? from,
        IEnumerable<InvoiceDocument> purchases, IEnumerable<InvoiceDocument> sales)
    {
        var warnings = new List<string>();
        var items = catalog.Snapshot().GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        var byCode = items.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
        var openingQuantity = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var missingOpening = new List<string>();
        foreach (var entry in opening.Where(entry => entry.Code.Trim().Length > 0))
        {
            var code = entry.Code.Trim();
            openingQuantity[code] = openingQuantity.GetValueOrDefault(code) + entry.Quantity;
            if (byCode.TryGetValue(code, out var item))
            {
                if (entry.Unit.Trim().Length > 0 && ItemCatalogStore.NameKey(entry.Unit) != ItemCatalogStore.NameKey(item.Unit))
                    warnings.Add($"Mã {code}: ĐVT tồn đầu kỳ '{entry.Unit}' khác ĐVT chính trong danh mục '{item.Unit}'.");
                continue;
            }
            missingOpening.Add(code);
            byCode[code] = new ItemCatalogEntry(code, entry.Name, entry.Unit);
            items.Add(byCode[code]);
        }
        if (missingOpening.Count > 0)
            warnings.Add($"{missingOpening.Count} mã có tồn đầu kỳ nhưng không có trong danh mục mã hàng đã nạp ({List(missingOpening)}): "
                + "xuất lại danh mục từ MISA và nạp lại.");
        foreach (var item in items)
        {
            var weightPair = StockUnits.IsTon(item.Unit) && StockUnits.IsKilogram(item.ConversionUnit)
                || StockUnits.IsKilogram(item.Unit) && StockUnits.IsTon(item.ConversionUnit);
            if (weightPair && item.ConversionRate > 0 && item.ConversionRate != 1000)
                warnings.Add($"Mã {item.Code} '{item.Name}': MISA quy đổi 1 {item.ConversionUnit} = "
                    + $"{(item.ConversionOperation.Trim() == "*" ? item.ConversionRate : 1m / item.ConversionRate).ToString("0.######", CultureInfo.InvariantCulture)} "
                    + $"{item.Unit} (tỷ lệ {item.ConversionRate.ToString(CultureInfo.InvariantCulture)}, đúng phải là 1000). "
                    + "Sửa tỷ lệ chuyển đổi của mã này trong MISA, xuất lại danh mục rồi cân đối lại.");
        }
        bool Original(ItemCatalogEntry item) => openingQuantity.GetValueOrDefault(item.Code) > 0;
        var profiles = items.ToDictionary(item => item.Code, item => ItemFamilies.Profile(item.Name, item.Unit), StringComparer.OrdinalIgnoreCase);
        var families = items.GroupBy(item => profiles[item.Code].Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(),
            StringComparer.Ordinal);

        ItemCatalogEntry? Match(InvoiceLine line)
        {
            try { return catalog.Find(line.Name, line.Unit, allowKgToTon: true) is { } found ? byCode.GetValueOrDefault(found.Code) ?? found : null; }
            catch (AutomationException) { return null; }
        }

        // ---- purchases: stock arrives on the code the tool enters it under ----
        var receipts = new List<(DateOnly Date, string Code, decimal Quantity)>();
        var unresolved = new List<string>();
        var before = 0;
        var purchaseCount = 0;
        foreach (var invoice in purchases)
        {
            if (DateOf(invoice) is not { } date) continue;
            if (from is { } start && date < start) { before++; continue; }
            purchaseCount++;
            foreach (var line in invoice.Lines.Where(line => line.Quantity > 0))
            {
                var code = line.ItemCode.Trim();
                var item = code.Length > 0 ? byCode.GetValueOrDefault(code) : Match(line);
                if (item is null) { unresolved.Add($"{line.Name.Trim()} ({line.Unit.Trim()})"); continue; }
                if (StockUnits.Factor(line.Unit, line.Name, item) is not { } factor)
                {
                    warnings.Add($"Mua vào HĐ {invoice.Identity.Number}: '{line.Name}' ĐVT '{line.Unit}' không quy đổi được sang ĐVT '{item.Unit}' của mã {item.Code}.");
                    continue;
                }
                receipts.Add((date, item.Code, line.Quantity!.Value * factor));
            }
        }
        if (unresolved.Count > 0)
            warnings.Add($"{unresolved.Count} dòng mua vào chưa có mã trong danh mục, chưa tính vào tồn ({List(unresolved.Distinct(StringComparer.Ordinal))}). "
                + "Nhập hóa đơn mua vào trước (tool tạo mã mới), nạp lại danh mục mã hàng rồi cân đối lại.");

        var received = receipts.GroupBy(receipt => receipt.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(receipt => receipt.Quantity), StringComparer.OrdinalIgnoreCase);
        var available = new Dictionary<string, decimal>(openingQuantity, StringComparer.OrdinalIgnoreCase);
        var remaining = items.Select(item => item.Code).Concat(received.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(code => code, code => openingQuantity.GetValueOrDefault(code) + received.GetValueOrDefault(code), StringComparer.OrdinalIgnoreCase);
        var issues = new List<(DateOnly Date, string Code, decimal Quantity)>();

        // ---- sales, day by day; the same day's purchases come first ----
        var lines = new List<(InvoiceDocument Invoice, InvoiceLine Line, int Index, DateOnly Date)>();
        foreach (var invoice in sales)
        {
            if (DateOf(invoice) is not { } date) continue;
            if (from is { } start && date < start) { before++; continue; }
            lines.AddRange(invoice.Lines.Select((line, index) => (invoice, line, index, date)).Where(entry => entry.line.Quantity > 0));
        }
        if (before > 0)
            warnings.Add($"Bỏ qua {before} hóa đơn trước ngày tồn đầu kỳ {from:dd/MM/yyyy}.");
        lines = [.. lines.OrderBy(entry => entry.Date).ThenBy(entry => NumberOrder(entry.Invoice.Identity.Number))
            .ThenBy(entry => entry.Invoice.Identity.Number, StringComparer.Ordinal).ThenBy(entry => entry.Index)];
        var arrivals = receipts.OrderBy(receipt => receipt.Date).ToArray();
        var next = 0;
        var candidates = new Dictionary<string, (ItemCatalogEntry? Own, ItemCatalogEntry[] Family, ItemCatalogEntry[] Similar)>(StringComparer.Ordinal);
        var assignments = new List<StockLineAssignment>();
        foreach (var (invoice, line, _, date) in lines)
        {
            for (; next < arrivals.Length && arrivals[next].Date <= date; next++)
                available[arrivals[next].Code] = available.GetValueOrDefault(arrivals[next].Code) + arrivals[next].Quantity;
            var quantity = line.Quantity!.Value;
            StockLineAssignment Assign(ItemCatalogEntry? matched, ItemCatalogEntry item, Entry entry, StockAllocationKind kind)
            {
                var missing = Math.Max(0, entry.StockQuantity - available.GetValueOrDefault(item.Code));
                available[item.Code] = available.GetValueOrDefault(item.Code) - entry.StockQuantity;
                remaining[item.Code] = remaining.GetValueOrDefault(item.Code) - entry.StockQuantity;
                issues.Add((date, item.Code, entry.StockQuantity));
                var name = matched is not null && string.Equals(matched.Code, item.Code, StringComparison.OrdinalIgnoreCase)
                    || ItemCatalogStore.SameItemName(line.Name, item.Name) ? line.Name : item.Name;
                return new(invoice.InvoiceId, line.LineId, invoice.Identity.Number, date, line.Name, line.Unit, quantity, line.UnitPrice,
                    matched?.Code, item.Code, name, entry.Unit, entry.Quantity, entry.UnitPrice, entry.StockQuantity, kind,
                    missing > Tolerance ? missing : 0);
            }
            if (line.ItemCode.Trim() is { Length: > 0 } fixedCode)
            {
                if (byCode.GetValueOrDefault(fixedCode) is { } item && EntryFor(line, item) is { } entry)
                    assignments.Add(Assign(item, item, entry, StockAllocationKind.Fixed));
                else
                    assignments.Add(new(invoice.InvoiceId, line.LineId, invoice.Identity.Number, date, line.Name, line.Unit, quantity,
                        line.UnitPrice, fixedCode, fixedCode, line.Name, line.Unit, quantity, line.UnitPrice, quantity, StockAllocationKind.Fixed));
                continue;
            }
            var signature = ItemCatalogStore.NameKey(line.Name) + "\u0001" + ItemCatalogStore.NameKey(line.Unit);
            if (!candidates.TryGetValue(signature, out var found))
            {
                var own = Match(line);
                var profile = ItemFamilies.Profile(line.Name, line.Unit);
                var ownProfile = own is null ? null : profiles.GetValueOrDefault(own.Code) ?? ItemFamilies.Profile(own.Name, own.Unit);
                var keys = new HashSet<string>(StringComparer.Ordinal) { profile.Key };
                if (ownProfile is not null) keys.Add(ownProfile.Key);
                var family = keys.SelectMany(key => families.GetValueOrDefault(key) ?? []).Append(own).OfType<ItemCatalogEntry>()
                    .DistinctBy(item => item.Code, StringComparer.OrdinalIgnoreCase).ToArray();
                var similar = items.Where(item => !family.Any(member => string.Equals(member.Code, item.Code, StringComparison.OrdinalIgnoreCase))
                    && (ItemFamilies.Similar(profile, profiles[item.Code])
                        || ownProfile is not null && ItemFamilies.Similar(ownProfile, profiles[item.Code]))).ToArray();
                found = (own, Ordered(family, own, Original), Ordered(similar, null, Original));
                candidates[signature] = found;
            }
            var options = found.Family.Select(item => (Item: item, Entry: EntryFor(line, item), Kind: StockAllocationKind.Family))
                .Concat(found.Similar.Select(item => (Item: item, Entry: EntryFor(line, item), Kind: StockAllocationKind.Similar)))
                .Where(option => option.Entry is not null).Select(option => (option.Item, Entry: option.Entry!, option.Kind)).ToArray();
            var chosen = options.Where(option => available.GetValueOrDefault(option.Item.Code) + Tolerance >= option.Entry.StockQuantity)
                .Select(option => (option.Item, option.Entry, Kind: string.Equals(option.Item.Code, found.Own?.Code, StringComparison.OrdinalIgnoreCase)
                    ? StockAllocationKind.Matched : option.Kind))
                .Concat(options.Where(option => remaining.GetValueOrDefault(option.Item.Code) + Tolerance >= option.Entry.StockQuantity)
                    .Select(option => (option.Item, option.Entry, Kind: StockAllocationKind.Borrowed)))
                .Cast<(ItemCatalogEntry Item, Entry Entry, StockAllocationKind Kind)?>().FirstOrDefault();
            if (chosen is { } pick) { assignments.Add(Assign(found.Own, pick.Item, pick.Entry, pick.Kind)); continue; }
            // Not enough anywhere: the line stays on its own code (or the first of its goods) and that code goes negative.
            var fallback = options.FirstOrDefault(option => string.Equals(option.Item.Code, found.Own?.Code, StringComparison.OrdinalIgnoreCase));
            if (fallback.Item is null && options.Length > 0) fallback = options[0];
            if (fallback.Item is not null) assignments.Add(Assign(found.Own, fallback.Item, fallback.Entry, StockAllocationKind.Shortage));
            else
                assignments.Add(new(invoice.InvoiceId, line.LineId, invoice.Identity.Number, date, line.Name, line.Unit, quantity, line.UnitPrice,
                    found.Own?.Code, found.Own?.Code, line.Name, line.Unit, quantity, line.UnitPrice, quantity,
                    found.Own is null ? StockAllocationKind.Unmatched : StockAllocationKind.Shortage, found.Own is null ? 0 : quantity));
        }

        var balances = Balances(byCode, openingQuantity, receipts, issues, opening);
        var groups = families.Values.Where(group => group.Length > 1)
            .Select(group => (IReadOnlyList<ItemCatalogEntry>)group.OrderBy(item => item.Code, ItemCatalogStore.CodeOrder).ToArray())
            .OrderBy(group => group[0].Code, ItemCatalogStore.CodeOrder).ToArray();
        return new(from, assignments, balances, groups, warnings, purchaseCount, unresolved.Count);
    }

    private sealed record Entry(string Unit, decimal Quantity, decimal? UnitPrice, decimal StockQuantity);

    /// <summary>
    /// The line entered under <paramref name="item"/>. MISA takes the line's own unit when it is registered on the item and the
    /// tool converts Kg to Tấn itself; any other unit is rewritten to the item's main unit, provided the quantity stays exact.
    /// </summary>
    private static Entry? EntryFor(InvoiceLine line, ItemCatalogEntry item)
    {
        if (StockUnits.Factor(line.Unit, line.Name, item) is not { } factor || factor <= 0) return null;
        var quantity = line.Quantity!.Value;
        var stock = quantity * factor;
        if (StockUnits.Registered(item, line.Unit) || StockUnits.IsKilogram(line.Unit) && StockUnits.IsTon(item.Unit))
            return new(line.Unit, quantity, line.UnitPrice, stock);
        if (decimal.Round(stock, 3) != stock) return null;
        var price = line.UnitPrice is { } unitPrice ? unitPrice / factor : (decimal?)null;
        return new(item.Unit, stock, price is { } value ? decimal.Round(value, 4) : null, stock);
    }

    private static ItemCatalogEntry[] Ordered(IEnumerable<ItemCatalogEntry> items, ItemCatalogEntry? own, Func<ItemCatalogEntry, bool> original)
    {
        var all = items.OrderBy(item => item.Code, ItemCatalogStore.CodeOrder).ToArray();
        bool Own(ItemCatalogEntry item) => own is not null && string.Equals(item.Code, own.Code, StringComparison.OrdinalIgnoreCase);
        var originals = all.Where(original).ToArray();
        var others = all.Where(item => !original(item)).ToArray();
        // Within each tier the line's own code comes first; tiers keep opening stock ahead of new purchases.
        return [.. originals.Where(Own), .. originals.Where(item => !Own(item)), .. others.Where(Own), .. others.Where(item => !Own(item))];
    }

    private static IReadOnlyList<StockCodeBalance> Balances(IReadOnlyDictionary<string, ItemCatalogEntry> byCode,
        IReadOnlyDictionary<string, decimal> opening, IEnumerable<(DateOnly Date, string Code, decimal Quantity)> receipts,
        IEnumerable<(DateOnly Date, string Code, decimal Quantity)> issues, IReadOnlyList<OpeningStockEntry> openingRows)
    {
        var moves = receipts.Select(receipt => (receipt.Date, receipt.Code, Delta: receipt.Quantity, Order: 0))
            .Concat(issues.Select(issue => (issue.Date, issue.Code, Delta: -issue.Quantity, Order: 1)))
            .GroupBy(move => move.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(move => move.Date).ThenBy(move => move.Order).ToArray(), StringComparer.OrdinalIgnoreCase);
        return opening.Keys.Concat(moves.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(code => code, ItemCatalogStore.CodeOrder)
            .Select(code =>
            {
                var start = opening.GetValueOrDefault(code);
                var (balance, lowest, lowestOn) = (start, start, (DateOnly?)null);
                foreach (var day in (moves.GetValueOrDefault(code) ?? []).GroupBy(move => move.Date))
                {
                    balance += day.Sum(move => move.Delta);
                    if (balance < lowest) (lowest, lowestOn) = (balance, day.Key);
                }
                var item = byCode.GetValueOrDefault(code);
                var row = openingRows.FirstOrDefault(entry => string.Equals(entry.Code.Trim(), code, StringComparison.OrdinalIgnoreCase));
                var codeMoves = moves.GetValueOrDefault(code) ?? [];
                return new StockCodeBalance(code, item?.Name ?? row?.Name ?? "", item?.Unit ?? row?.Unit ?? "", start,
                    codeMoves.Where(move => move.Order == 0).Sum(move => move.Delta), -codeMoves.Where(move => move.Order == 1).Sum(move => move.Delta),
                    lowest, lowestOn);
            }).ToArray();
    }

    /// <summary>Date the voucher is posted in MISA, which is what its stock follows.</summary>
    public static DateOnly? DateOf(InvoiceDocument invoice) => invoice.PostedDate ?? invoice.VoucherDate ?? invoice.Issued;

    private static long NumberOrder(string number)
        => long.TryParse(number.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : long.MaxValue;

    private static string List(IEnumerable<string> values, int take = 10)
    {
        var all = values.ToArray();
        return string.Join("; ", all.Take(take)) + (all.Length > take ? $"… (+{all.Length - take})" : "");
    }
}
