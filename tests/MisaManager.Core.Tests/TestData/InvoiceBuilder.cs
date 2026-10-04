using System.Collections.Immutable;
using System.Globalization;
using MisaManager.Import;

namespace MisaManager.Core.Tests.TestData;

/// <summary>Builds a valid typed invoice shaped like the importer output; tests then break one thing at a time.</summary>
internal sealed class InvoiceBuilder
{
    public static readonly string Sha256 = new('a', 64);
    public const string InvoiceId = "0123456789abcdef01234567";
    public const string Worksheet = "Hóa đơn";

    public static Party Organization { get; } = new("CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V", "0107286506",
        "Số 19B, ngõ 42, phố Trịnh Đình Cửu", "A&V", ValuePresence.Provided, PartyKind.Organization);

    public static Party Consumer { get; } = new("BÁN CHO NGƯỜI TIÊU DÙNG", "", "", "NTD", ValuePresence.NotApplicable, PartyKind.Individual);

    public static Party Seller { get; } = new("CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT", "3300123456", "Huế", "", ValuePresence.Provided);

    private Party buyer = Organization;
    private string number = "1000";
    private DateOnly? issued = new(2026, 10, 4);
    private ImmutableArray<InvoiceLine> lines = [Line(0, "Rông đỏ 200SL - Chai 100ml", "Chai", 22, 10_000m), Line(1, "Hesman 60WG 18g", "Gói", 38, 16_000m)];
    private (decimal? Subtotal, decimal? TaxTotal, decimal? GrandTotal)? totals;

    public InvoiceBuilder WithBuyer(Party value) { buyer = value; return this; }
    public InvoiceBuilder WithNumber(string value) { number = value; return this; }
    public InvoiceBuilder WithIssued(DateOnly? value) { issued = value; return this; }
    public InvoiceBuilder WithLines(params InvoiceLine[] value) { lines = [.. value]; return this; }
    public InvoiceBuilder WithTotals(decimal? subtotal, decimal? taxTotal, decimal? grandTotal) { totals = (subtotal, taxTotal, grandTotal); return this; }

    public static InvoiceDocument Valid() => new InvoiceBuilder().Build();

    public InvoiceDocument Build()
    {
        var (subtotal, taxTotal, grandTotal) = totals ?? (
            lines.Sum(line => line.AmountBeforeTax ?? 0),
            lines.Sum(line => line.TaxAmount ?? 0),
            lines.Sum(line => (line.AmountBeforeTax ?? 0) + (line.TaxAmount ?? 0)));
        var source = new SourceReference(Path.Combine(Path.GetTempPath(), "hoa-don-t10.xlsx"), Sha256, Worksheet, 1, 1 + lines.Length);
        var invoice = new InvoiceDocument(InvoiceId, 1, "", new InvoiceIdentity(Seller.TaxCode, "1C26MLV", number, issued), source,
            Seller, buyer, "", "VND", "Tiền mặt", issued, issued, issued, "Hóa đơn bán hàng", null, lines,
            subtotal, taxTotal, grandTotal, null, [], "", [], [],
            SourceIssues: [], Corrections: [], HeaderCells: ImmutableDictionary<string, SourceCell>.Empty, Signed: issued);
        invoice = invoice with { Issues = [.. InvoiceValidation.Validate(invoice)] };
        return invoice with { Fingerprint = InvoiceFingerprint.Compute(invoice) };
    }

    /// <summary>A line whose amount, tax and total are internally consistent (VAT rounded to whole đồng).</summary>
    public static InvoiceLine Line(int index, string name, string unit, decimal quantity, decimal unitPrice,
        string taxRate = "8%", decimal discount = 0)
    {
        var amount = quantity * unitPrice - discount;
        var rate = decimal.Parse(taxRate.Replace("%", "").Trim(), CultureInfo.InvariantCulture);
        var tax = Math.Round(amount * rate / 100m, MidpointRounding.AwayFromZero);
        return LineRaw(index, name, unit, quantity, unitPrice, amount, taxRate, tax, amount + tax, discount == 0 ? null : discount);
    }

    /// <summary>A line with every number given verbatim, for building inconsistent data on purpose.</summary>
    public static InvoiceLine LineRaw(int index, string name, string unit, decimal? quantity, decimal? unitPrice,
        decimal? amount, string taxRate, decimal? taxAmount, decimal? total, decimal? discount = null)
    {
        var row = 2 + index;
        var cells = ImmutableDictionary.CreateBuilder<string, SourceCell>();
        void Add(string field, int column, string? value, string type = "number")
            => cells[field] = new SourceCell(Worksheet, row, column, CellAddress.Format(column, row), value ?? "", type, "", null, value);
        Add("name", 6, name, "text");
        Add("unit", 7, unit, "text");
        Add("quantity", 8, Format(quantity));
        Add("unitPrice", 9, Format(unitPrice));
        Add("amount", 10, Format(amount));
        Add("taxRate", 11, taxRate, "text");
        Add("taxAmount", 12, Format(taxAmount));
        Add("lineTotal", 13, Format(total));
        return new InvoiceLine($"{InvoiceId}:line:{row}", "", name, unit, quantity, unitPrice, amount, discount, taxRate, taxAmount, total,
            null, "", cells.ToImmutable());
    }

    public static InvoiceLine WithFormula(InvoiceLine line, string field, string formula)
        => line with { Cells = line.Cells.SetItem(field, line.Cells[field] with { Formula = formula }) };

    private static string? Format(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
}
