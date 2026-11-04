using System.Globalization;
using System.Text;

namespace MisaManager.Core;

/// <summary>
/// Which MISA voucher an invoice becomes. The kind lives in <see cref="InvoiceDocument.InvoiceType"/> (sales keep
/// "Hóa đơn bán hàng"), so no persisted field is added and fingerprints of sales invoices do not change.
/// </summary>
public static class InvoiceKinds
{
    public const string Sales = "Hóa đơn bán hàng";
    public const string Purchase = "Mua hàng trong nước nhập kho";
    /// <summary>Payment shown for purchases: entered as "Chưa thanh toán" (credit 331).</summary>
    public const string PurchasePayment = "Chưa thanh toán";

    public static bool IsPurchase(this InvoiceDocument invoice) => invoice.InvoiceType == Purchase;

    /// <summary>The party on the MISA voucher: the buyer (khách hàng) of a sale, the seller (nhà cung cấp) of a purchase.</summary>
    public static Party Partner(this InvoiceDocument invoice) => invoice.IsPurchase() ? invoice.Seller : invoice.Buyer;

    public static string PartnerLabel(this InvoiceDocument invoice) => invoice.IsPurchase() ? "nhà cung cấp" : "khách hàng";
}

public enum InvoiceNature { Goods, Service, Mixed }

public sealed record ServiceLine(string Name, string Unit, decimal? Amount, bool IsService, string Reason);

/// <summary>A purchase invoice left out of entry because it buys services/expenses, not stock goods.</summary>
public sealed record ServiceInvoice(string InvoiceId, string Number, DateOnly? Issued, string Seller, string SellerTaxCode,
    decimal? Subtotal, decimal? GrandTotal, InvoiceNature Nature, IReadOnlyList<ServiceLine> Lines)
{
    public string Reason => Nature == InvoiceNature.Mixed
        ? "Có cả hàng hóa và dịch vụ – không nhập bằng tool, nhập tay nếu cần."
        : "Dịch vụ/chi phí: " + string.Join("; ", Lines.Where(line => line.IsService).Select(line => line.Reason).Distinct().Take(3));
}

/// <summary>
/// Tells stock goods (phân bón, thuốc BVTV, vật tư…) from services and expenses (phí, cước, vé, xăng dầu, điện nước, ăn
/// uống, điện thoại/máy tính, phần mềm, thanh toán hợp đồng…). Only goods go into "Mua hàng trong nước nhập kho".
/// </summary>
public static class ItemNatures
{
    // Phrases are compared word by word on lower-case text with accents kept, so "phí" (fee) is not "phì" (fertile).
    private static readonly (string Phrase, string Reason)[] ServicePhrases =
    [
        ("dịch vụ", "dịch vụ"), ("phí", "phí"), ("lệ phí", "lệ phí"), ("cước", "cước phí"), ("chi phí", "chi phí"),
        ("vé", "vé"), ("vé máy bay", "vé máy bay"), ("thuê", "thuê"), ("vận chuyển", "vận chuyển"), ("vận tải", "vận tải"),
        ("giao hàng", "giao hàng"), ("bốc xếp", "bốc xếp"), ("sửa chữa", "sửa chữa"), ("bảo dưỡng", "bảo dưỡng"),
        ("bảo trì", "bảo trì"), ("lắp đặt", "lắp đặt"), ("bảo hiểm", "bảo hiểm"), ("tư vấn", "tư vấn"), ("đào tạo", "đào tạo"),
        ("quảng cáo", "quảng cáo"), ("thanh toán", "thanh toán theo hợp đồng"), ("hợp đồng", "thanh toán theo hợp đồng"),
        ("tiền điện", "tiền điện"), ("điện năng", "tiền điện"), ("điện sinh hoạt", "tiền điện"), ("tiền nước", "tiền nước"),
        ("nước sinh hoạt", "tiền nước"), ("xăng", "xăng dầu"), ("dầu diesel", "xăng dầu"), ("dầu do", "xăng dầu"),
        ("diesel", "xăng dầu"), ("nhiên liệu", "xăng dầu"), ("ron", "xăng dầu"), ("điện thoại", "điện thoại"),
        ("laptop", "máy tính"), ("máy tính", "máy tính"), ("máy in", "máy in"), ("phần mềm", "phần mềm"),
        ("chữ ký số", "chữ ký số"), ("gia hạn", "gia hạn dịch vụ"), ("bản quyền", "bản quyền"), ("tên miền", "tên miền"),
        ("hosting", "hosting"), ("internet", "internet"), ("viễn thông", "viễn thông"), ("ăn uống", "ăn uống"),
        ("suất ăn", "ăn uống"), ("buffet", "ăn uống"), ("nhà hàng", "ăn uống"), ("khách sạn", "lưu trú"),
        ("lưu trú", "lưu trú"), ("phòng nghỉ", "lưu trú"), ("phiếu mua hàng", "phiếu mua hàng"), ("voucher", "phiếu mua hàng"),
        ("thẻ cào", "thẻ cào"), ("nạp tiền", "nạp tiền")
    ];

    // Units of things that are used up at once (meals, trips, periods), never stocked.
    private static readonly HashSet<string> ServiceUnits = new(StringComparer.Ordinal)
    {
        "phần", "suất", "xuất", "đĩa", "dĩa", "ly", "tô", "bát", "chén", "nồi", "vé", "lần", "lượt", "tháng", "năm", "ngày",
        "đêm", "giờ", "chuyến", "km", "kwh", "người", "phòng", "set", "combo", "khóa", "dịch vụ", "gói dịch vụ", "bàn"
    };

    /// <summary>Accepted values of the optional "Phân loại" column that overrides the automatic decision.</summary>
    public const string GoodsLabel = "Hàng hóa";
    public const string ServiceLabel = "Dịch vụ";

    /// <summary>Line decision with the reason shown to the user.</summary>
    public static (bool IsService, string Reason) Classify(string? name, string? unit, decimal? quantity, string? overrideLabel = null)
    {
        var automatic = Detect(name, unit, quantity);
        var forced = Plain(overrideLabel);
        if (forced == Plain(GoodsLabel) && automatic.IsService) return (false, "đánh dấu Hàng hóa trong file");
        if (forced == Plain(ServiceLabel) && !automatic.IsService) return (true, "đánh dấu Dịch vụ trong file");
        return automatic;
    }

    private static (bool IsService, string Reason) Detect(string? name, string? unit, decimal? quantity)
    {
        var text = " " + Words(name) + " ";
        foreach (var (phrase, reason) in ServicePhrases)
            if (text.Contains(" " + Words(phrase) + " ", StringComparison.Ordinal)) return (true, reason);
        var unitWords = Words(unit);
        if (ServiceUnits.Contains(unitWords)) return (true, $"đơn vị tính '{unitWords}' (ăn uống/dịch vụ)");
        if (unitWords.Length == 0 && quantity is null) return (true, "không có đơn vị tính và số lượng (khoản chi phí)");
        return (false, "");
    }

    /// <summary>
    /// Whole-invoice decision. A service share of at least half the amount makes the invoice a service purchase (a
    /// restaurant bill with a bottle of water); a smaller share mixed with goods cannot be entered as one stock voucher.
    /// </summary>
    public static (InvoiceNature Nature, IReadOnlyList<ServiceLine> Lines) Classify(InvoiceDocument invoice)
    {
        var lines = invoice.Lines.Where(line => !IsDiscount(line))
            .Select(line =>
            {
                var forced = line.Cells?.GetValueOrDefault("itemNature")?.ParsedValue;
                var (service, reason) = Classify(line.Name, line.Unit, line.Quantity, forced);
                return new ServiceLine(line.Name, line.Unit, line.AmountBeforeTax, service, reason);
            }).ToArray();
        var services = lines.Count(line => line.IsService);
        if (services == 0) return (InvoiceNature.Goods, lines);
        if (services == lines.Length) return (InvoiceNature.Service, lines);
        var total = lines.Sum(line => Math.Abs(line.Amount ?? 0));
        var serviceAmount = lines.Where(line => line.IsService).Sum(line => Math.Abs(line.Amount ?? 0));
        var mostlyService = total > 0 ? serviceAmount * 2 >= total : services * 2 >= lines.Length;
        return (mostlyService ? InvoiceNature.Service : InvoiceNature.Mixed, lines);
    }

    /// <summary>"Chiết khấu thương mại" or "Điều chỉnh giảm" lines reduce the invoice; they are not items.</summary>
    public static bool IsDiscount(InvoiceLine line)
        => Plain(line.Kind).Contains("chietkhau", StringComparison.Ordinal) || IsReduction(line.Name);

    public static bool IsReduction(string? name) => Plain(name).StartsWith("dieuchinhgiam", StringComparison.Ordinal);

    private static string Words(string? value)
    {
        var builder = new StringBuilder();
        foreach (var c in (value ?? "").Normalize(NormalizationForm.FormC).ToLower(CultureInfo.GetCultureInfo("vi-VN")))
            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Lower-case letters and digits without accents ("Chiết khấu" → "chietkhau").</summary>
    public static string Plain(string? value)
        => new((value ?? "").Normalize(NormalizationForm.FormD).ToLowerInvariant().Replace('đ', 'd')
            .Where(char.IsLetterOrDigit).ToArray());
}
