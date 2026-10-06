namespace MisaManager.Core;

/// <summary>
/// Problems in the loaded item catalog that would otherwise only surface mid-run (ITEM_AMBIGUOUS) or that break the
/// item-code convention (code prefix = item group code + "_").
/// </summary>
public static class CatalogAudit
{
    public static IReadOnlyList<string> Items(IReadOnlyList<ItemCatalogEntry> items, ItemGroupStore groups)
    {
        var findings = new List<string>();
        static string List(IEnumerable<string> values, int take = 12)
        {
            var all = values.ToArray();
            return string.Join(", ", all.Take(take)) + (all.Length > take ? $"… (+{all.Length - take})" : "");
        }
        var spaced = items.Where(item => item.Code.Any(char.IsWhiteSpace) && item.Code.Contains('_', StringComparison.Ordinal))
            .Select(item => $"'{item.Code}'").ToArray();
        if (spaced.Length > 0)
            findings.Add($"Mã có khoảng trắng trước/sau dấu _: {List(spaced)}. Nên sửa trong MISA (ví dụ 'HH _CAI' → 'HH_CAI').");
        if (groups.Count > 0)
        {
            var prefixes = string.Join(", ", groups.Snapshot().Select(group => group.Code + "_"));
            var outside = items.Where(item => groups.GroupOfCode(item.Code) is null).Select(item => $"'{item.Code}'").ToArray();
            if (outside.Length > 0)
                findings.Add($"{outside.Length} mã không bắt đầu bằng mã nhóm VTHH ({prefixes}): {List(outside)}. "
                    + "Tool vẫn dùng được các mã này; mã mới do tool tạo luôn theo nhóm.");
        }
        foreach (var group in items.GroupBy(item => (Name: ItemCatalogStore.ItemNameKey(item.Name), Unit: ItemCatalogStore.NameKey(item.Unit)))
                     .Where(group => group.Key.Name.Length > 0 && group.Count() > 1))
            findings.Add($"Trùng tên cùng ĐVT '{group.First().Unit}': '{group.First().Name}' = {List(group.Select(item => item.Code))}. "
                + "Hóa đơn có tên này mà không ghi mã sẽ dừng (ITEM_AMBIGUOUS): ghi mã vào cột Mã hàng hoặc ngừng theo dõi một mã.");
        return findings;
    }
}
