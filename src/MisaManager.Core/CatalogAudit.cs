namespace MisaManager.Core;

/// <summary>
/// Problems in the loaded item catalog: names shared by several codes (invoices using them are held, see
/// <see cref="CatalogHold"/>) and codes outside the item-code convention (code prefix = item group code + "_").
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
        if (groups.Count > 0)
        {
            var prefixes = string.Join(", ", groups.Snapshot().Select(group => group.Code + "_"));
            var outside = items.Where(item => groups.GroupOfCode(item.Code) is null).Select(item => $"'{item.Code}'").ToArray();
            if (outside.Length > 0)
                findings.Add($"{outside.Length} mã không bắt đầu bằng mã nhóm VTHH ({prefixes}): {List(outside)}. "
                    + "Tool vẫn dùng được các mã này; mã mới do tool tạo luôn theo nhóm.");
        }
        const string hold = "Hóa đơn có mặt hàng này (không ghi mã) được tạm hoãn, không tick nhập được: ngừng theo dõi mã thừa trong MISA "
            + "rồi nạp lại danh mục, hoặc ghi mã vào cột 'Mã hàng' của file Excel hóa đơn.";
        foreach (var group in items.GroupBy(item => (Name: ItemCatalogStore.ItemNameKey(item.Name), Unit: ItemCatalogStore.NameKey(item.Unit)))
                     .Where(group => group.Key.Name.Length > 0 && group.Count() > 1))
        {
            var sameSpelling = group.Select(item => ItemCatalogStore.NameKey(item.Name)).Distinct(StringComparer.Ordinal).Count() == 1;
            findings.Add(sameSpelling
                ? $"Trùng tên cùng ĐVT '{group.First().Unit}': '{group.First().Name}' = {List(group.Select(item => item.Code))}. " + hold
                : $"Tên gần trùng cùng ĐVT '{group.First().Unit}': {List(group.Select(item => $"{item.Code} '{item.Name}'"), 6)}. " + hold);
        }
        return findings;
    }
}
