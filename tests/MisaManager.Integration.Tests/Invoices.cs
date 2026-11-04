using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using MisaManager.Core;

namespace MisaManager.Integration.Tests;

/// <summary>Typed invoices shaped like the Excel importer output, backed by a real source file (the runner hashes it).</summary>
public static class Invoices
{
    public static readonly DateOnly Day = new(2026, 10, 4);
    public static readonly Party Seller = new("CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT", "3300123456", "Huế", "", ValuePresence.Provided);
    public static readonly Party AV = new("CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V", "0107286506",
        "Số 19B, ngõ 42, phố Trịnh Đình Cửu", "A&V", ValuePresence.Provided, PartyKind.Organization);
    public static readonly Party Consumer = new("BÁN CHO NGƯỜI TIÊU DÙNG", "", "", "NTD", ValuePresence.NotApplicable, PartyKind.Individual);

    public static InvoiceLine Line(int index, string code, string name, string unit, decimal quantity, decimal unitPrice, string taxRate)
    {
        var amount = Math.Round(quantity * unitPrice, 0, MidpointRounding.AwayFromZero);
        var rate = decimal.Parse(taxRate.TrimEnd('%'), CultureInfo.InvariantCulture);
        var tax = Math.Round(amount * rate / 100m, 0, MidpointRounding.AwayFromZero);
        var row = 2 + index;
        var cells = ImmutableDictionary.CreateBuilder<string, SourceCell>();
        void Add(string field, int column, string value, string type = "number")
            => cells[field] = new SourceCell("Hóa đơn", row, column, (char)('A' + column - 1) + row.ToString(CultureInfo.InvariantCulture),
                value, type, "", null, value);
        Add("name", 6, name, "text");
        Add("unit", 7, unit, "text");
        Add("quantity", 8, quantity.ToString(CultureInfo.InvariantCulture));
        Add("unitPrice", 9, unitPrice.ToString(CultureInfo.InvariantCulture));
        Add("amount", 10, amount.ToString(CultureInfo.InvariantCulture));
        Add("taxRate", 11, taxRate, "text");
        Add("taxAmount", 12, tax.ToString(CultureInfo.InvariantCulture));
        return new InvoiceLine($"line:{row}", code, name, unit, quantity, unitPrice, amount, null, taxRate, tax, amount + tax,
            null, "", cells.ToImmutable());
    }

    public static readonly Party Supplier = new("CÔNG TY CỔ PHẦN PHÂN BÓN HUẾ", "3300555111", "12 Lê Lợi, Thành phố Huế", "", ValuePresence.Provided);
    public static readonly Party UnknownSupplier = new("CÔNG TY TNHH HÓA CHẤT MỚI", "3300999888", "Huế", "", ValuePresence.Provided);

    public static InvoiceDocument Build(string directory, string number, Party buyer, params InvoiceLine[] lines)
        => Create(directory, number, Seller, buyer, InvoiceKinds.Sales, "Tiền mặt", lines);

    /// <summary>A purchase invoice as the tax-portal importer builds it: the supplier is the seller, the shop the buyer.</summary>
    public static InvoiceDocument BuildPurchase(string directory, string number, Party supplier, params InvoiceLine[] lines)
        => Create(directory, number, supplier, Seller, InvoiceKinds.Purchase, InvoiceKinds.PurchasePayment, lines, template: "1");

    private static InvoiceDocument Create(string directory, string number, Party seller, Party buyer, string invoiceType,
        string payment, InvoiceLine[] lines, string template = "")
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, $"hoa-don-{number}.xlsx");
        File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(256));
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        var subtotal = lines.Sum(line => line.AmountBeforeTax ?? 0);
        var tax = lines.Sum(line => line.TaxAmount ?? 0);
        var invoice = new InvoiceDocument(Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(), 1, "",
            new InvoiceIdentity(seller.TaxCode, "1C26MLV", number, Day),
            new SourceReference(source, sha, "Hóa đơn", 1, 1 + lines.Length),
            seller, buyer, template, "VND", payment, Day, Day, Day, invoiceType, null, [.. lines],
            subtotal, tax, subtotal + tax, null, [], "", [], [],
            SourceIssues: [], Corrections: [], HeaderCells: ImmutableDictionary<string, SourceCell>.Empty, Signed: Day);
        invoice = invoice with { Issues = [.. InvoiceValidation.Validate(invoice)] };
        Assert.False(invoice.IsBlocked, string.Join(" | ", invoice.Issues.Select(issue => issue.Code + ": " + issue.Message)));
        return invoice with { Fingerprint = InvoiceFingerprint.Compute(invoice) };
    }
}
