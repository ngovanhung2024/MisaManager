namespace MisaManager.Core;

/// <summary>
/// Problems in the loaded item catalog: names shared by several codes (lines without a code use the smallest one unless the user
/// chose another; choosing several holds the invoice, see <see cref="CatalogHold"/>) and codes outside the item-code convention
/// (code prefix = item group code + "_").
/// </summary>
public static class CatalogAudit
{
    /// <summary>Codes sharing a name (by <see cref="ItemCatalogStore.ItemNameKey"/>) and primary unit.</summary>
    public static IReadOnlyList<IReadOnlyList<ItemCatalogEntry>> DuplicateGroups(IReadOnlyList<ItemCatalogEntry> items)
        => items.GroupBy(item => (Name: ItemCatalogStore.ItemNameKey(item.Name), Unit: ItemCatalogStore.NameKey(item.Unit)))
            .Where(group => group.Key.Name.Length > 0 && group.Count() > 1)
            .Select(group => (IReadOnlyList<ItemCatalogEntry>)group.ToArray()).ToArray();

    /// <param name="preferred">Codes chosen with <see cref="ItemCatalogStore.SetPreferred"/>; a group with exactly one is settled.</param>
    public static IReadOnlyList<string> Items(IReadOnlyList<ItemCatalogEntry> items, ItemGroupStore groups,
        IReadOnlySet<string>? preferred = null)
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
        const string hold = "Đang chọn nhiều hơn một mã dùng nên hóa đơn có mặt hàng này (không ghi mã) bị tạm hoãn: chỉ giữ một mã "
            + "(••• → Chọn mã dùng khi trùng tên), hoặc ghi mã vào cột 'Mã hàng' của file Excel hóa đơn.";
        foreach (var group in DuplicateGroups(items))
        {
            var chosen = preferred is null ? 0 : group.Count(item => preferred.Contains(item.Code));
            if (chosen == 1) continue;
            var smallest = group.Select(item => item.Code).Order(ItemCatalogStore.CodeOrder).First();
            var resolution = chosen > 1 ? hold
                : $"Dòng không ghi mã dùng mã số nhỏ nhất {smallest}; muốn dùng mã khác: ••• → Chọn mã dùng khi trùng tên, hoặc ghi mã vào "
                    + "cột 'Mã hàng' của file Excel hóa đơn.";
            var sameSpelling = group.Select(item => ItemCatalogStore.NameKey(item.Name)).Distinct(StringComparer.Ordinal).Count() == 1;
            findings.Add(sameSpelling
                ? $"Trùng tên cùng ĐVT '{group.First().Unit}': '{group.First().Name}' = {List(group.Select(item => item.Code))}. " + resolution
                : $"Tên gần trùng cùng ĐVT '{group.First().Unit}': {List(group.Select(item => $"{item.Code} '{item.Name}'"), 6)}. " + resolution);
        }
        return findings;
    }
}
