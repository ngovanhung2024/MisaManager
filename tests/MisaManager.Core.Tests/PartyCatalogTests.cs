using MisaManager.Core.Tests.TestData;
using MisaManager.Import;

namespace MisaManager.Core.Tests;

public sealed class PartyCatalogTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PartyCatalogStore Store(string name = "party-catalog.json") => new(temp.Combine(name));

    private static readonly PartyCatalogEntry[] Customers =
    [
        new("DMH", "CÔNG TY CỔ PHẦN DỆT MAY HUẾ", "3300100628"),
        new("HTNY", "Huỳnh Thị Như Ý (046166001495)"),
        new("UBND XÃ LỘC HOÀ", "UBND XÃ LỘC HOÀ", "3300309796"),
        new("KBNN PHÚ LỘC", "KHO BẠC NHÀ NƯỚC PHÚ LỘC", "3300498416-003")
    ];

    private static readonly PartyCatalogEntry[] Suppliers =
    [
        new("AMC", "CÔNG TY CỔ PHẦN HÓA NÔNG AMC", "0108364972"),
        new("DMH-NCC", "CÔNG TY CỔ PHẦN DỆT MAY HUẾ", "3300100628")
    ];

    [Theory]
    [InlineData("CÔNG TY CỔ PHẦN DỆT MAY HUẾ")]
    [InlineData("công ty cổ phần dệt may huế")]
    [InlineData("  CÔNG TY  CỔ PHẦN\u00A0DỆT MAY HUẾ ")]
    [InlineData("CÔNG TY CỔ PHẦN DE\u0323\u0302T MAY HUÊ\u0301")]
    [InlineData("CÔNG\u200B TY CỔ PHẦN DỆT MAY HUẾ")]
    public void ExactNameMatchesAfterCharacterNormalisation(string invoiceName)
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");

        var match = store.FindCode(invoiceName);

        Assert.NotNull(match);
        Assert.Equal(("DMH", PartyRole.Customer, "trùng tên"), (match.Code, match.Role, match.Method));
        Assert.Equal("CÔNG TY CỔ PHẦN DỆT MAY HUẾ", match.Name);
    }

    [Fact]
    public void ClearForgetsCustomersAndSuppliers()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");
        store.Replace(PartyRole.Supplier, Suppliers, "Nha_cung_cap.xlsx");

        store.Clear();

        var reloaded = Store();
        Assert.Equal((0, 0, null, null), (reloaded.CustomerCount, reloaded.SupplierCount, reloaded.CustomerSource, reloaded.SupplierSource));
        Assert.Null(reloaded.FindCode("CÔNG TY CỔ PHẦN DỆT MAY HUẾ"));
    }

    [Fact]
    public void EthLookAlikeIsReadAsVietnameseD()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, [new("A&V", "CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V", "0107286506")], "Khach_hang.xlsx");

        Assert.Equal("A&V", store.FindCode("CÔNG TY CỔ PHẦN \u00D0ẦU TƯ VÀ PHÁT TRIỂN A&V")?.Code);
    }

    [Fact]
    public void OldAndNewTonePlacementCompareEqual()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");

        Assert.Equal("UBND XÃ LỘC HOÀ", store.FindCode("UBND XÃ LỘC HÒA")?.Code);
        Assert.Equal("UBND XÃ LỘC HOÀ", store.FindCode("ỦY BAN NHÂN DÂN XÃ LỘC HÒA")?.Code);
    }

    [Fact]
    public void TaxCodeIdentifiesARenamedCustomer()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");

        var match = store.FindCode("KBNN HUYỆN PHÚ LỘC", "3300498416 - 003");

        Assert.Equal(("KBNN PHÚ LỘC", "trùng mã số thuế"), (match?.Code, match?.Method));
    }

    [Fact]
    public void FindByCodeReturnsTheListedTaxCode()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, [new("NAMHOA", "CÔNG TY TNHH NHÀ NƯỚC MỘT THÀNH VIÊN LÂM NGHIỆP NAM HÒA", "3300100096")], "Khach_hang.xlsx");
        store.Replace(PartyRole.Supplier, [new("AMC", "CÔNG TY CỔ PHẦN HÓA NÔNG AMC", "0108364972")], "Nha_cung_cap.xlsx");

        Assert.Equal("3300100096", store.FindByCode(" namhoa ")?.TaxCode);
        Assert.Equal("0108364972", store.FindByCode("AMC")?.TaxCode);
        Assert.Null(store.FindByCode("KHONGCO"));
        Assert.Null(store.FindByCode(null));
    }

    [Theory]
    [InlineData("CTY CP DỆT MAY HUẾ")]
    [InlineData("Công ty CP Dệt-May Huế.")]
    public void AbbreviatedLegalFormsMatchTheRelaxedName(string invoiceName)
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");

        var match = store.FindCode(invoiceName);

        Assert.Equal(("DMH", "tên gần đúng"), (match?.Code, match?.Method));
    }

    [Fact]
    public void IdentityNumberInBracketsIsIgnoredOnlyInTheRelaxedPass()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");

        Assert.Equal("HTNY", store.FindCode("Huỳnh Thị Như Ý")?.Code);
    }

    [Fact]
    public void CustomersWinOverSuppliersAndSuppliersAreTheFallback()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");
        store.Replace(PartyRole.Supplier, Suppliers, "Nha_cung_cap.xlsx");

        Assert.Equal(("DMH", PartyRole.Customer), (store.FindCode("CÔNG TY CỔ PHẦN DỆT MAY HUẾ")?.Code, store.FindCode("CÔNG TY CỔ PHẦN DỆT MAY HUẾ")?.Role));
        var supplier = store.FindCode("Công ty Cổ phần Hóa nông AMC");
        Assert.Equal(("AMC", PartyRole.Supplier), (supplier?.Code, supplier?.Role));
    }

    [Fact]
    public void UnknownNameReturnsNull()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");

        Assert.Null(store.FindCode("CÔNG TY TNHH KHÔNG CÓ TRONG DANH MỤC", "0000000000"));
    }

    [Fact]
    public void SameNameWithSeveralCodesThrowsUnlessTheTaxCodeDecides()
    {
        var store = Store();
        store.Replace(PartyRole.Customer,
            [new("NVA1", "NGUYỄN VĂN AN", "111"), new("NVA2", "Nguyễn Văn An", "222")], "Khach_hang.xlsx");

        var error = Assert.Throws<AutomationException>(() => store.FindCode("NGUYỄN VĂN AN"));
        Assert.Equal("CUSTOMER_AMBIGUOUS", error.Code);
        Assert.Contains("NVA1", error.Message);
        Assert.Equal("NVA2", store.FindCode("NGUYỄN VĂN AN", "222")?.Code);
    }

    [Fact]
    public void ReplaceKeepsTheOtherListAndPersists()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");
        store.Replace(PartyRole.Supplier, Suppliers, "Nha_cung_cap.xlsx");
        store.Replace(PartyRole.Customer, [new(" DMH ", " CÔNG TY CỔ PHẦN DỆT MAY HUẾ "), new("", "Không mã")], "Khach_hang_2.xlsx");

        var reloaded = Store();

        Assert.Equal((1, 2), (reloaded.CustomerCount, reloaded.SupplierCount));
        Assert.Equal(("Khach_hang_2.xlsx", "Nha_cung_cap.xlsx"), (reloaded.CustomerSource, reloaded.SupplierSource));
        Assert.Equal("DMH", reloaded.FindCode("CÔNG TY CỔ PHẦN DỆT MAY HUẾ")?.Code);
    }

    [Fact]
    public void ExcelCodeWinsConsumersUseNtdAndOthersComeFromTheCatalog()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");
        Party Buyer(string name, string code = "", string tax = "") => new(name, tax, "", code, ValuePresence.Provided);

        Assert.Equal(new BuyerCode("X01", "Excel"), BuyerCodes.Resolve(Buyer("CÔNG TY CỔ PHẦN DỆT MAY HUẾ", " X01 "), store));
        Assert.Equal("NTD", BuyerCodes.Resolve(Buyer("Bán cho người tiêu dùng"), store)?.Code);
        Assert.Equal("NTD", BuyerCodes.Resolve(Buyer("BÁN CHO NGƯỜI TIÊU DÙNG"), null)?.Code);
        var catalog = BuyerCodes.Resolve(Buyer("CTY CP DỆT MAY HUẾ"), store);
        Assert.Equal(("DMH", "CÔNG TY CỔ PHẦN DỆT MAY HUẾ"), (catalog?.Code, catalog?.CatalogName));
        Assert.Null(BuyerCodes.Resolve(Buyer("CÔNG TY KHÁC"), store));
        Assert.Null(BuyerCodes.Resolve(Buyer("CÔNG TY CỔ PHẦN DỆT MAY HUẾ"), null));
    }

    /// <summary>Layout of MISA's "Danh sách khách hàng" export (the attached Khach_hang.xlsx).</summary>
    [Fact]
    public async Task CustomerExportIsReadWithoutFooterOrStoppedRows()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Khach_hang");
        sheet.Row(1, "DANH SÁCH KHÁCH HÀNG").Merge("A1:G1");
        sheet.Row(2, "Mã khách hàng", "Tên khách hàng", "Địa chỉ", "Nhóm KH, NCC", "Mã số thuế", "Điện thoại", "Ngừng theo dõi");
        sheet.Row(3, "A&V", "CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V", "Số 19B, ngõ 42", null, "0107286506", null, "0");
        sheet.Row(4, "HTNY", "Huỳnh Thị Như Ý (046166001495)", null, null, null, null, "0");
        sheet.Row(5, "CU", "KHÁCH CŨ", null, null, null, null, "1");
        sheet.Row(6, "Số dòng = 3");

        var entries = await PartyCatalogWorkbook.ReadAsync(new WorkbookReader(), book.Save(temp.Combine("Khach_hang.xlsx")), PartyRole.Customer, Token);

        Assert.Equal(["A&V", "HTNY"], entries.Select(entry => entry.Code));
        Assert.Equal(("CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN A&V", "0107286506", "Số 19B, ngõ 42"), (entries[0].Name, entries[0].TaxCode, entries[0].Address));
    }

    /// <summary>Layout of MISA's "Danh sách nhà cung cấp" export (the attached Nha_cung_cap.xlsx): codes may have stray spaces.</summary>
    [Fact]
    public async Task SupplierExportIsRead()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Nha_cung_cap");
        sheet.Row(1, "DANH SÁCH NHÀ CUNG CẤP").Merge("A1:G1");
        sheet.Row(2, "Mã nhà cung cấp", "Tên nhà cung cấp", "Địa chỉ", "Nhóm KH, NCC", "Mã số thuế", "Điện thoại", "Ngừng theo dõi");
        sheet.Row(3, " KHAC 01", "KHÁC ", null, null, null, null, "0");
        sheet.Row(4, "AMC", "CÔNG TY CỔ PHẦN HÓA NÔNG AMC", "Tầng 11 Tòa nhà Zen Tower", null, "0108364972", null, "0");
        sheet.Row(5, "Số dòng = 2");

        var entries = await PartyCatalogWorkbook.ReadAsync(new WorkbookReader(), book.Save(temp.Combine("Nha_cung_cap.xlsx")), PartyRole.Supplier, Token);

        Assert.Equal(["KHAC 01", "AMC"], entries.Select(entry => entry.Code));
        Assert.Equal("KHÁC", entries[0].Name);
    }

    [Fact]
    public async Task WorkbookWithoutCodeAndNameColumnsIsRejected()
    {
        var book = new WorkbookBuilder();
        book.Sheet("S").Row(1, "Cột A", "Cột B").Row(2, "x", "y");

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() =>
            PartyCatalogWorkbook.ReadAsync(new WorkbookReader(), book.Save(temp.Combine("sai.xlsx")), PartyRole.Customer, Token));
        Assert.Equal("PARTY_CATALOG_HEADER_MISSING", error.Code);
    }
}

public sealed class CompanyRedactionTests
{
    [Fact]
    public void CompanyNamesAndTheWorkingOnLabelAreRemoved()
    {
        const string text = """
            Cửa sổ: MISA SME 2023 R20 Enterprise - CÔNG TY CỔ PHẦN TƯ VẤN DỊCH VỤ TÂM MINH PHÁT
            "name": "Đang làm việc trên: CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT"
            Khách hàng: CÔNG TY CỔ PHẦN DỆT MAY HUẾ
            """;

        var redacted = CompanyRedaction.Redact(text, ["CÔNG TY CỔ PHẦN TƯ VẤN DỊCH VỤ TÂM MINH PHÁT", "CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT", ""]);

        Assert.DoesNotContain("TÂM MINH PHÁT", redacted);
        Assert.DoesNotContain("LÁ VIỆT", redacted);
        Assert.Contains("MISA SME 2023 R20 Enterprise - " + CompanyRedaction.Placeholder, redacted);
        Assert.Contains("Đang làm việc trên: " + CompanyRedaction.Placeholder + "\"", redacted);
        // Invoice data (buyers) is kept.
        Assert.Contains("CÔNG TY CỔ PHẦN DỆT MAY HUẾ", redacted);
    }
}
