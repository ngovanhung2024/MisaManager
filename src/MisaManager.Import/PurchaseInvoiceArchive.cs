using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MisaManager.Core;

namespace MisaManager.Import;

public sealed record PurchaseHtmlLine(string Kind, string Name, string Unit, decimal? Quantity, decimal? UnitPrice,
    decimal Amount, string TaxRate, decimal TaxAmount);

public sealed record PurchaseHtmlInvoice(string File, string Template, string Series, string Number, DateOnly Issued,
    string Seller, string SellerTaxCode, string SellerAddress, string Buyer, string BuyerTaxCode,
    IReadOnlyList<PurchaseHtmlLine> Lines, decimal? StatedTotal)
{
    public decimal Subtotal => Lines.Sum(line => line.Amount);
    public decimal TaxTotal => Lines.Sum(line => line.TaxAmount);
}

public sealed record PurchaseArchiveResult(IReadOnlyList<PurchaseHtmlInvoice> Invoices, IReadOnlyList<string> Skipped);

/// <summary>
/// Purchase invoices downloaded from the tax portal as one HTML page each (usually zipped). They are read, de-duplicated,
/// sorted from the start to the end of the year and written to one Excel sheet that the invoice importer reads in
/// purchase mode. Discount/"Điều chỉnh giảm" lines are written as negative amounts so such invoices are left out like
/// other negative invoices.
/// </summary>
public static partial class PurchaseInvoiceArchive
{
    public const string SheetName = "HoaDonMuaVao";
    private const int MaxFiles = 20000;
    private const long MaxFileBytes = 20L * 1024 * 1024;

    public static readonly string[] Headers =
    [
        "Ngày hóa đơn", "Mẫu số", "Ký hiệu", "Số hóa đơn", "Tên người bán", "MST người bán", "Địa chỉ người bán",
        "Tên người mua", "MST người mua", "Tính chất dòng", "Tên hàng hóa", "Đơn vị tính", "Số lượng", "Đơn giá",
        "Thành tiền", "Thuế suất", "Tiền thuế", "Tổng tiền có thuế", "Tổng thanh toán hóa đơn", "Phân loại", "Lý do phân loại", "File"
    ];

    /// <summary>Reads a .zip of HTML invoices, a folder of them, or a single HTML file.</summary>
    public static PurchaseArchiveResult Read(string path, CancellationToken token = default)
    {
        var pages = new List<(string Name, string Html)>();
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.htm*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (pages.Count >= MaxFiles) throw Fault("ARCHIVE_LIMIT", $"Thư mục có hơn {MaxFiles:N0} file HTML.");
                if (new FileInfo(file).Length <= MaxFileBytes) pages.Add((Path.GetFileName(file), File.ReadAllText(file, Encoding.UTF8)));
            }
        }
        else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (!IsHtml(entry.Name)) continue;
                if (pages.Count >= MaxFiles) throw Fault("ARCHIVE_LIMIT", $"File zip có hơn {MaxFiles:N0} file HTML.");
                if (entry.Length > MaxFileBytes) continue;
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                pages.Add((entry.Name, reader.ReadToEnd()));
            }
        }
        else pages.Add((Path.GetFileName(path), File.ReadAllText(path, Encoding.UTF8)));

        var invoices = new List<PurchaseHtmlInvoice>();
        var skipped = new List<string>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var (name, html) in pages)
        {
            token.ThrowIfCancellationRequested();
            if (IsCancelledFile(name)) { skipped.Add(name + ": hóa đơn đã hủy"); continue; }
            PurchaseHtmlInvoice invoice;
            try { invoice = Parse(html, name); }
            catch (WorkbookImportException error) { skipped.Add(name + ": " + error.Message); continue; }
            if (!seen.Add((PartyCatalogStore.TaxKey(invoice.SellerTaxCode), PostedInvoiceStore.SeriesKey(invoice.Series), InvoiceLookup.NumberKey(invoice.Number))))
            {
                skipped.Add(name + ": trùng hóa đơn đã đọc");
                continue;
            }
            invoices.Add(invoice);
        }
        if (invoices.Count == 0 && pages.Count == 0) throw Fault("ARCHIVE_EMPTY", "Không có file HTML hóa đơn nào trong nguồn đã chọn.");
        return new(Sort(invoices), skipped);
    }

    /// <summary>Start of the year first: date, then supplier, then invoice number.</summary>
    public static IReadOnlyList<PurchaseHtmlInvoice> Sort(IEnumerable<PurchaseHtmlInvoice> invoices)
        => [.. invoices.OrderBy(invoice => invoice.Issued)
            .ThenBy(invoice => invoice.Seller, StringComparer.Ordinal)
            .ThenBy(invoice => long.TryParse(invoice.Number, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : long.MaxValue)
            .ThenBy(invoice => invoice.Number, StringComparer.Ordinal)];

    public static PurchaseHtmlInvoice Parse(string html, string file)
    {
        html = Noise().Replace(html, " ");
        var code = Text(CodeBlock().Match(html).Groups[1].Value);
        var template = CodeValue(code, "Mẫu số");
        var series = CodeValue(code, "Ký hiệu");
        var number = CodeValue(code, "Số");
        var day = DateText().Match(Text(html));
        if (number.Length == 0 || !day.Success)
            throw Fault("HTML_NOT_INVOICE", "không đọc được số hoặc ngày hóa đơn.");
        if (!DateOnly.TryParseExact($"{day.Groups[1].Value.PadLeft(2, '0')}/{day.Groups[2].Value.PadLeft(2, '0')}/{day.Groups[3].Value}",
                "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued))
            throw Fault("HTML_DATE_INVALID", "ngày hóa đơn không hợp lệ.");

        // Labels repeat for both parties: everything before "Tên người mua" belongs to the seller.
        var fields = DataItem().Matches(html).Select(match => (Label: Plain(Text(match.Groups[1].Value)), Value: Text(match.Groups[2].Value))).ToArray();
        var buyerStart = Array.FindIndex(fields, field => field.Label.StartsWith("tennguoimua", StringComparison.Ordinal));
        if (buyerStart < 0) buyerStart = fields.Length;
        string Field(int from, int to, string label)
            => fields.Skip(from).Take(to - from).FirstOrDefault(field => field.Label.StartsWith(label, StringComparison.Ordinal)).Value ?? "";
        var seller = Field(0, buyerStart, "tennguoiban");
        var sellerTax = Field(0, buyerStart, "masothue");
        var sellerAddress = Field(0, buyerStart, "diachi");
        var buyer = Field(buyerStart, fields.Length, "tennguoimua");
        if (buyer.Length == 0) buyer = Field(buyerStart, fields.Length, "hotennguoimua");
        var buyerTax = Field(buyerStart, fields.Length, "masothue");

        var tables = Table().Matches(html).Select(match => match.Groups[1].Value).ToArray();
        if (tables.Length == 0) throw Fault("HTML_LINES_MISSING", "không có bảng hàng hóa.");
        var lines = ReadLines(tables[0]);
        if (lines.Count == 0) throw Fault("HTML_LINES_MISSING", "bảng hàng hóa không có dòng.");
        var taxes = tables.Length > 1 ? ReadTaxes(tables[1]) : new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal? statedTotal = null;
        if (tables.Length > 2)
            foreach (var row in Rows(tables[2]))
                if (row.Length >= 2 && Plain(row[0]).StartsWith("tongtienthanhtoanbangso", StringComparison.Ordinal)) statedTotal = Number(row[1]);
        return new(file, template, series, number, issued, seller, sellerTax, sellerAddress, buyer, buyerTax,
            AllocateTax(lines, taxes), statedTotal);
    }

    private static List<PurchaseHtmlLine> ReadLines(string table)
    {
        var headers = Header().Matches(table).Select(match => Plain(Text(match.Groups[1].Value))).ToArray();
        int Column(string key) => Array.FindIndex(headers, header => header.StartsWith(key, StringComparison.Ordinal));
        var (kind, name, unit, quantity, price, rate, amount) = (Column("tinhchat"), Column("tenhanghoa"), Column("donvitinh"),
            Column("soluong"), Column("dongia"), Column("thuesuat"), Column("thanhtien"));
        if (name < 0 || amount < 0) throw Fault("HTML_LINES_MISSING", "bảng hàng hóa thiếu cột Tên hàng/Thành tiền.");
        var lines = new List<PurchaseHtmlLine>();
        foreach (var row in Rows(table))
        {
            if (row.Length <= Math.Max(name, amount)) continue;
            string Cell(int index) => index >= 0 && index < row.Length ? row[index] : "";
            var lineName = Cell(name);
            if (lineName.Length == 0 && Cell(amount).Length == 0) continue;
            var lineKind = Cell(kind);
            var value = Number(Cell(amount)) ?? 0;
            // The portal shows reductions as positive amounts; the kind or the name tells they subtract.
            var reduction = Plain(lineKind).Contains("chietkhau", StringComparison.Ordinal) || ItemNatures.IsReduction(lineName);
            var lineQuantity = Number(Cell(quantity));
            var linePrice = Number(Cell(price));
            // Gifts ("Khuyến mại") show no price; their price is 0.
            if (linePrice is null && value == 0 && lineQuantity is not null) linePrice = 0;
            lines.Add(new(lineKind, lineName, Cell(unit), lineQuantity, linePrice, reduction ? -Math.Abs(value) : value, Cell(rate), 0));
        }
        return lines;
    }

    private static Dictionary<string, decimal> ReadTaxes(string table)
    {
        var taxes = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var row in Rows(table))
            if (row.Length >= 3 && RateKey(row[0]) is { } rate && Number(row[2]) is { } tax) taxes[rate] = tax;
        return taxes;
    }

    /// <summary>
    /// The portal gives tax per rate only. Each line gets amount × rate rounded to whole đồng, and the rounding
    /// difference to the invoice's tax for that rate goes to its largest line, so line taxes add up to the invoice.
    /// </summary>
    private static IReadOnlyList<PurchaseHtmlLine> AllocateTax(List<PurchaseHtmlLine> lines, Dictionary<string, decimal> taxes)
    {
        var result = lines.Select(line => RateKey(line.TaxRate) is { } key && decimal.TryParse(key, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)
            ? line with { TaxAmount = decimal.Round(line.Amount * rate / 100m, 0, MidpointRounding.AwayFromZero) } : line).ToList();
        foreach (var group in result.Select((line, index) => (line, index)).Where(item => RateKey(item.line.TaxRate) is not null)
                     .GroupBy(item => RateKey(item.line.TaxRate)!))
        {
            if (!taxes.TryGetValue(group.Key, out var stated)) continue;
            var difference = stated - group.Sum(item => item.line.TaxAmount);
            if (difference == 0) continue;
            var largest = group.OrderByDescending(item => item.line.Amount).First().index;
            result[largest] = result[largest] with { TaxAmount = result[largest].TaxAmount + difference };
        }
        return result;
    }

    /// <summary>Writes the invoices to one sheet with the importer's purchase headers.</summary>
    public static void WriteWorkbook(string path, IReadOnlyList<PurchaseHtmlInvoice> invoices)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var document = SpreadsheetDocument.Create(temporary, SpreadsheetDocumentType.Workbook))
            {
                var workbook = document.AddWorkbookPart();
                workbook.Workbook = new Workbook();
                var sheetPart = workbook.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                var rowNumber = 1u;
                data.Append(Row(rowNumber++, Headers.Select(header => (object?)header)));
                foreach (var invoice in invoices)
                    foreach (var line in invoice.Lines)
                    {
                        var (service, reason) = ItemNatures.Classify(line.Name, line.Unit, line.Quantity);
                        var discount = line.Amount < 0;
                        data.Append(Row(rowNumber++,
                        [
                            invoice.Issued.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), invoice.Template, invoice.Series, invoice.Number,
                            invoice.Seller, invoice.SellerTaxCode, invoice.SellerAddress, invoice.Buyer, invoice.BuyerTaxCode,
                            line.Kind, line.Name, line.Unit, line.Quantity, line.UnitPrice, line.Amount, line.TaxRate, line.TaxAmount,
                            line.Amount + line.TaxAmount, invoice.StatedTotal,
                            discount ? "" : service ? ItemNatures.ServiceLabel : ItemNatures.GoodsLabel,
                            discount ? "chiết khấu/điều chỉnh giảm – không nhập" : reason, invoice.File
                        ]));
                    }
                sheetPart.Worksheet = new Worksheet(new SheetViews(new SheetView(new Pane
                    {
                        VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen
                    }) { WorkbookViewId = 0 }), data);
                workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(sheetPart), SheetId = 1, Name = SheetName }));
                workbook.Workbook.Save();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Row Row(uint number, IEnumerable<object?> values)
    {
        var row = new Row { RowIndex = number };
        var column = 0;
        foreach (var value in values)
        {
            column++;
            var reference = CellAddress.Format(column, (int)number);
            switch (value)
            {
                case null: break;
                case decimal amount:
                    row.Append(new Cell { CellReference = reference, CellValue = new CellValue(amount.ToString(CultureInfo.InvariantCulture)) });
                    break;
                case string text when text.Length > 0:
                    row.Append(new Cell { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(text)) });
                    break;
            }
        }
        return row;
    }

    private static IEnumerable<string[]> Rows(string table)
        => RowTag().Matches(table).Select(match => CellTag().Matches(match.Groups[1].Value).Select(cell => Text(cell.Groups[1].Value)).ToArray())
            .Where(cells => cells.Length > 0);

    private static string CodeValue(string code, string label)
    {
        var match = Regex.Match(code, Regex.Escape(label) + @"\s*:\s*(\S+)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    /// <summary>"5%", "5", "KCT"… → "5"/"KCT"; null when empty.</summary>
    private static string? RateKey(string? rate)
    {
        var text = (rate ?? "").Trim().TrimEnd('%').Trim();
        if (text.Length == 0) return null;
        return decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.####", CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Vietnamese display numbers: "1.234.567,5"; "-" or brackets mean negative.</summary>
    internal static decimal? Number(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return null;
        var negative = raw.StartsWith('-') || raw.StartsWith('(') && raw.EndsWith(')');
        raw = raw.Trim('-', '(', ')', ' ').Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        return decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? negative ? -value : value : null;
    }

    private static bool IsHtml(string name) => name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    // Portal file names end with "_<trạng thái>_<hash>.html" ("dung" = valid).
    private static bool IsCancelledFile(string name)
    {
        var parts = Path.GetFileNameWithoutExtension(name).Split('_');
        return parts.Length >= 3 && Plain(parts[^2]) is "huy" or "dahuy" or "bihuy";
    }

    // WebUtility.HtmlDecode knows HTML 4 entities only; the portal also writes HTML 5 ones such as "n&abreve;m".
    private static readonly Dictionary<string, string> Html5Entities = new(StringComparer.Ordinal)
    {
        ["&abreve;"] = "ă", ["&Abreve;"] = "Ă", ["&dstrok;"] = "đ", ["&Dstrok;"] = "Đ", ["&itilde;"] = "ĩ", ["&Itilde;"] = "Ĩ",
        ["&utilde;"] = "ũ", ["&Utilde;"] = "Ũ", ["&ndash;"] = "–", ["&mdash;"] = "—"
    };

    private static string Text(string html)
    {
        var text = Tag().Replace(html, " ");
        foreach (var (entity, value) in Html5Entities) text = text.Replace(entity, value, StringComparison.Ordinal);
        return WhiteSpace().Replace(WebUtility.HtmlDecode(text), " ").Trim();
    }
    private static string Plain(string value) => ItemNatures.Plain(value);
    private static WorkbookImportException Fault(string code, string message) => new(code, message);

    [GeneratedRegex(@"<(script|style|svg)\b.*?</\1\s*>|data:[^""')\s]+", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Noise();
    [GeneratedRegex(@"class=""code-content""[^>]*>(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();
    [GeneratedRegex(@"Ngày\s+(\d{1,2})\s+tháng\s+(\d{1,2})\s+năm\s+(\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex DateText();
    [GeneratedRegex(@"<div class=""di-label"">(.*?)</div>\s*<div class=""di-value"">(.*?)</div>\s*</div>", RegexOptions.Singleline)]
    private static partial Regex DataItem();
    [GeneratedRegex(@"<table\b[^>]*class=""res-tb""[^>]*>(.*?)</table>", RegexOptions.Singleline)]
    private static partial Regex Table();
    [GeneratedRegex(@"<th\b[^>]*>(.*?)</th>", RegexOptions.Singleline)]
    private static partial Regex Header();
    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline)]
    private static partial Regex RowTag();
    [GeneratedRegex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline)]
    private static partial Regex CellTag();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
