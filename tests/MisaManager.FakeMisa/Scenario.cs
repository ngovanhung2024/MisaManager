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
    /// <summary>Added to every computed line VAT, to simulate MISA rounding differently from the source.</summary>
    public decimal TaxOffset { get; init; }
    /// <summary>Item codes the new-item dialog refuses with "đã tồn tại" (created by another user meanwhile).</summary>
    public IReadOnlyList<string> TakenItemCodes { get; init; } = [];
    /// <summary>Also opens the "MISA SME 2023" login window next to the session window.</summary>
    public bool ShowLoginWindow { get; init; }

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
            TakenItemCodes = scenario.TakenItemCodes ?? []
        };
    }
}

public sealed record SavedLine(string Code, string Name, string Unit, string DebitAccount, string RevenueAccount,
    decimal Quantity, decimal UnitPrice, decimal Amount, string TaxRate, decimal TaxAmount);

public sealed record SavedVoucher(string VoucherNumber, string InvoiceNumber, string Series, string CustomerCode,
    string CustomerName, string CustomerTaxCode, string PostedDate, string VoucherDate, string IssuedDate, string Payment,
    IReadOnlyList<SavedLine> Lines, decimal Subtotal, decimal TaxTotal, decimal GrandTotal);

public sealed record StateFile(IReadOnlyList<SavedVoucher> Vouchers, IReadOnlyList<CatalogItem> CreatedItems);
