using System.Globalization;
using System.Text.Json;

namespace MisaManager.FakeMisa;

public sealed record Customer(string Code, string Name, string Address, string TaxCode);
public sealed record CatalogItem(string Code, string Name, string Unit, string TaxRate, decimal Price = 0);

/// <summary>In-memory "database" of the fake; saved vouchers are mirrored to the --state file.</summary>
public sealed class FakeStore(Scenario scenario, string? statePath)
{
    public static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public List<Customer> Customers { get; } =
    [
        new("A&V", "CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V", "Số 19B, ngõ 42, phố Trịnh Đình Cửu", "0107286506"),
        new("BACH MÃ", "VƯỜN QUỐC GIA BẠCH MÃ", "Thị trấn Phú Lộc, Huyện Phú Lộc", "3300305784"),
        new("BO", "PHẠM THỊ BO", "Chợ Khe Tre, Thị trấn Khe Tre", "3300689139"),
        new("DMH", "CÔNG TY CỔ PHẦN DỆT MAY HUẾ", "Số 122 Dương Thiệu Tước", "3300100628"),
        new("HPL", "TRUNG TÂM DỊCH VỤ NÔNG NGHIỆP HUYỆN PHÚ LỘC", "Thị Trấn Phú Lộc", "3301638391"),
        new("HT", "TRUNG TÂM DỊCH VỤ NÔNG NGHIỆP THỊ XÃ HƯƠNG THỦY", "05 Hà Thế Hạnh, Phường Tứ Hạ", "3301641845"),
        new("KHAC", "KHÁC", "", ""),
        new("KL", "Khách lẻ", "", ""),
        new("NTD", "BÁN CHO NGƯỜI TIÊU DÙNG", "", "")
    ];

    public List<CatalogItem> Items { get; } =
    [
        new("CM GOLD 20.10.10", "Phân NPK Cà Mau Gold 20-10-10-25kg", "Tấn", "5%"),
        new("CPMH", "Chi phí mua hàng", "", "10%"),
        new("HH", "HÀNG HÓA CHUNG", "", "10%"),
        new("HH _CAI", "HANG HOA CAI", "Cái", "10%"),
        new("HH _COC", "HANG HOA COC", "Cốc", "10%"),
        new("HH _CHAI", "HANG HOA CHAI", "Chai", "5%"),
        new("HH _GOI", "HANG HOA GOI", "Gói", "5%"),
        new("HH _KG", "HANG HOA KG", "kg", "5%"),
        new("HH_01", "Chery 15g", "Gói", "5%"),
        new("HH_02", "Phân NPK 16.16.8.13S QN", "kg", "5%"),
        new("HH_03", "Phân kali", "kg", "5%"),
        new("HH_04", "Phân ĐT Lót thúc 17.12.5", "kg", "5%"),
        new("HH_05", "Phân NPK 16.16.8.6S", "kg", "5%"),
        new("HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", "5%"),
        new("HH_07", "Hesman 60WG 18g", "Gói", "8%"),
        new("HH_08", "Phân Ure Đạm Phú Mỹ loại 1 (bao)", "kg", "5%"),
        new("HH_09", "Phân NPK 20-5-5+TE Phú Mỹ(bao 25kg)", "kg", "5%"),
        new("HH_10", "Thuốc trừ sâu Regent 800WG", "Gói", "10%"),
        .. scenario.AdditionalItems
    ];

    public IReadOnlyList<string> Units { get; } = ["Bao", "Cái", "Chai", "Cốc", "Gói", "kg", "Tấn", "Thùng"];
    public IReadOnlyList<string> Stocks { get; } = ["KHH", "KVT"];
    public IReadOnlyList<string> TaxRates { get; } = ["0%", "5%", "8%", "10%", "KCT", "KKKNT"];
    public bool IncompleteItemLookup => scenario.IncompleteItemLookup;
    public string? CreatedItemNameOverride => scenario.CreatedItemNameOverride;

    public List<SavedVoucher> Vouchers { get; } = [.. scenario.ExistingVouchers];
    private readonly List<CatalogItem> created = [];
    private int voucherSequence = 2320;

    public Customer? FindCustomer(string code) => Customers.FirstOrDefault(customer => string.Equals(customer.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
    public CatalogItem? FindItem(string code) => Items.FirstOrDefault(item => string.Equals(item.Code, code.Trim(), StringComparison.Ordinal));

    public string NextVoucherNumber() => "PT0" + (voucherSequence++).ToString(CultureInfo.InvariantCulture);

    public string NextInvoiceNumber()
        => (Vouchers.Select(voucher => int.TryParse(voucher.InvoiceNumber, out var number) ? number : 0).DefaultIfEmpty(999).Max() + 1)
            .ToString(CultureInfo.InvariantCulture);

    public string NextItemCode()
    {
        var next = Items.Select(item => item.Code.StartsWith("HH_", StringComparison.Ordinal)
                && int.TryParse(item.Code[3..], out var number) ? number : 0).DefaultIfEmpty(0).Max() + 1;
        return "HH_" + next.ToString("00", CultureInfo.InvariantCulture);
    }

    public bool ItemCodeTaken(string code) => FindItem(code) is not null || scenario.TakenItemCodes.Contains(code.Trim(), StringComparer.Ordinal);

    public bool InvoiceExists(string series, string number)
        => Vouchers.Any(voucher => voucher.Series == series.Trim() && voucher.InvoiceNumber == number.Trim());

    public void AddItem(CatalogItem item)
    {
        Items.Add(item);
        created.Add(item);
        Persist();
    }

    public void Save(SavedVoucher voucher)
    {
        Vouchers.Add(voucher);
        Persist();
    }

    private void Persist()
    {
        if (statePath is null) return;
        var state = new StateFile(Vouchers.Except(scenario.ExistingVouchers).ToArray(), created);
        var temp = statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Scenario.Json));
        File.Move(temp, statePath, overwrite: true);
    }
}
