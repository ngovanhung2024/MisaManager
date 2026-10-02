using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace MisaManager.Core;

public static class InvoiceCorrectionService
{
    public static IReadOnlyList<string> HeaderFields { get; } = ["buyer.code", "buyer.name", "buyer.taxCode", "buyer.taxPresence", "buyer.kind", "buyer.address",
        "seller.name", "seller.taxCode", "series", "number", "template", "issued", "voucherDate", "postedDate", "currency", "paymentMethod",
        "invoiceType", "exchangeRate", "subtotal", "taxTotal", "grandTotal", "notes"];
    public static IReadOnlyList<string> LineFields { get; } = ["name", "unit", "quantity", "unitPrice", "amount", "taxRate", "taxAmount", "lineTotal", "discount", "discountRate", "kind"];

    public static InvoiceCorrectionResult Apply(InvoiceDocument invoice, InvoiceCorrection correction)
    {
        if (correction.Provenance != "user_override" || string.IsNullOrWhiteSpace(correction.Confirmation))
            throw new InvalidDataException("Correction requires user_override and an explicit confirmation.");
        if (!string.Equals(invoice.InvoiceId, correction.InvoiceId, StringComparison.Ordinal)
            || invoice.Revision != correction.ExpectedRevision)
            throw new InvalidOperationException("Correction expected revision does not match the invoice.");
        var changes = new List<CorrectionChange>();
        var updated = invoice;
        var field = correction.Field.Trim();
        var value = correction.Value.Trim();
        if (string.IsNullOrWhiteSpace(field) || value.Length > 32768 || value.Any(char.IsControl)) throw new ArgumentException("Correction field/value is invalid.");

        string Text(string before, Func<string, InvoiceDocument> set)
        {
            if (before == value) return before;
            changes.Add(new(field, before, value, correction.Provenance));
            updated = set(value);
            return value;
        }
        decimal Number(string name)
        {
            if (!DecimalValue.TryParseInvariant(value, out var parsed)) throw new InvalidDataException(name + " must be an invariant decimal.");
            return parsed;
        }
        DateOnly Date(string name)
        {
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new InvalidDataException(name + " must use YYYY-MM-DD.");
            return parsed;
        }

        switch (field)
        {
            case "buyer.code": Text(invoice.Buyer.Code, v => updated with { Buyer = updated.Buyer with { Code = v } }); break;
            case "buyer.name": Text(invoice.Buyer.Name, v => updated with { Buyer = updated.Buyer with { Name = v } }); break;
            case "buyer.taxCode": Text(invoice.Buyer.TaxCode, v => updated with { Buyer = updated.Buyer with { TaxCode = v, TaxCodePresence = ValuePresence.Provided } }); break;
            case "buyer.taxPresence": Text(invoice.Buyer.TaxCodePresence.ToString(), v => updated with { Buyer = updated.Buyer with { TaxCodePresence = EnumValue<ValuePresence>(v) } }); break;
            case "buyer.kind": Text(invoice.Buyer.Kind.ToString(), v => updated with { Buyer = updated.Buyer with { Kind = EnumValue<PartyKind>(v) } }); break;
            case "buyer.address": Text(invoice.Buyer.Address, v => updated with { Buyer = updated.Buyer with { Address = v } }); break;
            case "seller.name": Text(invoice.Seller.Name, v => updated with { Seller = updated.Seller with { Name = v } }); break;
            case "seller.taxCode": Text(invoice.Seller.TaxCode, v => updated with { Seller = updated.Seller with { TaxCode = v }, Identity = updated.Identity with { SellerTaxCode = v } }); break;
            case "series": Text(invoice.Identity.Series, v => updated with { Identity = updated.Identity with { Series = v } }); break;
            case "number": Text(invoice.Identity.Number, v => updated with { Identity = updated.Identity with { Number = v } }); break;
            case "template": Text(invoice.Template, v => updated with { Template = v }); break;
            case "issued": Text(invoice.Issued?.ToString("yyyy-MM-dd") ?? "", _ => updated with { Issued = Date(field), Identity = updated.Identity with { Issued = Date(field) } }); break;
            case "voucherDate":
                var voucher = Date(field); changes.Add(new(field, invoice.VoucherDate?.ToString("yyyy-MM-dd") ?? "", value, correction.Provenance)); updated = updated with { VoucherDate = voucher }; break;
            case "postedDate":
                var posted = Date(field); changes.Add(new(field, invoice.PostedDate?.ToString("yyyy-MM-dd") ?? "", value, correction.Provenance)); updated = updated with { PostedDate = posted }; break;
            case "currency": Text(invoice.Currency, v => updated with { Currency = v }); break;
            case "paymentMethod": Text(invoice.PaymentMethod, v => updated with { PaymentMethod = v }); break;
            case "invoiceType": Text(invoice.InvoiceType ?? "", v => updated with { InvoiceType = v }); break;
            case "exchangeRate": Text(Format(invoice.ExchangeRate), _ => updated with { ExchangeRate = Number(field) }); break;
            case "subtotal": Text(Format(invoice.Subtotal), _ => updated with { Subtotal = Number(field) }); break;
            case "taxTotal": Text(Format(invoice.TaxTotal), _ => updated with { TaxTotal = Number(field) }); break;
            case "grandTotal": Text(Format(invoice.GrandTotal), _ => updated with { GrandTotal = Number(field) }); break;
            case "notes": Text(invoice.Notes, v => updated with { Notes = v }); break;
            default:
                if (!field.StartsWith("lines[", StringComparison.Ordinal) || !field.Contains("].", StringComparison.Ordinal))
                    throw new InvalidDataException("Unsupported correction field: " + field);
                var close = field.IndexOf("].", StringComparison.Ordinal);
                if (!int.TryParse(field.AsSpan(6, close - 6), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index < 0 || index >= invoice.Lines.Length) throw new InvalidDataException("Invalid correction line.");
                var lineField = field[(close + 2)..];
                var line = invoice.Lines[index];
                var changed = lineField switch
                {
                    "name" => line with { Name = value },
                    "unit" => line with { Unit = value },
                    "quantity" => line with { Quantity = Number(lineField) },
                    "unitPrice" => line with { UnitPrice = Number(lineField) },
                    "amount" => line with { AmountBeforeTax = Number(lineField) },
                    "taxRate" => line with { TaxRate = value },
                    "taxAmount" => line with { TaxAmount = Number(lineField) },
                    "lineTotal" => line with { TotalWithTax = Number(lineField) },
                    "discount" => line with { Discount = Number(lineField) },
                    "discountRate" => line with { DiscountRate = Number(lineField) },
                    "kind" => line with { Kind = value },
                    _ => throw new InvalidDataException("Unsupported correction field: " + field)
                };
                var before = lineField switch
                {
                    "name" => line.Name, "unit" => line.Unit,
                    "quantity" => line.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "",
                    "unitPrice" => line.UnitPrice?.ToString(CultureInfo.InvariantCulture) ?? "",
                    "amount" => line.AmountBeforeTax?.ToString(CultureInfo.InvariantCulture) ?? "",
                    "taxRate" => line.TaxRate, "taxAmount" => Format(line.TaxAmount), "lineTotal" => Format(line.TotalWithTax),
                    "discount" => Format(line.Discount), "discountRate" => Format(line.DiscountRate), "kind" => line.Kind, _ => ""
                };
                if (before == value) throw new InvalidDataException("Giá trị không thay đổi.");
                var sourceCell = line.Cells.GetValueOrDefault(lineField);
                changed = changed with { Cells = changed.Cells.SetItem(lineField, Override(sourceCell, field, value, correction.Confirmation)) };
                changes.Add(new(field, before, value, correction.Provenance));
                updated = updated with { Lines = updated.Lines.SetItem(index, changed) };
                break;
        }
        if (changes.Count == 0) throw new InvalidDataException("Correction made no change.");
        // Keep source parser/grouping errors: a draft correction must not silently bless a malformed workbook.
        var originalIssues = invoice.SourceIssues.IsDefault ? invoice.Issues : invoice.SourceIssues;
        var original = invoice.HeaderCells?.GetValueOrDefault(field);
        var cell = Override(original, field, value, correction.Confirmation);
        var cells = updated.Cells.Add(cell);
        var revision = checked(invoice.Revision + 1);
        var audit = new CorrectionAudit(revision, DateTimeOffset.UtcNow,
            correction.Confirmation ?? "Typed correction confirmed by user.", changes.ToArray());
        updated = updated with { Revision = revision, Fingerprint = "", Cells = cells,
            Corrections = invoice.Corrections.IsDefault ? [audit] : invoice.Corrections.Add(audit),
            HeaderCells = field.StartsWith("lines[", StringComparison.Ordinal) ? invoice.HeaderCells
                : (invoice.HeaderCells ?? ImmutableDictionary<string, SourceCell>.Empty).SetItem(field, cell) };
        var issues = originalIssues.Concat(InvoiceValidation.Validate(updated)).Select(issue => issue with { InvoiceId = updated.InvoiceId }).ToImmutableArray();
        updated = updated with { Issues = issues, Fingerprint = InvoiceFingerprint.Compute(updated) };
        return new(updated, changes, issues);

        SourceCell Override(SourceCell? source, string key, string text, string confirmation)
            => (source ?? new SourceCell(invoice.Source.Worksheet, invoice.Source.FirstRow, 0, "override:" + key,
                "", "text", "", null, null)) with { ParsedValue = text, Formula = null, ValueOrigin = "user_override", Confirmation = confirmation };
    }

    private static T EnumValue<T>(string value) where T : struct, Enum
        => Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new InvalidDataException("Invalid " + typeof(T).Name);
    private static string Format(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}

public static class InvoiceFingerprint
{
    public static string Compute(InvoiceDocument invoice)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(invoice with { Fingerprint = "", Issues = [], SourceIssues = [], Corrections = [] }, ContractJson.Compact);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

public sealed class InvoiceDraftStore(IStateStore state)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static string PathFor(string id) => id.Length == 24 && id.All(Uri.IsHexDigit)
        ? "drafts/" + id + ".json" : throw new InvalidDataException("Invalid invoice ID.");

    public async Task<InvoiceDocument> ReadAsync(InvoiceDocument source, CancellationToken token = default)
    {
        var saved = await state.ReadAsync<InvoiceDocument>("invoice-draft", PathFor(source.InvoiceId), token);
        if (saved is null || saved.Source.Sha256 != source.Source.Sha256 || saved.Source.ImportRevision != source.Source.ImportRevision) return source;
        if (saved.InvoiceId != source.InvoiceId || saved.Revision < source.Revision || saved.Fingerprint != InvoiceFingerprint.Compute(saved))
            throw new InvalidDataException("Saved draft revision/hash is invalid.");
        return saved;
    }

    public async Task<InvoiceCorrectionResult> CorrectAsync(InvoiceDocument source, InvoiceCorrection correction, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var current = await ReadAsync(source, token);
            var result = InvoiceCorrectionService.Apply(current, correction);
            await state.WriteAsync("invoice-draft", PathFor(source.InvoiceId), result.Invoice, token);
            return result;
        }
        finally { gate.Release(); }
    }
}
