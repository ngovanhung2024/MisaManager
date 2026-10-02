using System.Text.Json;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed record WorkflowProfile(
    int SchemaVersion, string Id, string Revision, string VersionTitle, string Status, string Culture,
    string EditorId, string SalesScopeId, string ItemGridId, string SalesGridId,
    string ReceiptTab, string InvoiceTab, string ItemTab, string TaxTab,
    NativeSelector OpenVoucher, NativeSelector SaveVoucher, NativeSelector DiscardVoucher,
    NativeSelector ItemNewCode, NativeSelector ItemNewName, NativeSelector ItemNewUnit,
    NativeSelector ItemNewStock, NativeSelector ItemNewTaxRate, NativeSelector ItemNewSave,
    IReadOnlyDictionary<string, NativeSelector> Fields, IReadOnlyDictionary<string, string> Columns,
    IReadOnlyDictionary<string, string> SalesColumns)
{
    public static WorkflowProfile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "profiles", "misa-sme2023-r20.json");
        var profile = JsonSerializer.Deserialize<WorkflowProfile>(File.ReadAllText(path), ContractJson.Options)
            ?? throw new InvalidDataException("Profile MISA trống.");
        if (profile.SchemaVersion != 1 || profile.Id != MisaR20Profile.ProfileId || profile.Status is not ("live_pending" or "supported")
            || string.IsNullOrWhiteSpace(profile.Revision) || profile.Culture != "vi-VN")
            throw new InvalidDataException("Profile MISA không đúng schema hoặc identity.");
        if (new[] { profile.VersionTitle, profile.EditorId, profile.SalesScopeId, profile.ItemGridId, profile.SalesGridId,
                profile.ReceiptTab, profile.InvoiceTab, profile.ItemTab, profile.TaxTab }.Any(string.IsNullOrWhiteSpace)
            || profile.Fields is null || profile.Columns is null || profile.SalesColumns is null)
            throw new InvalidDataException("Profile thiếu scope, tab hoặc bảng ánh xạ native.");
        foreach (var key in new[] { "customer", "customer_name", "customer_tax", "voucher_date", "posted_date", "issued_date",
            "number", "series", "currency", "payment", "voucher_number", "subtotal", "tax_total", "grand_total" })
            if (!profile.Fields.TryGetValue(key, out var selector) || selector is null
                || string.IsNullOrWhiteSpace(selector.AutomationId) && string.IsNullOrWhiteSpace(selector.Name))
                throw new InvalidDataException("Profile thiếu field " + key);
        foreach (var selector in new[] { profile.OpenVoucher, profile.SaveVoucher, profile.DiscardVoucher })
            if (selector is null || selector.ControlType != "Button"
                || string.IsNullOrWhiteSpace(selector.AutomationId) && string.IsNullOrWhiteSpace(selector.Name))
                throw new InvalidDataException("Profile thiếu selector duy nhất cho hành động chứng từ.");
        foreach (var selector in new[] { profile.ItemNewCode, profile.ItemNewName, profile.ItemNewUnit,
                profile.ItemNewStock, profile.ItemNewTaxRate, profile.ItemNewSave })
            if (selector is null || string.IsNullOrWhiteSpace(selector.AutomationId) && string.IsNullOrWhiteSpace(selector.Name))
                throw new InvalidDataException("Profile item creation selectors are missing.");
        foreach (var selector in profile.Fields.Values.Concat(new[] { profile.OpenVoucher, profile.SaveVoucher, profile.DiscardVoucher,
                profile.ItemNewCode, profile.ItemNewName, profile.ItemNewUnit, profile.ItemNewStock,
                profile.ItemNewTaxRate, profile.ItemNewSave }))
            if (selector is null || selector.AutomationId is null || selector.Name is null || selector.ControlType is null
                || selector.ControlType.Length > 0 && !Enum.TryParse<FlaUI.Core.Definitions.ControlType>(selector.ControlType, out _))
                throw new InvalidDataException("Profile có selector không hợp lệ.");
        foreach (var key in new[] { "code", "name", "unit", "debit_account", "quantity", "unit_price", "amount", "tax_rate", "tax_amount" })
            if (!profile.Columns.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Profile thiếu column " + key);
        foreach (var key in new[] { "voucher_number", "number", "customer", "subtotal", "voucher_date" })
            if (!profile.SalesColumns.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Profile thiếu sales column " + key);
        if (profile.Columns.Values.Distinct(StringComparer.Ordinal).Count() != profile.Columns.Count
            || profile.SalesColumns.Values.Distinct(StringComparer.Ordinal).Count() != profile.SalesColumns.Count)
            throw new InvalidDataException("Profile có tiêu đề cột trùng nhau.");
        return profile;
    }
}
