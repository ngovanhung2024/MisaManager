using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MisaManager.Core;

namespace MisaManager.Import;

public sealed record WorkbookImportOptions(string? Worksheet = null, string? Locale = null,
    int? HeaderRow = null, ImmutableDictionary<string, int>? ColumnMap = null, int ImportRevision = 1,
    ImportConfirmation? Confirmation = null);

public sealed record WorkbookColumnChoice(int Column, string Display);

public sealed record WorkbookMappingField(string Key, bool Required, int? SuggestedColumn);

public sealed record WorkbookMappingSchema(int HeaderRow, IReadOnlyList<WorkbookColumnChoice> Columns,
    IReadOnlyList<WorkbookMappingField> Fields);

/// <param name="purchase">
/// Purchase invoices ("Mua hàng"): the seller is the supplier on the voucher, invoices are told apart by supplier MST,
/// series and number (suppliers reuse numbers), and services/expenses are left out (<see cref="ImportResult.Services"/>).
/// </param>
public sealed class InvoiceWorkbookImporter(WorkbookReader reader, bool purchase = false)
{
    private static readonly IReadOnlyDictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["number"] = ["số hóa đơn", "so hoa don", "số hđ", "so hd", "invoice no", "invoice number"],
        ["series"] = ["ký hiệu", "ky hieu", "ký hiệu hóa đơn", "ky hieu hoa don", "series"],
        ["template"] = ["mẫu số", "mau so", "template"],
        ["invoiceType"] = ["loại hóa đơn", "loai hoa don", "invoice type"],
        ["issued"] = ["ngày hóa đơn", "ngay hoa don", "ngày lập", "ngay lap", "invoice date", "date"],
        ["signed"] = ["ngày ký", "ngay ky", "ngày ký hóa đơn", "ngay ky hoa don", "signed date", "signature date"],
        ["seller"] = ["người bán", "nguoi ban", "tên người bán", "ten nguoi ban", "seller"],
        ["sellerTaxCode"] = ["mst người bán", "mst nguoi ban", "mã số thuế người bán", "ma so thue nguoi ban", "seller tax code"],
        ["sellerAddress"] = ["địa chỉ người bán", "dia chi nguoi ban", "seller address"],
        ["sellerCode"] = ["mã người bán", "ma nguoi ban", "seller code"],
        ["buyer"] = ["người mua", "nguoi mua", "tên người mua", "ten nguoi mua", "customer", "buyer"],
        ["buyerCompany"] = ["dn mua", "doanh nghiệp mua", "tên doanh nghiệp mua", "đơn vị mua", "tên đơn vị mua", "buyer company", "buyer organization"],
        ["buyerTaxCode"] = ["mst người mua", "mst nguoi mua", "mã số thuế người mua", "ma so thue nguoi mua", "buyer tax code"],
        ["buyerAddress"] = ["địa chỉ người mua", "dia chi nguoi mua", "buyer address"],
        ["buyerCode"] = ["mã khách hàng", "ma khach hang", "customer code", "buyer code"],
        ["buyerTaxPresence"] = ["trạng thái mst người mua", "buyer tax presence"],
        ["buyerKind"] = ["loại người mua", "buyer kind"],
        ["buyerContact"] = ["người liên hệ", "buyer contact"],
        ["itemCode"] = ["mã hàng", "ma hang", "mã hàng hóa", "ma hang hoa", "mã vthh", "ma vthh", "mã vật tư", "ma vat tu", "item code"],
        ["name"] = ["tên hàng hóa", "ten hang hoa", "tên hàng", "ten hang", "hàng hóa", "hang hoa", "item name", "description"],
        ["unit"] = ["đơn vị tính", "don vi tinh", "dvt", "unit", "uom"],
        ["quantity"] = ["số lượng", "so luong", "quantity", "qty"],
        ["unitPrice"] = ["đơn giá", "don gia", "unit price", "price"],
        ["amount"] = ["thành tiền", "thanh tien", "tổng tiền chưa thuế", "tong tien chua thue", "amount before tax", "line amount", "subtotal line"],
        ["discount"] = ["chiết khấu", "chiet khau", "discount", "discount amount"],
        ["discountRate"] = ["tỷ lệ chiết khấu", "ty le chiet khau", "discount rate"],
        ["lineKind"] = ["tính chất dòng", "line kind"],
        ["taxRate"] = ["thuế suất", "thue suat", "thuế hàng hóa", "thue hang hoa", "tax rate", "vat rate"],
        ["taxAmount"] = ["tiền thuế chi tiết", "tien thue chi tiet", "tiền thuế", "tien thue", "tax amount", "vat amount"],
        ["subtotal"] = ["tổng trước thuế hóa đơn", "tong truoc thue hoa don", "invoice subtotal"],
        ["taxTotal"] = ["tổng tiền thuế", "tong tien thue", "invoice tax", "tax total"],
        ["total"] = ["tổng thanh toán hóa đơn", "tong thanh toan hoa don", "invoice grand total", "grand total"],
        ["lineTotal"] = ["tổng dòng có thuế", "tong dong co thue", "tổng tiền có thuế", "tong tien co thue", "line total with tax"],
        ["invoiceDiscount"] = ["chiết khấu hóa đơn", "chiet khau hoa don", "invoice discount"],
        ["currency"] = ["tiền tệ", "tien te", "currency"],
        ["payment"] = ["hình thức thanh toán", "hinh thuc thanh toan", "payment method", "payment"],
        ["exchangeRate"] = ["tỷ giá", "ty gia", "exchange rate"],
        ["voucherDate"] = ["ngày chứng từ", "ngay chung tu", "voucher date"],
        ["postedDate"] = ["ngày hạch toán", "ngay hach toan", "posted date"],
        ["notes"] = ["ghi chú", "ghi chu", "notes", "note"],
        ["itemNature"] = ["phân loại", "phan loai", "loại hàng", "hàng hóa/dịch vụ", "item nature"]
    };
    // "template"/"series" (Mẫu số, Ký hiệu) are typed into MISA, which clears its proposed series once the template is
    // chosen; optional, and not part of the sales InvoiceId.
    private static readonly string[] SelectedFields = ["number", "template", "series", "issued", "signed", "buyer", "buyerCompany", "buyerCode", "buyerTaxCode", "itemCode", "name", "unit", "unitPrice", "quantity", "amount", "taxRate", "taxAmount", "lineTotal"];
    private static readonly string[] Required = ["number", "issued", "buyer", "name", "unit", "unitPrice", "quantity", "amount", "taxRate", "taxAmount", "lineTotal"];
    private static readonly string[] IdentityFields = ["number", "buyerCode", "buyerTaxCode"];
    private static readonly string[] PurchaseFields = ["number", "series", "template", "issued", "seller", "sellerTaxCode", "sellerAddress", "sellerCode",
        "buyer", "buyerTaxCode", "itemCode", "name", "unit", "unitPrice", "quantity", "amount", "taxRate", "taxAmount", "lineTotal",
        "lineKind", "itemNature", "total"];
    private static readonly string[] PurchaseRequired = ["number", "issued", "seller", "sellerTaxCode", "name", "unit", "unitPrice", "quantity",
        "amount", "taxRate", "taxAmount", "lineTotal"];
    // Per-line columns; every other mapped column is invoice metadata and must agree across the invoice's rows.
    private static readonly HashSet<string> LineFields = new(["itemCode", "name", "unit", "quantity", "unitPrice", "amount", "taxRate",
        "taxAmount", "lineTotal", "lineKind", "itemNature"], StringComparer.Ordinal);
    /// <summary>Sales fields (the default importer).</summary>
    public static IReadOnlyList<string> Fields => SelectedFields;
    public IReadOnlyList<string> MappedFields => purchase ? PurchaseFields : SelectedFields;
    private IReadOnlyList<string> RequiredFields => purchase ? PurchaseRequired : Required;

    public WorkbookMappingSchema DescribeMapping(WorkbookData workbook, string worksheet)
    {
        var sheet = workbook.Sheets.SingleOrDefault(item => item.Name == worksheet)
            ?? throw new WorkbookImportException("WORKSHEET_NOT_FOUND", $"Không tìm thấy worksheet '{worksheet}'.");
        var header = FindHeader(sheet) ?? throw new WorkbookImportException("HEADER_MISSING", "Không tìm thấy dòng tiêu đề.");
        var columns = header.Cells.Values.OrderBy(cell => cell.Column)
            .Select(cell => new WorkbookColumnChoice(cell.Column, $"{cell.Address} - {cell.ParsedValue}"))
            .ToArray();
        var fields = MappedFields.Select(key => new WorkbookMappingField(key, RequiredFields.Contains(key), SuggestColumn(header, key))).ToArray();
        return new WorkbookMappingSchema(header.Number, columns, fields);
    }

    public async Task<ImportResult> ImportAsync(string path, WorkbookImportOptions? options = null, CancellationToken token = default)
    {
        options ??= new();
        if (options.Locale is not (null or "vi-VN" or "en-US" or "invariant"))
            throw new WorkbookImportException("LOCALE_UNSUPPORTED", "Chọn vi-VN, en-US hoặc invariant cho số dạng văn bản.");
        var workbook = await reader.ReadAsync(path, token);
        return await Task.Run(() => Import(workbook, options, token), token);
    }

    public ImportResult Import(WorkbookData workbook, WorkbookImportOptions options, CancellationToken token = default)
    {
        if (options.ImportRevision < 1) throw new WorkbookImportException("REVISION_INVALID", "Import revision phải lớn hơn 0.");
        var confirmation = options.Confirmation;
        if (confirmation is not null && (confirmation.WorkbookSha256 != workbook.Sha256
            || string.IsNullOrWhiteSpace(confirmation.Confirmation)
            || confirmation.UseLegacySalesDefaults && string.IsNullOrWhiteSpace(confirmation.CompanyIdentity)))
            throw new WorkbookImportException("CONFIRMATION_STALE", "Xác nhận nhập không khớp nội dung workbook hoặc thiếu công ty.");
        if (options.Locale is not (null or "vi-VN" or "en-US" or "invariant"))
            throw new WorkbookImportException("LOCALE_UNSUPPORTED", "Chọn rõ locale số/ngày dạng văn bản.");
        var issues = new List<ImportIssue>();
        var sheets = workbook.Sheets;
        WorkbookSheet sheet;
        if (!string.IsNullOrWhiteSpace(options.Worksheet))
            sheet = sheets.SingleOrDefault(item => item.Name == options.Worksheet)
                ?? throw new WorkbookImportException("WORKSHEET_NOT_FOUND", $"Không tìm thấy worksheet '{options.Worksheet}'.");
        else
        {
            var candidates = sheets.Where(item => item.Rows.Take(50).Any(row => row.Cells.Values.Any(cell =>
                Aliases["number"].Any(alias => Normalize(alias) == Normalize(cell.ParsedValue))) && row.Cells.Values.Any(cell =>
                Aliases["name"].Any(alias => Normalize(alias) == Normalize(cell.ParsedValue))))).ToArray();
            if (candidates.Length != 1) throw new WorkbookImportException("WORKSHEET_SELECTION_REQUIRED", "Chọn worksheet dữ liệu: không có đúng một sheet phù hợp.");
            sheet = candidates[0];
        }
        var header = options.HeaderRow is int headerNumber ? sheet.Rows.SingleOrDefault(row => row.Number == headerNumber) : FindHeader(sheet);
        header = header
            ?? throw new WorkbookImportException("HEADER_MISSING", "Không tìm thấy dòng tiêu đề.");
        var columns = options.ColumnMap is null ? MapColumns(header, issues) : ValidateMapping(options.ColumnMap, header);
        // A "DN mua" column alone names the buyer as well as "Người mua" does.
        foreach (var required in RequiredFields.Where(key => !columns.ContainsKey(key) && !(key == "buyer" && columns.ContainsKey("buyerCompany"))))
            issues.Add(new("COLUMN_MISSING", IssueSeverity.Blocking, $"Thiếu cột bắt buộc '{required}'.", sheet.Name, Resolution: "Bổ sung cột hoặc chọn lại worksheet."));
        if (issues.Any(issue => issue.Severity == IssueSeverity.Blocking))
            return EmptyResult(workbook, sheet, header.Number, issues);

        if (sheet.HasFilter || sheet.Rows.Any(row => row.Hidden) || !sheet.Visibility.Equals("visible", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("HIDDEN_ROWS_INCLUDED", IssueSeverity.Warning, "Đã bao gồm các dòng ẩn/lọc của worksheet được chọn.", sheet.Name,
                Resolution: "Kiểm tra phạm vi và từng hóa đơn trước khi chọn nhập."));
        var groups = new Dictionary<string, List<(WorkbookRow Row, IReadOnlyDictionary<string, SourceCell> Cells)>>(StringComparer.Ordinal);
        var ignored = 0;
        var unnumbered = new List<SourceCell>();
        foreach (var row in sheet.Rows.Where(row => row.Number > header.Number))
        {
            token.ThrowIfCancellationRequested();
            var cells = columns.ToDictionary(pair => pair.Key, pair => row.Cells.GetValueOrDefault(pair.Value) ?? Blank(sheet.Name, row.Number, pair.Value), StringComparer.Ordinal);
            if (row.Cells.Values.All(cell => string.IsNullOrWhiteSpace(cell.ParsedValue) && !cell.HasFormula)) { ignored++; continue; }
            if (confirmation?.UseLegacySalesDefaults == true) ApplyLegacyDefaults(cells, sheet.Name, row.Number, confirmation);
            var number = Text(cells, "number");
            // Unnumbered rows are invoices not yet signed/issued: skipped, never glued to the previous invoice.
            if (number.Length == 0) { unnumbered.Add(cells["number"]); continue; }
            ParseDate(cells["issued"], workbook.Date1904, options.Locale, issues);
            // One invoice number denotes one sales invoice; suppliers reuse numbers, so purchases add MST and series.
            // Conflicting header values are checked in BuildInvoice.
            var groupKey = purchase ? JsonSerializer.Serialize(new[] { PartyCatalogStore.TaxKey(Text(cells, "sellerTaxCode")),
                PostedInvoiceStore.SeriesKey(Text(cells, "series")), number }) : number;
            if (!groups.TryGetValue(groupKey, out var group)) groups.Add(groupKey, group = []);
            group.Add((row, cells));
        }
        if (unnumbered.Count > 0)
            issues.Add(new("INVOICE_NUMBER_MISSING_SKIPPED", IssueSeverity.Info,
                $"Bỏ qua {unnumbered.Count:N0} dòng chưa có số hóa đơn (dòng {string.Join(", ", unnumbered.Take(10).Select(cell => cell.Row))}{(unnumbered.Count > 10 ? ", …" : "")}).",
                sheet.Name, unnumbered[0].Address,
                Resolution: "Hóa đơn chưa được cấp số (chưa ký/phát hành) không nhập; xuất lại file sau khi đã có số."));
        var sha = workbook.Sha256;
        var source = new SourceReference(workbook.FilePath, sha, sheet.Name, header.Number, sheet.Rows.LastOrDefault()?.Number ?? header.Number, options.ImportRevision);
        var built = groups.Values.Select(group => BuildInvoice(source, group, workbook.Date1904, options.Locale, confirmation, purchase, token)).ToArray();
        var services = new List<ServiceInvoice>();
        if (purchase)
        {
            // Only stock goods are entered; a service or mixed invoice is listed for the user instead.
            foreach (var invoice in built)
            {
                var (nature, classified) = ItemNatures.Classify(invoice);
                if (nature != InvoiceNature.Goods)
                    services.Add(new(invoice.InvoiceId, invoice.Identity.Number, invoice.Issued, invoice.Seller.Name, invoice.Seller.TaxCode,
                        invoice.Subtotal, invoice.GrandTotal, nature, classified));
            }
            var serviceIds = services.Select(item => item.InvoiceId).ToHashSet(StringComparer.Ordinal);
            built = built.Where(invoice => !serviceIds.Contains(invoice.InvoiceId)).ToArray();
            if (services.Count > 0)
                issues.Add(new("SERVICE_INVOICES_SKIPPED", IssueSeverity.Info,
                    $"Bỏ qua {services.Count:N0} hóa đơn dịch vụ/chi phí (xem tab Dịch vụ).", sheet.Name,
                    Resolution: "Chỉ hàng hóa được nhập vào Mua hàng nhập kho. Nếu tool nhận nhầm, sửa cột 'Phân loại' thành 'Hàng hóa' rồi nạp lại."));
        }
        // Negative lines are adjustments/returns, which are not entered: the whole invoice is left out.
        var negative = built.Where(invoice => invoice.Lines.Any(line => line.Quantity < 0 || line.AmountBeforeTax < 0)).ToArray();
        var invoices = built.Except(negative).ToImmutableArray();
        if (negative.Length > 0)
            issues.Add(new("INVOICE_NEGATIVE_SKIPPED", IssueSeverity.Info,
                $"Bỏ qua {negative.Length:N0} hóa đơn có số lượng/thành tiền âm (HĐ {string.Join(", ", negative.Take(10).Select(invoice => invoice.Identity.Number))}{(negative.Length > 10 ? ", …" : "")}).",
                sheet.Name, Resolution: "Hóa đơn điều chỉnh/trả hàng không nhập bằng tool."));
        if (invoices.Length == 0 && services.Count == 0) issues.Add(new("NO_INVOICES", IssueSeverity.Blocking, "Worksheet không có dòng hóa đơn.", sheet.Name));
        return new ImportResult(source, sheets.Select(item => item.Name).ToArray(), invoices, issues.Concat(invoices.SelectMany(item => item.Issues)).ToArray(),
            sheet.Rows.Length, ignored, unnumbered.Count, negative.Length, purchase ? services : null);
    }

    private static InvoiceDocument BuildInvoice(SourceReference source, List<(WorkbookRow Row, IReadOnlyDictionary<string, SourceCell> Cells)> records, bool date1904, string? locale, ImportConfirmation? confirmation, bool purchase, CancellationToken token)
    {
        var invoiceSource = new SourceReference(source.FilePath, source.Sha256, source.Worksheet,
            records.Min(item => item.Row.Number), records.Max(item => item.Row.Number), source.ImportRevision);
        var issues = new List<ImportIssue>();
        var first = records[0].Cells.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var field in new[] { "issued", "signed", "buyer", "buyerCompany", "buyerTaxCode", "seller", "sellerTaxCode" })
        {
            var populated = records.Select(record => record.Cells.GetValueOrDefault(field))
                .Where(cell => cell is not null && !string.IsNullOrWhiteSpace(cell.ParsedValue)).ToArray();
            if (populated.Length > 0) first[field] = populated[0]!;
        }
        var number = Text(records[0].Cells, "number");
        var key = JsonSerializer.Serialize(purchase
            ? new[] { source.FilePath, source.Sha256, source.Worksheet, number, Text(first, "series"), PartyCatalogStore.TaxKey(Text(first, "sellerTaxCode")) }
            : new[] { source.FilePath, source.Sha256, source.Worksheet, number });
        var id = Hash(key)[..24];
        var issued = ParseDate(first["issued"], date1904, locale, issues);
        var identity = new InvoiceIdentity(Text(first, "sellerTaxCode"), Text(first, "series"), Text(first, "number"), issued);
        var lines = ImmutableArray.CreateBuilder<InvoiceLine>();
        var allCells = ImmutableArray.CreateBuilder<SourceCell>();
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            var cells = record.Cells;
            allCells.AddRange(cells.Values);
            foreach (var cell in cells.Values)
            {
                if (cell.HasFormula) issues.Add(new("FORMULA_UNCONFIRMED", IssueSeverity.Blocking, "Ô chứa công thức; cached value chưa chứng minh được tính lại.", cell.Worksheet, cell.Address,
                    Resolution: "Xuất giá trị tĩnh hoặc bổ sung giá trị đã xác nhận."));
                if (cell.CellType is "error" or "boolean") issues.Add(new("CELL_TYPE_INVALID", IssueSeverity.Blocking, "Ô lỗi/boolean không dùng được cho trường hóa đơn.", cell.Worksheet, cell.Address));
            }
            foreach (var field in IdentityFields)
                if (cells.TryGetValue(field, out var cell) && !string.IsNullOrWhiteSpace(cell.ParsedValue) && cell.CellType != "text"
                    && !(field is "number" or "buyerTaxCode" && cell.CellType == "number"
                        && Regex.IsMatch(cell.ParsedValue, @"^[0-9]{1,15}$", RegexOptions.CultureInvariant)))
                    issues.Add(new("IDENTITY_REQUIRES_TEXT", IssueSeverity.Blocking, $"{field}: mã/số phải là ô text để bảo toàn số 0 đầu và precision.", cell.Worksheet, cell.Address,
                        Resolution: "Xác nhận mã nguồn, định dạng Text rồi nhập lại giá trị chính xác."));
            // Optional "Mã hàng": settles names that match several catalog codes (ITEM_AMBIGUOUS/ITEM_UNIT_CONFLICT).
            lines.Add(new InvoiceLine($"{id}:line:{record.Row.Number}", Text(cells, "itemCode"), Text(cells, "name"), Text(cells, "unit"),
                Number(cells, "quantity", locale, issues), Number(cells, "unitPrice", locale, issues), Number(cells, "amount", locale, issues), Number(cells, "discount", locale, issues),
                Text(cells, "taxRate"), Number(cells, "taxAmount", locale, issues), Number(cells, "lineTotal", locale, issues), Number(cells, "discountRate", locale, issues),
                Text(cells, "lineKind"), cells.ToImmutableDictionary()));
        }
        var lineFields = LineFields;
        foreach (var field in first.Keys.Where(field => !lineFields.Contains(field)))
            if (records.Select(row => Text(row.Cells, field)).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Count() > 1)
                issues.Add(new("METADATA_CONFLICT", IssueSeverity.Blocking, $"{field}: dữ liệu trong cùng nhóm hóa đơn không nhất quán.", source.Worksheet,
                    first[field].Address, Resolution: "Kiểm tra nhóm hóa đơn và điền metadata nhất quán trên từng dòng."));
        var buyerPresence = Presence(Text(first, "buyerTaxCode"));
        var declaredPresence = Text(first, "buyerTaxPresence");
        if (declaredPresence.Length > 0 && (!Enum.TryParse(declaredPresence, true, out buyerPresence) || !Enum.IsDefined(buyerPresence)))
            issues.Add(new("TAX_PRESENCE_INVALID", IssueSeverity.Blocking, "Trạng thái MST phải là Missing, Provided hoặc NotApplicable."));
        var buyerKind = PartyKind.Unspecified;
        if (Text(first, "buyerKind").Length > 0 && (!Enum.TryParse(Text(first, "buyerKind"), true, out buyerKind) || !Enum.IsDefined(buyerKind)))
            issues.Add(new("PARTY_KIND_INVALID", IssueSeverity.Blocking, "Loại người mua phải là Organization hoặc Individual."));
        var buyerName = BuyerName(first);
        var buyerContact = Text(first, "buyerContact");
        // With "DN mua" the organisation is the buyer; "Người mua" is then only the person who took the invoice.
        if (buyerContact.Length == 0 && Text(first, "buyerCompany").Length > 0
            && TextKey.Compact(Text(first, "buyer")) != TextKey.Compact(buyerName))
            buyerContact = Text(first, "buyer");
        var buyerCode = Text(first, "buyerCode");
        // Consumer invoices use MISA's standard NTD customer when the source
        // workbook does not carry a customer code.
        if (purchase) { }
        else if (Normalize(buyerName) == Normalize("BÁN CHO NGƯỜI TIÊU DÙNG") && buyerCode.Length == 0)
        {
            buyerCode = "NTD";
            buyerKind = PartyKind.Individual;
            if (buyerPresence == ValuePresence.Missing) buyerPresence = ValuePresence.NotApplicable;
        }
        else if (BuyerCodes.IsAnonymous(buyerName))
        {
            // Entered under MISA's "Khách lẻ" (KL) when the invoice runs; an Excel code still wins.
            buyerKind = PartyKind.Individual;
            if (buyerPresence == ValuePresence.Missing) buyerPresence = ValuePresence.NotApplicable;
        }
        DateOnly? DateField(string field) => first.TryGetValue(field, out var cell) ? ParseDate(cell, date1904, locale, issues) : null;
        decimal? Total(string field, Func<InvoiceLine, decimal?> select)
        {
            if (lines.Any(line => select(line) is null)) return null;
            try
            {
                var sum = lines.Sum(line => select(line)!.Value);
                allCells.Add(new(source.Worksheet, records[0].Row.Number, 0, "derived:" + field, "", "decimal", "", null,
                    sum.ToString(CultureInfo.InvariantCulture), ValueOrigin: "sum_lines"));
                return sum;
            }
            catch (OverflowException)
            {
                issues.Add(new("ARITHMETIC_OVERFLOW", IssueSeverity.Blocking, "Tổng các dòng vượt precision hỗ trợ.", InvoiceId: id));
                return null;
            }
        }
        var subtotal = Total("subtotal", line => line.AmountBeforeTax);
        var taxTotal = Total("taxTotal", line => line.TaxAmount);
        var grandTotal = Total("total", line => line.TotalWithTax);
        var signed = DateField("signed") ?? issued;
        var voucherDate = DateField("voucherDate") ?? signed;
        var postedDate = DateField("postedDate") ?? signed;
        // The portal's invoice total must equal the lines; otherwise a fee or rounding is missing from them.
        if (purchase && Number(first, "total", locale, issues) is { } stated && grandTotal is { } summed && stated != summed
            && !lines.Any(line => line.AmountBeforeTax < 0))
            issues.Add(new("SOURCE_TOTAL_MISMATCH", IssueSeverity.Blocking, $"Tổng thanh toán hóa đơn {stated:N0} khác tổng các dòng {summed:N0}.",
                source.Worksheet, first["total"].Address, Resolution: "Kiểm tra phí/chiết khấu trên hóa đơn gốc; nhập tay hóa đơn này."));
        var invoice = new InvoiceDocument(id, source.ImportRevision, "", identity, invoiceSource,
            new Party(Text(first, "seller"), Text(first, "sellerTaxCode"), Text(first, "sellerAddress"), Text(first, "sellerCode"), Presence(Text(first, "sellerTaxCode"))),
            new Party(buyerName, Text(first, "buyerTaxCode"), Text(first, "buyerAddress"), buyerCode, buyerPresence, buyerKind, buyerContact),
            Text(first, "template"), Text(first, "currency").Length == 0 ? "VND" : Text(first, "currency"),
            purchase ? InvoiceKinds.PurchasePayment : Text(first, "payment").Length == 0 ? "Tiền mặt" : Text(first, "payment"), issued, voucherDate, postedDate,
            purchase ? InvoiceKinds.Purchase : Text(first, "invoiceType").Length == 0 ? InvoiceKinds.Sales : Text(first, "invoiceType"),
            Number(first, "exchangeRate", locale, issues), lines.ToImmutable(),
            subtotal, taxTotal, grandTotal, Number(first, "invoiceDiscount", locale, issues),
            ImmutableArray<TaxSummary>.Empty, Text(first, "notes"), ImmutableArray<ImportIssue>.Empty, allCells.ToImmutable(), confirmation,
            Signed: signed);
        invoice = invoice with { SourceIssues = issues.ToImmutableArray(), Corrections = [],
            HeaderCells = first.Where(pair => !lineFields.Contains(pair.Key)).ToImmutableDictionary() };
        return invoice with { Fingerprint = InvoiceFingerprint.Compute(invoice),
            Issues = issues.Concat(InvoiceValidation.Validate(invoice)).Select(issue => issue with { InvoiceId = id }).ToImmutableArray() };
    }

    private static void ApplyLegacyDefaults(Dictionary<string, SourceCell> cells, string sheet, int row, ImportConfirmation confirmation)
    {
        void Fill(string field, string value, SourceCell? source = null, bool force = false)
        {
            if (!force && !string.IsNullOrWhiteSpace(Text(cells, field))) return;
            cells[field] = source is null
                ? new(sheet, row, 0, "confirmed:" + field, "", "text", "", null, value,
                    ValueOrigin: "user_override", Confirmation: confirmation.Confirmation)
                : source with { ValueOrigin = "user_override", Confirmation = confirmation.Confirmation };
        }
        // An explicit company confirmation must win over a stale/truncated seller cell.
        Fill("seller", confirmation.CompanyIdentity, force: confirmation.UseLegacySalesDefaults);
        Fill("currency", "VND");
        Fill("invoiceType", "Hóa đơn bán hàng");
        Fill("payment", "Tiền mặt");
        Fill("voucherDate", Text(cells, "signed").Length == 0 ? Text(cells, "issued") : Text(cells, "signed"),
            cells.GetValueOrDefault(Text(cells, "signed").Length == 0 ? "issued" : "signed"));
        Fill("postedDate", Text(cells, "signed").Length == 0 ? Text(cells, "issued") : Text(cells, "signed"),
            cells.GetValueOrDefault(Text(cells, "signed").Length == 0 ? "issued" : "signed"));
        if (Normalize(BuyerName(cells)) == Normalize("BÁN CHO NGƯỜI TIÊU DÙNG"))
        {
            Fill("buyerCode", "NTD");
            Fill("buyerKind", nameof(PartyKind.Individual));
            if (Text(cells, "buyerTaxCode").Length == 0) Fill("buyerTaxPresence", nameof(ValuePresence.NotApplicable));
        }
    }

    private Dictionary<string, int> MapColumns(WorkbookRow header, List<ImportIssue> issues)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (column, cell) in header.Cells)
        {
            var name = Normalize(cell.ParsedValue);
            if (cell.HasFormula) issues.Add(new("HEADER_FORMULA", IssueSeverity.Blocking, "Tiêu đề cột không được là công thức.", cell.Worksheet, cell.Address));
            var matches = Aliases.Where(pair => MappedFields.Contains(pair.Key)
                && pair.Value.Any(alias => Normalize(alias) == name)).Select(pair => pair.Key).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length > 1 || columns.ContainsKey(matches[0]))
            {
                issues.Add(new("COLUMN_AMBIGUOUS", IssueSeverity.Blocking, $"Cột '{cell.ParsedValue}' map không rõ ràng hoặc bị trùng.", cell.Worksheet, cell.Address,
                    Resolution: "Đổi tên cột để mỗi cột map đúng một trường."));
                continue;
            }
            columns[matches[0]] = column;
        }
        return columns;
    }

    private int? SuggestColumn(WorkbookRow header, string field)
    {
        var matches = header.Cells.Values.Where(cell => !cell.HasFormula && SuggestField(cell.ParsedValue ?? "", MappedFields) == field)
            .Select(cell => cell.Column).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static WorkbookRow? FindHeader(WorkbookSheet sheet)
        => sheet.Rows.Take(50).FirstOrDefault(row => row.Cells.Values.Any(cell =>
            Aliases["number"].Any(alias => Normalize(alias) == Normalize(cell.ParsedValue))));

    private Dictionary<string, int> ValidateMapping(ImmutableDictionary<string, int> mapping, WorkbookRow header)
    {
        if (mapping.Keys.Any(key => !MappedFields.Contains(key)) || mapping.Values.Distinct().Count() != mapping.Count
            || mapping.Values.Any(column => !header.Cells.ContainsKey(column) || header.Cells[column].HasFormula))
            throw new WorkbookImportException("MAPPING_INVALID", "Mapping phải dùng field hợp lệ và mỗi cột duy nhất tồn tại trong header.");
        return mapping.ToDictionary();
    }

    private static decimal? Repeated(List<(WorkbookRow Row, IReadOnlyDictionary<string, SourceCell> Cells)> rows, string key, string? locale, List<ImportIssue> issues, string invoiceId)
    {
        var values = rows.Select(row => Number(row.Cells, key, locale, issues)).Where(value => value.HasValue).Select(value => value!.Value).Distinct().ToArray();
        if (values.Length > 1) issues.Add(new("REPEATED_TOTAL_CONFLICT", IssueSeverity.Blocking, $"Tổng '{key}' lặp không nhất quán.", InvoiceId: invoiceId,
            Resolution: "Kiểm tra lại các dòng nguồn; không tự chọn giá trị."));
        return values.Length == 1 ? values[0] : null;
    }

    private static DateOnly? ParseDate(SourceCell cell, bool date1904, string? locale, List<ImportIssue> issues)
    {
        var raw = cell.ParsedValue?.Trim() ?? "";
        if (raw.Length == 0) return null;
        var formats = locale switch { "vi-VN" => new[] { "yyyy-MM-dd", "dd/MM/yyyy" }, "en-US" => ["yyyy-MM-dd", "MM/dd/yyyy"], _ => ["yyyy-MM-dd"] };
        if (cell.CellType is "text" or "date" && DateOnly.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)) return result;
        if (locale is null && cell.CellType == "text")
        {
            var dayFirst = DateOnly.TryParseExact(raw, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayDate);
            var monthFirst = DateOnly.TryParseExact(raw, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthDate);
            // Vietnamese workbooks write dd/MM/yyyy; 05/10/2026 is 5 October.
            if (dayFirst) return dayDate;
            if (monthFirst) return monthDate;
        }
        if (cell.CellType == "number" && DecimalValue.TryParseInvariant(raw, out var serial) && serial == decimal.Truncate(serial)
            && serial >= (date1904 ? 0 : 1) && (date1904 || serial != 60))
        {
            var basis = date1904 ? new DateOnly(1904, 1, 1) : serial < 60 ? new DateOnly(1899, 12, 31) : new DateOnly(1899, 12, 30);
            if (serial <= DateOnly.MaxValue.DayNumber - basis.DayNumber) return basis.AddDays((int)serial);
        }
        issues.Add(new("DATE_AMBIGUOUS", IssueSeverity.Blocking, $"Ngày '{raw}' không xác định được.", cell.Worksheet, cell.Address,
            Resolution: "Dùng ngày YYYY-MM-DD hoặc sửa kiểu ô ngày trong workbook."));
        return null;
    }

    private static decimal? Number(IReadOnlyDictionary<string, SourceCell> values, string key, string? locale, List<ImportIssue> issues)
    {
        if (!values.TryGetValue(key, out var cell) || string.IsNullOrWhiteSpace(cell.ParsedValue)) return null;
        var raw = cell.ParsedValue!.Trim();
        if (cell.CellType == "text")
        {
            var pattern = locale switch
            {
                "vi-VN" => @"^-?(?:[0-9]+|[0-9]{1,3}(?:\.[0-9]{3})+)(?:,[0-9]+)?$",
                "en-US" => @"^-?(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]+)?$",
                "invariant" => @"^-?[0-9]+(?:\.[0-9]+)?$", _ => @"^-?[0-9]+$"
            };
            if (!Regex.IsMatch(raw, pattern, RegexOptions.CultureInvariant)) raw = "";
            else if (locale == "vi-VN") raw = raw.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
            else if (locale == "en-US") raw = raw.Replace(",", "", StringComparison.Ordinal);
        }
        // Excel stores computed values as doubles (e.g. 3333.3333333333335); keep 6 decimals instead of rejecting them.
        if (cell.CellType == "number" && raw.Replace(".", "", StringComparison.Ordinal).TrimStart('-', '0').Length > 15
            && decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var wide))
            raw = decimal.Round(wide, 6, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        if (cell.CellType is "number" or "text" && DecimalValue.TryParseInvariant(raw, out var result)
            && (cell.CellType != "number" || raw.Replace(".", "", StringComparison.Ordinal).TrimStart('-', '0').Length <= 15)) return result;
        issues.Add(new("NUMBER_INVALID", IssueSeverity.Blocking, $"Giá trị số '{cell.RawValue}' không hợp lệ.", cell.Worksheet, cell.Address,
            Resolution: "Dùng số theo locale rõ ràng, không dùng chuỗi mơ hồ."));
        return null;
    }

    private static string Text(IReadOnlyDictionary<string, SourceCell> cells, string key) => cells.TryGetValue(key, out var cell) ? cell.ParsedValue?.Trim() ?? "" : "";
    private static string BuyerName(IReadOnlyDictionary<string, SourceCell> cells)
        => Text(cells, "buyerCompany") is { Length: > 0 } company ? company : Text(cells, "buyer");
    private static ValuePresence Presence(string value) => string.IsNullOrWhiteSpace(value) ? ValuePresence.Missing : ValuePresence.Provided;
    private static SourceCell Blank(string sheet, int row, int column) => new(sheet, row, column, CellAddress.Format(column, row), "", "blank", "", null, null);
    private static string Normalize(string? value) => Regex.Replace(new string((value ?? "").Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray())
        .Replace('đ', 'd').Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string? SuggestField(string header) => SuggestField(header, SelectedFields);
    private static string? SuggestField(string header, IReadOnlyList<string> selected)
    {
        var fields = Aliases.Where(pair => selected.Contains(pair.Key)
            && pair.Value.Any(alias => Normalize(alias) == Normalize(header))).Select(pair => pair.Key).ToArray();
        return fields.Length == 1 ? fields[0] : null;
    }
    private static ImportResult EmptyResult(WorkbookData data, WorkbookSheet sheet, int headerRow, List<ImportIssue> issues) => new(
        new SourceReference(data.FilePath, data.Sha256, sheet.Name, headerRow, sheet.Rows.LastOrDefault()?.Number ?? headerRow), data.Sheets.Select(item => item.Name).ToArray(), [], issues, sheet.Rows.Length, 0);
}
