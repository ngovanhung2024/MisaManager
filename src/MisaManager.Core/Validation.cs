using System.Globalization;

namespace MisaManager.Core;

public static class InvoiceValidation
{
    public static IReadOnlyList<ImportIssue> Validate(InvoiceDocument invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var issues = new List<ImportIssue>();
        var sourceWorksheet = invoice.Source?.Worksheet ?? "";
        var invoiceId = invoice.InvoiceId ?? "";
        void Block(string code, string message, string resolution, InvoiceLine? line = null, string? field = null)
        {
            var cell = field is not null && line?.Cells is not null ? line.Cells.GetValueOrDefault(field) : null;
            issues.Add(new(code, IssueSeverity.Blocking, message, cell?.Worksheet ?? sourceWorksheet,
                cell?.Address, invoiceId, line?.LineId, Resolution: resolution));
        }
        void Required(string value, string field, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) Block(field + "_MISSING", "Thiếu " + label + ".", "Bổ sung " + label + " trong workbook rồi nhập lại.");
        }
        if (invoice.Identity is null)
            Block("IDENTITY_MISSING", "Thiếu khối nhận diện hóa đơn.", "Nhập lại invoice typed đầy đủ từ workbook.");
        else
            Required(invoice.Identity.Number, "NUMBER", "số hóa đơn");
        if (invoice.Source is null)
            Block("SOURCE_MISSING", "Thiếu tham chiếu workbook nguồn.", "Nhập lại invoice typed có Source.FilePath và Source.Sha256.");
        if (invoice.Seller is null)
            Block("SELLER_MISSING", "Thiếu thông tin người bán.", "Nhập lại invoice typed có seller.");
        if (invoice.Buyer is null)
            Block("BUYER_MISSING", "Thiếu thông tin người mua.", "Nhập lại invoice typed có buyer.");
        if (invoice.Issues.IsDefault || invoice.Issues.Any(issue => issue is null))
            Block("ISSUES_MISSING", "Invoice issues must be an explicit array without null entries.", "Reimport the complete typed invoice.");
        if (invoice.Lines.IsDefaultOrEmpty)
            Block("LINES_MISSING", "Invoice must contain line items.", "Reimport the complete typed invoice.");
        else
            foreach (var line in invoice.Lines)
            {
                if (line is null)
                    Block("LINE_MISSING", "Invoice contains a null line item.", "Reimport the complete typed invoice.");
                else if (line.Cells is null || line.Cells.Values.Any(cell => cell is null))
                    Block("LINE_CELLS_MISSING", "Line source cells are missing or contain null entries.", "Reimport the complete typed invoice.", line);
            }
        // JSON constructor parameters can be omitted despite non-nullable C# types.
        // Stop before domain calculations or computed properties access that graph.
        if (invoice.Buyer is null || issues.Any(issue => issue.Severity == IssueSeverity.Blocking))
            return issues;
        Required(invoice.Buyer.Name, "BUYER", "tên người mua");
        if (string.IsNullOrWhiteSpace(invoice.Buyer.TaxCode))
        {
            if (invoice.Buyer.Name?.Contains("NGƯỜI TIÊU DÙNG", StringComparison.OrdinalIgnoreCase) == true)
                issues.Add(new("BUYER_TAX_OPTIONAL", IssueSeverity.Info, "MST người mua để trống; chấp nhận cho giao dịch bán cho người tiêu dùng."));
            else if (BuyerCodes.IsAnonymous(invoice.Buyer.Name))
                issues.Add(new("BUYER_TAX_OPTIONAL", IssueSeverity.Info,
                    $"MST người mua để trống; người mua không cung cấp thông tin nên nhập vào khách hàng '{BuyerCodes.RetailCustomerName}'."));
            else if (invoice.Buyer.Kind != PartyKind.Organization && !BuyerCodes.LooksLikeOrganization(invoice.Buyer.Name))
                issues.Add(new("BUYER_TAX_OPTIONAL", IssueSeverity.Info, "MST người mua để trống; người mua là cá nhân nên không bắt buộc MST."));
            else Block("BUYER_TAX_STATUS", "Thiếu MST của người mua là tổ chức/doanh nghiệp.", "Bổ sung MST người mua trong workbook.");
        }
        if (invoice.Buyer.TaxCodePresence == ValuePresence.Provided && string.IsNullOrWhiteSpace(invoice.Buyer.TaxCode))
            Block("BUYER_TAX_STATUS", "Trạng thái MST người mua không phù hợp.", "Điền MST hoặc để trống cho người tiêu dùng.");
        if (invoice.Issued is null) Block("DATES_MISSING", "Thiếu ngày lập hóa đơn.", "Bổ sung ngày lập rõ ràng.");
        if (invoice.Lines.Length == 0) Block("LINES_MISSING", "Hóa đơn không có dòng hàng.", "Bổ sung dòng hàng.");
        decimal lineSum = 0, taxSum = 0;
        try
        {
            foreach (var line in invoice.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Name) || string.IsNullOrWhiteSpace(line.Unit))
                    Block("ITEM_IDENTITY_MISSING", "Dòng hàng thiếu tên hoặc đơn vị.", "Bổ sung đủ thông tin hàng hóa.", line, "name");
                if (line.Quantity is null or <= 0 || line.UnitPrice is null or < 0 || line.AmountBeforeTax is null or < 0 || line.TaxAmount is null or < 0)
                    Block("LINE_NUMBERS_INVALID", "Thiếu hoặc sai số lượng, đơn giá, thành tiền hoặc tiền thuế.", "Điền số nguồn hợp lệ; dòng điều chỉnh/âm chưa được hỗ trợ.", line, "quantity");
                if (line.Cells is null)
                    Block("LINE_CELLS_MISSING", "Dòng hàng thiếu dữ liệu nguồn.", "Nhập lại invoice typed đầy đủ từ workbook.", line);
                else if (line.HasFormula)
                    Block("FORMULA_UNCONFIRMED", "Dòng chứa công thức chưa xác nhận.", "Xuất giá trị đã tính thành ô tĩnh.", line);
                if (line.Quantity is decimal quantity && line.UnitPrice is decimal price && line.AmountBeforeTax is decimal amount)
                {
                    var amountDifference = Math.Abs(checked(quantity * price - (line.Discount ?? 0)) - amount);
                    // Source amounts are rounded to whole đồng; only a larger gap is worth a warning.
                    if (amountDifference > 0.5m)
                        issues.Add(new("UNIT_PRICE_AMOUNT_DIFFERENCE", IssueSeverity.Warning, "Số lượng × đơn giá khác thành tiền nguồn; giữ nguyên giá trị nguồn để đối chiếu.", sourceWorksheet, line.Cells?.GetValueOrDefault("amount")?.Address, invoiceId, line.LineId));
                    var rateText = (line.TaxRate ?? "").TrimEnd('%').Trim();
                    if (!DecimalValue.TryParseInvariant(rateText, out var rate) || rate is < 0 or > 100)
                        Block("TAX_RATE_UNSUPPORTED", "Thuế suất thiếu hoặc chưa được hỗ trợ.", "Cần thuế suất phần trăm rõ ràng; thuế đặc biệt chưa được hỗ trợ.", line, "taxRate");
                    else if (line.TaxAmount is decimal tax && Math.Abs(checked(amount * rate / 100m) - tax) > 0.5m)
                        issues.Add(new("TAX_RATE_AMOUNT_DIFFERENCE", IssueSeverity.Warning, "Tiền thuế chi tiết không khớp phép tính thuế suất; giữ nguyên dữ liệu nguồn.", sourceWorksheet, line.Cells?.GetValueOrDefault("taxAmount")?.Address, invoiceId, line.LineId));
                    if (line.TotalWithTax is decimal total && line.TaxAmount is decimal lineTax && checked(amount + lineTax) != total)
                        Block("LINE_TOTAL_MISMATCH", "Tổng tiền có thuế khác thành tiền cộng tiền thuế.", "Kiểm tra tổng tiền có thuế của dòng.", line, "lineTotal");
                }
                lineSum = checked(lineSum + (line.AmountBeforeTax ?? 0));
                taxSum = checked(taxSum + (line.TaxAmount ?? 0));
            }
            if (invoice.Subtotal is null || invoice.TaxTotal is null || invoice.GrandTotal is null)
                Block("TOTALS_MISSING", "Thiếu tổng trước thuế, tiền thuế hoặc tổng thanh toán hóa đơn.", "Bổ sung đủ các tổng nguồn; không dùng tổng tính toán thay giá trị nguồn.");
            else if (lineSum != invoice.Subtotal || taxSum != invoice.TaxTotal || checked(invoice.Subtotal + invoice.TaxTotal) != invoice.GrandTotal)
                Block("TOTAL_MISMATCH", "Tổng các dòng không khớp tổng hóa đơn.", "Kiểm tra thành tiền, tiền thuế và tổng có thuế của từng dòng.");
        }
        catch (OverflowException)
        {
            Block("ARITHMETIC_OVERFLOW", "Giá trị vượt precision kế toán được hỗ trợ.", "Giảm phạm vi hoặc sửa giá trị; không tự làm tròn để nhận số.");
        }
        return issues;
    }
}
