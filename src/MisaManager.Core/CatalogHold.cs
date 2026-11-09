namespace MisaManager.Core;

/// <summary>
/// Invoices kept out of entry because a line without an item code matches several catalog codes with the same unit (same or
/// near-duplicate names) and the user chose more than one of them with <see cref="ItemCatalogStore.SetPreferred"/>; with no choice
/// the smallest code is used. Derived from the loaded catalog and never stored, so the hold lifts once only one code stays chosen
/// or the code is written in the invoice's "Mã hàng" column.
/// </summary>
public static class CatalogHold
{
    public const string Code = "ITEM_DUPLICATE_HOLD";

    public static IReadOnlyList<ImportIssue> Issues(InvoiceDocument invoice, ItemCatalogStore catalog)
    {
        if (catalog.Count == 0) return [];
        var issues = new List<ImportIssue>();
        var seen = new HashSet<(string, string)>();
        foreach (var line in invoice.Lines.Where(line => line.ItemCode.Trim().Length == 0))
        {
            if (!seen.Add((ItemCatalogStore.ItemNameKey(line.Name), ItemCatalogStore.NameKey(line.Unit)))) continue;
            // Same Kg→Tấn allowance as the live run, so the hold covers exactly the lines that would stop as ITEM_AMBIGUOUS.
            var duplicates = catalog.Duplicates(line.Name, line.Unit, allowKgToTon: true);
            if (duplicates.Count == 0) continue;
            var names = duplicates.Select(item => item.Name.Trim()).Distinct(StringComparer.Ordinal).Count() == 1
                ? string.Join(", ", duplicates.Select(item => item.Code))
                : string.Join(", ", duplicates.Select(item => $"{item.Code} '{item.Name.Trim()}'"));
            issues.Add(new(Code, IssueSeverity.Blocking,
                $"Tạm hoãn: mặt hàng '{line.Name.Trim()}' ({line.Unit.Trim()}) trùng tên nhiều mã cùng ĐVT và đang chọn nhiều mã dùng: {names}.",
                InvoiceId: invoice.InvoiceId,
                Resolution: "Chỉ giữ một mã (••• → Chọn mã dùng khi trùng tên), hoặc ghi mã vào cột 'Mã hàng' của file Excel hóa đơn."));
        }
        return issues;
    }
}
