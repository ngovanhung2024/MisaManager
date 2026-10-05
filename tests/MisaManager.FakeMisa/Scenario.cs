using System.Text.Json;
using System.Text.Json.Serialization;

namespace MisaManager.FakeMisa;

/// <summary>Switches for behaviours real MISA shows in different situations.</summary>
public sealed record Scenario
{
    /// <summary>R20 normally closes the voucher after Cất; false keeps it open read-only with Cất disabled.</summary>
    public bool SaveClosesEditor { get; init; } = true;
    /// <summary>Vouchers already in the database (shown in the list, trigger the duplicate-number prompt).</summary>
    public IReadOnlyList<SavedVoucher> ExistingVouchers { get; init; } = [];
    /// <summary>Shows MISA's "tổng tiền thuế chênh lệch" prompt on Cất.</summary>
    public bool TaxRoundingPrompt { get; init; }
    /// <summary>Difference MISA reports in that prompt; "Có" adds it to the first line.</summary>
    public decimal TaxRoundingDifference { get; init; } = 1;
    /// <summary>Added to every computed line VAT, to simulate MISA rounding differently from the source.</summary>
    public decimal TaxOffset { get; init; }
    /// <summary>Item codes the new-item dialog refuses with "đã tồn tại" (created by another user meanwhile).</summary>
    public IReadOnlyList<string> TakenItemCodes { get; init; } = [];
    /// <summary>Extra rows used to model a live MISA catalog differing from a local export.</summary>
    public IReadOnlyList<CatalogItem> AdditionalItems { get; init; } = [];
    public bool IncompleteItemLookup { get; init; }
    /// <summary>Lookup builds showing voucher-style column captions instead of Mã/Tên/ĐVT chính.</summary>
    public bool ItemLookupUsesVoucherColumns { get; init; }
    public string? CreatedItemNameOverride { get; init; }
    /// <summary>Also opens the "MISA SME 2023" login window next to the session window.</summary>
    public bool ShowLoginWindow { get; init; }
    /// <summary>R20 builds whose "Hóa đơn" tab shows the buyer MST in a control the profile selector does not match.</summary>
    public bool CustomerTaxUnlisted { get; init; }
    /// <summary>
    /// User's hand on the mouse: "always" or "after_save". A click whose press is not preceded by the
    /// tool's own move to the same point (cursor placed separately) lands where the user moved the cursor.
    /// </summary>
    public string UserMouseNudge { get; init; } = "";
    /// <summary>User clicks other tabs mid-entry: header → "Phiếu xuất" after the posted date and the invoice number, detail → "Thuế" after the first item code.</summary>
    public bool UserSwitchesTabs { get; init; }
    /// <summary>The first "Đóng" after Cất is swallowed (a stray user click took it).</summary>
    public bool CloseSwallowedOnceAfterSave { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static Scenario Load(string? path)
    {
        var scenario = path is null ? null : JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), Json);
        return scenario is null ? new Scenario() : scenario with
        {
            ExistingVouchers = scenario.ExistingVouchers ?? [],
            TakenItemCodes = scenario.TakenItemCodes ?? [],
            AdditionalItems = scenario.AdditionalItems ?? []
        };
    }
}

public sealed record SavedLine(string Code, string Name, string Unit, string DebitAccount, string RevenueAccount,
    decimal Quantity, decimal UnitPrice, decimal Amount, string TaxRate, decimal TaxAmount);

public sealed record SavedVoucher(string VoucherNumber, string InvoiceNumber, string Series, string CustomerCode,
    string CustomerName, string CustomerTaxCode, string PostedDate, string VoucherDate, string IssuedDate, string Payment,
    IReadOnlyList<SavedLine> Lines, decimal Subtotal, decimal TaxTotal, decimal GrandTotal);

public sealed record StateFile(IReadOnlyList<SavedVoucher> Vouchers, IReadOnlyList<CatalogItem> CreatedItems);
