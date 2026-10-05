using System.Globalization;
using MisaManager.Core;

namespace MisaManager.Automation;

/// <summary>Result of comparing the voucher open in MISA with a source invoice, outside any workflow run.</summary>
/// <remarks><see cref="Invoice"/> is null when the loaded workbook has no invoice with MISA's number: values then carry no Excel side.</remarks>
public sealed record OpenVoucherComparison(ReconciliationResult Result, string VoucherNumber, string CustomerName,
    InvoiceDocument? Invoice = null, string InvoiceNumber = "", string IssuedDate = "", string GrandTotal = "");

public sealed partial class NativeWorkflowGateway
{
    // MISA fills in its own name for a code found in the customer/supplier list; that is what readback must show.
    private string ExpectedCustomerName(WorkflowContext context)
        => BuyerCodes.Resolve(context.Invoice.Buyer, PartyCatalog)?.CatalogName ?? context.Invoice.Buyer.Name;

    private string BuyerCode(WorkflowContext context)
        => BuyerCodes.Resolve(context.Invoice.Buyer, PartyCatalog)?.Code
            ?? throw new AutomationException("CUSTOMER_CODE_MISSING",
                $"Hóa đơn {context.Invoice.Identity.Number}: chưa có mã khách hàng cho '{context.Invoice.Buyer.Name}'. "
                + "Ghi mã vào cột Mã khách hàng của file Excel, hoặc nạp danh mục khách hàng/nhà cung cấp ở menu ••• rồi chạy lại.");

    /// <summary>
    /// Read-only "Đối chiếu hóa đơn": reads the sales voucher currently open in MISA (header, every grid row on both
    /// tabs, totals) and compares it with <paramref name="invoice"/>. Only tabs are switched; nothing is typed, saved or closed.
    /// Lines pair by item code, then by normalised name, then by position when both sides have the same row count.
    /// </summary>
    public Task<OpenVoucherComparison> CompareOpenVoucherAsync(InvoiceDocument invoice, WindowIdentity window, CancellationToken token)
        => CompareOpenVoucherAsync(window, _ => invoice, token);

    /// <summary>
    /// Same read, but the source invoice is the one in <paramref name="candidates"/> carrying the invoice number MISA shows.
    /// Without such an invoice the result holds only what MISA shows (no Excel side).
    /// </summary>
    public Task<OpenVoucherComparison> CompareOpenVoucherAsync(IReadOnlyList<InvoiceDocument> candidates, WindowIdentity window, CancellationToken token)
        => CompareOpenVoucherAsync(window, read => InvoiceLookup.ByNumber(candidates, read.Header["number"].Value, read.Header["series"].Value), token);

    private async Task<OpenVoucherComparison> CompareOpenVoucherAsync(WindowIdentity window, Func<OpenVoucherRead, InvoiceDocument?> pick,
        CancellationToken token)
    {
        var session = windows.Focus(window.WindowHandle, window.ProcessId);
        if (!session.Title.StartsWith(profile.VersionTitle + " - ", StringComparison.Ordinal))
            throw new AutomationException("WINDOW_IDENTITY_CHANGED", "MISA đang ở màn hình đăng nhập hoặc không phải bản " + profile.VersionTitle + ".");
        _ = await input.TryBindWorkflowScopeAsync(session, profile.EditorId, null, token)
            ?? throw new AutomationException("EDITOR_NOT_FOUND",
                "Chưa có chứng từ bán hàng nào đang mở trong MISA. Mở chứng từ cần đối chiếu rồi bấm Đọc lại.");
        var read = await ReadOpenVoucherAsync(token);
        var first = Compare(pick(read), read);
        if (first.Invoice is null || first.Result.Matched) return first;
        // One confirming read: a grid that was still repainting after the tab switch must not count as a mismatch.
        await Task.Delay(400, token);
        read = await ReadOpenVoucherAsync(token);
        return Compare(pick(read), read);
    }

    private sealed record OpenVoucherRead(IReadOnlyDictionary<string, Readback> Receipt, IReadOnlyDictionary<string, Readback> Header,
        Readback? Tax, IReadOnlyList<IReadOnlyDictionary<string, string>> Items, IReadOnlyList<IReadOnlyDictionary<string, string>> Taxes,
        IReadOnlyDictionary<string, Readback> Totals);

    private async Task<OpenVoucherRead> ReadOpenVoucherAsync(CancellationToken token)
    {
        async Task<IReadOnlyDictionary<string, Readback>> ReadTab(string tab, IEnumerable<string> keys)
        {
            await Tab(tab, token);
            var fields = keys.Distinct(StringComparer.Ordinal).ToDictionary(key => key, key => profile.Fields[key], StringComparer.Ordinal);
            return (await NativeWait.UntilAsync(async () => await input.ReadFieldsAsync(await input.CaptureAsync(token), fields, token),
                _ => true, "TARGET_NOT_FOUND", "Các trường chưa sẵn sàng sau khi chuyển tab.", token)).Values;
        }
        var receipt = await ReadTab(profile.ReceiptTab, ["voucher_number", "customer", "customer_name", "voucher_date", "posted_date"]);
        var tax = CustomerTaxTab == profile.ReceiptTab ? await ReadCustomerTaxAsync(token) : null;
        var header = await ReadTab(profile.InvoiceTab, ["number", "issued_date", "series"]);
        if (CustomerTaxTab != profile.ReceiptTab) tax = await ReadCustomerTaxAsync(token);
        var itemFrame = await Tab(profile.ItemTab, token);
        var items = await input.ReadCompleteTableAsync(itemFrame, profile.ItemGridId,
            new[] { "code", "name", "unit", "quantity", "amount", "unit_price" }.Select(key => profile.Columns[key]).ToArray(), token);
        var totals = (await input.ReadFieldsAsync(await input.CaptureAsync(token),
            new[] { "subtotal", "tax_total", "grand_total" }.ToDictionary(key => key, key => profile.Fields[key]), token)).Values;
        var taxFrame = await Tab(profile.TaxTab, token);
        var taxes = await input.ReadCompleteTableAsync(taxFrame, profile.ItemGridId,
            new[] { "code", "name", "tax_rate", "tax_amount" }.Select(key => profile.Columns[key]).ToArray(), token);
        return new(receipt, header, tax, items.Rows, taxes.Rows, totals);
    }

    private string Cell(IReadOnlyDictionary<string, string> row, string key) => row.GetValueOrDefault(profile.Columns[key])?.Trim() ?? "";

    private (IReadOnlyDictionary<string, string> Row, int Index)[] NativeLines(OpenVoucherRead read)
        => read.Items.Select((row, index) => (Row: row, Index: index))
            .Where(item => Cell(item.Row, "code").Length > 0 || Cell(item.Row, "name").Length > 0).ToArray();

    private OpenVoucherComparison Identify(OpenVoucherRead read, ReconciliationResult result, InvoiceDocument? invoice)
        => new(result, read.Receipt["voucher_number"].Value?.Trim() ?? "", read.Receipt["customer_name"].Value?.Trim() ?? "", invoice,
            read.Header["number"].Value?.Trim() ?? "", read.Header["issued_date"].Value?.Trim() ?? "", read.Totals["grand_total"].Value?.Trim() ?? "");

    /// <summary>Every value MISA shows, with an empty Excel side: the voucher's invoice is not in the loaded workbook.</summary>
    private OpenVoucherComparison MisaOnly(OpenVoucherRead read)
    {
        var values = new List<ValueComparison>();
        void Add(string field, string? actual, string source = "UIA3") => values.Add(new(field, "", actual, false, source));
        void AddRead(string field, Readback? read) => Add(field, read?.Value, read?.Source ?? "UIA3");
        // R20 exposes the selected customer's internal GUID instead of its code.
        if (!Guid.TryParse(read.Receipt["customer"].Value, out _)) AddRead("customer", read.Receipt["customer"]);
        AddRead("customer_name", read.Receipt["customer_name"]);
        if (read.Tax is not null) AddRead("customer_tax", read.Tax);
        AddRead("posted_date", read.Receipt["posted_date"]);
        AddRead("voucher_date", read.Receipt["voucher_date"]);
        AddRead("issued_date", read.Header["issued_date"]);
        AddRead("number", read.Header["number"]);
        var native = NativeLines(read);
        foreach (var (row, index) in native)
        {
            var taxRow = index < read.Taxes.Count ? read.Taxes[index] : null;
            foreach (var key in new[] { "code", "name", "unit", "quantity", "unit_price", "amount" })
                Add($"line:misa:{index}:{key}", Cell(row, key), "UIA3.GridPattern");
            Add($"line:misa:{index}:tax_rate", taxRow is null ? null : Cell(taxRow, "tax_rate"), "UIA3.GridPattern");
            Add($"line:misa:{index}:tax_amount", taxRow is null ? null : Cell(taxRow, "tax_amount"), "UIA3.GridPattern");
        }
        Add("item_row_count", native.Length.ToString(CultureInfo.InvariantCulture), "UIA3.GridPattern");
        foreach (var key in new[] { "subtotal", "tax_total", "grand_total" }) AddRead(key, read.Totals[key]);
        return Identify(read, new("", 0, DateTimeOffset.UtcNow, values), null);
    }

    private OpenVoucherComparison Compare(InvoiceDocument? invoice, OpenVoucherRead read)
    {
        if (invoice is null) return MisaOnly(read);
        var values = new List<ValueComparison>();
        void Add(string field, string expected, Readback? read, bool matched)
            => values.Add(new(field, expected, read?.Value, matched, read?.Source ?? "UIA3"));
        bool Text(string expected, string? actual) => Match(expected, actual, NativeValueKind.Text) || actual is not null && CatalogNameMatch(expected, actual);
        static string Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

        var taxCode = invoice.Buyer.TaxCode.Trim();
        var receipt = read.Receipt;
        BuyerCode? resolved;
        try { resolved = BuyerCodes.Resolve(invoice.Buyer, PartyCatalog); }
        catch (AutomationException) { resolved = null; }
        var expectedCode = resolved?.Code;
        if (taxCode.Length > 0) values.Add(CustomerTaxComparison(taxCode, expectedCode, read.Tax));
        // R20 exposes the selected customer's internal GUID instead of its code; the name identifies it then.
        var customer = receipt["customer"];
        if (expectedCode is not null && !Guid.TryParse(customer.Value, out _))
            Add("customer", expectedCode, customer, string.Equals(expectedCode, customer.Value?.Trim(), StringComparison.OrdinalIgnoreCase));
        var misaName = receipt["customer_name"].Value;
        Add("customer_name", invoice.Buyer.Name, receipt["customer_name"], Text(invoice.Buyer.Name, misaName)
            || resolved?.CatalogName is { } catalogName && Text(catalogName, misaName));
        Add("voucher_date", Day(invoice.VoucherDate), receipt["voucher_date"], Match(Day(invoice.VoucherDate), receipt["voucher_date"].Value, NativeValueKind.Date));
        Add("posted_date", Day(invoice.PostedDate), receipt["posted_date"], Match(Day(invoice.PostedDate), receipt["posted_date"].Value, NativeValueKind.Date));

        var header = read.Header;
        Add("number", invoice.Identity.Number, header["number"], Match(invoice.Identity.Number, header["number"].Value, NativeValueKind.Exact));
        Add("issued_date", Day(invoice.Issued), header["issued_date"], Match(Day(invoice.Issued), header["issued_date"].Value, NativeValueKind.Date));
        if (invoice.Identity.Series.Length > 0)
            Add("series", invoice.Identity.Series, header["series"], Match(invoice.Identity.Series, header["series"].Value, NativeValueKind.Exact));

        var totals = read.Totals;
        var taxes = read.Taxes;
        var native = NativeLines(read);
        var used = new bool[native.Length];
        var lines = invoice.Lines.Select(line => (Line: line, Code: ExpectedItemCode(line))).ToArray();
        var pairs = new int?[lines.Length];
        bool Claim(int line, Func<int, bool> accept)
        {
            for (var i = 0; i < native.Length; i++)
                if (!used[i] && accept(i)) { used[i] = true; pairs[line] = i; return true; }
            return false;
        }
        for (var l = 0; l < lines.Length; l++)
            if (lines[l].Code is { Length: > 0 } code)
                _ = Claim(l, i => string.Equals(Cell(native[i].Row, "code"), code, StringComparison.OrdinalIgnoreCase)
                        && CatalogNameMatch(lines[l].Line.Name, Cell(native[i].Row, "name")))
                    || Claim(l, i => string.Equals(Cell(native[i].Row, "code"), code, StringComparison.OrdinalIgnoreCase));
        for (var l = 0; l < lines.Length; l++)
            if (pairs[l] is null) Claim(l, i => CatalogNameMatch(lines[l].Line.Name, Cell(native[i].Row, "name")));
        if (native.Length == lines.Length)
            for (var l = 0; l < lines.Length; l++)
                if (pairs[l] is null && !used[l]) { used[l] = true; pairs[l] = l; }

        for (var l = 0; l < lines.Length; l++)
        {
            var (line, code) = lines[l];
            var row = pairs[l] is { } index ? native[index] : default;
            var taxRow = row.Row is not null && row.Index < taxes.Count ? taxes[row.Index] : null;
            string? Actual(IReadOnlyDictionary<string, string>? source, string key) => source is null ? null : Cell(source, key);
            var source = row.Row is null ? "UIA3.GridPattern:missing_row" : "UIA3.GridPattern";
            void Line(string key, string expected, string? actual, bool matched) => values.Add(new($"line:{line.LineId}:{key}", expected, actual, matched, source));
            var unit = Actual(row.Row, "unit");
            // The tool enters Kg lines as Tấn when the item is registered in tons.
            var tons = unit is not null && !Match(line.Unit, unit, NativeValueKind.Text)
                && Match(line.Unit, "Kg", NativeValueKind.Text) && Match("T\u1ea5n", unit, NativeValueKind.Text);
            var quantity = tons ? line.Quantity / 1000m : line.Quantity;
            if (code is { Length: > 0 }) Line("code", code, Actual(row.Row, "code"), string.Equals(code, Actual(row.Row, "code"), StringComparison.OrdinalIgnoreCase));
            Line("name", line.Name, Actual(row.Row, "name"), CatalogNameMatch(line.Name, Actual(row.Row, "name")));
            Line("unit", tons ? "T\u1ea5n" : line.Unit, unit, tons || Match(line.Unit, unit, NativeValueKind.Text));
            Line("quantity", Number(quantity), Actual(row.Row, "quantity"), Match(Number(quantity), Actual(row.Row, "quantity"), NativeValueKind.Decimal));
            Line("amount", Number(line.AmountBeforeTax), Actual(row.Row, "amount"), Match(Number(line.AmountBeforeTax), Actual(row.Row, "amount"), NativeValueKind.Decimal));
            var price = Actual(row.Row, "unit_price");
            Line("unit_price", Number(quantity is > 0 && line.AmountBeforeTax is { } amount ? decimal.Round(amount / quantity.Value, 2, MidpointRounding.AwayFromZero) : line.UnitPrice),
                price, quantity is > 0 && line.AmountBeforeTax is { } total && NativeComparison.MatchesDerivedUnitPrice(quantity.Value, total, price, Culture)
                    || Match(Number(line.UnitPrice), price, NativeValueKind.Decimal));
            Line("tax_rate", line.TaxRate, Actual(taxRow, "tax_rate"), Match(line.TaxRate, Actual(taxRow, "tax_rate"), NativeValueKind.TaxRate));
            Line("tax_amount", Number(line.TaxAmount), Actual(taxRow, "tax_amount"), Match(Number(line.TaxAmount), Actual(taxRow, "tax_amount"), NativeValueKind.Decimal));
        }
        for (var i = 0; i < native.Length; i++)
            if (!used[i])
                values.Add(new($"line:unexpected:{native[i].Index}:code", "<không có trong Excel>",
                    Cell(native[i].Row, "code") + " · " + Cell(native[i].Row, "name"), false, "UIA3.GridPattern"));
        values.Add(new("item_row_count", invoice.Lines.Length.ToString(CultureInfo.InvariantCulture),
            native.Length.ToString(CultureInfo.InvariantCulture), native.Length == invoice.Lines.Length, "UIA3.GridPattern"));
        foreach (var (key, wanted) in new[] { ("subtotal", invoice.Subtotal), ("tax_total", invoice.TaxTotal), ("grand_total", invoice.GrandTotal) })
            Add(key, Number(wanted), totals[key], Match(Number(wanted), totals[key].Value, NativeValueKind.Decimal));
        return Identify(read, new(invoice.InvoiceId, invoice.Revision, DateTimeOffset.UtcNow, values, invoice.Source.Sha256), invoice);
    }

    private string? ExpectedItemCode(InvoiceLine line)
    {
        if (line.ItemCode.Trim().Length > 0) return line.ItemCode.Trim();
        try { return ItemCatalog?.Find(line.Name, line.Unit, allowKgToTon: true)?.Code; }
        catch (AutomationException) { return null; }
    }
}
