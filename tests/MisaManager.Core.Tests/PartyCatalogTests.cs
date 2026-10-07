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

    /// <summary>The posted vouchers enter every "Khách hàng không cung cấp thông tin" invoice under MISA's "Khách lẻ".</summary>
    [Fact]
    public void AnonymousBuyerUsesTheRetailCustomerOfTheCatalog()
    {
        var store = Store();
        Party Buyer(string name, string code = "") => new(name, "", "", code, ValuePresence.NotApplicable);

        Assert.Null(BuyerCodes.Resolve(Buyer("Khách hàng không cung cấp thông tin"), null));
        store.Replace(PartyRole.Customer, [.. Customers, new("KL", "Khách lẻ"), new("NTD", "BÁN CHO NGƯỜI TIÊU DÙNG")], "Khach_hang.xlsx");

        Assert.Equal(new BuyerCode("KL", "người mua không cung cấp thông tin → Khách lẻ", "Khách lẻ"),
            BuyerCodes.Resolve(Buyer("Khách hàng không cung cấp thông tin"), store));
        Assert.Equal("KL", BuyerCodes.Resolve(Buyer(" KHACH HANG KHONG CUNG CAP THONG TIN "), store)?.Code);
        Assert.Equal("KL", BuyerCodes.Resolve(Buyer("Người mua không cung cấp thông tin."), store)?.Code);
        Assert.Equal("X01", BuyerCodes.Resolve(Buyer("Khách hàng không cung cấp thông tin", "X01"), store)?.Code);
        Assert.False(BuyerCodes.IsAnonymous("Khách hàng"));
        Assert.False(BuyerCodes.IsAnonymous("BÁN CHO NGƯỜI TIÊU DÙNG"));
    }

    [Theory]
    [InlineData("CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN LÂM NGHIỆP A&V", true)]
    [InlineData("CTY. TNHH ABC", true)]
    [InlineData("Hợp tác xã nông nghiệp Vinh Hà", true)]
    [InlineData("HTX Phú Lộc", true)]
    [InlineData("Chi nhánh Huế - Ngân hàng X", true)]
    [InlineData("Hoàng Trọng Tiếp", false)]
    [InlineData("Trương Văn Công", false)]
    [InlineData("", false)]
    public void OrganizationNamesAreRecognisedByWholeWords(string name, bool expected)
        => Assert.Equal(expected, BuyerCodes.LooksLikeOrganization(name));

    /// <summary>The real export stores "Ngừng theo dõi" as boolean cells, not "0"/"1" text.</summary>
    [Fact]
    public async Task BooleanStoppedColumnDropsOnlyStoppedRows()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Khach_hang");
        sheet.Row(1, "DANH SÁCH KHÁCH HÀNG").Merge("A1:G1");
        sheet.Row(2, "Mã khách hàng", "Tên khách hàng", "Địa chỉ", "Nhóm KH, NCC", "Mã số thuế", "Điện thoại", "Ngừng theo dõi");
        sheet.Row(3, "KL", "Khách lẻ", null, null, null, null, false);
        sheet.Row(4, "CU", "KHÁCH CŨ", null, null, null, null, true);
        sheet.Row(5, "NTD", "BÁN CHO NGƯỜI TIÊU DÙNG", null, null, "", null, false);
        sheet.Row(6, "Số dòng = 3");

        var entries = await PartyCatalogWorkbook.ReadAsync(new WorkbookReader(), book.Save(temp.Combine("Khach_hang.xlsx")), PartyRole.Customer, Token);

        Assert.Equal(["KL", "NTD"], entries.Select(entry => entry.Code));
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

    [Fact]
    public void NewCustomerCodeIsTheDistinctiveNameForOrganisationsAndInitialsForPeople()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, [.. Customers, new("LTH", "LÊ THANH HẢI")], "Khach_hang.xlsx");

        // Like the accountants' list (Khach_hang.xlsx: HƯNG LỘC, LINH NGỌC, BẠCH MÃ, A&V): never the MST.
        Assert.Equal("PHÚ LỘC", store.NewCustomerCode(new Party("TRUNG TÂM DỊCH VỤ SỰ NGHIỆP CÔNG XÃ PHÚ LỘC", " 3301747760 ", "", "",
            ValuePresence.Provided)));
        Assert.Equal("THỪA THIÊN HUẾ", store.NewCustomerCode(new Party("CHI NHÁNH THỪA THIÊN HUẾ", "0100101611-008", "", "",
            ValuePresence.Provided, PartyKind.Organization)));
        Assert.Equal("LINH NGỌC", store.NewCustomerCode(new Party("CÔNG TY TNHH MỘT THÀNH VIÊN LINH NGỌC", "3301247101", "", "",
            ValuePresence.Provided)));
        Assert.Equal("BẠCH MÃ", store.NewCustomerCode(new Party("VƯỜN QUỐC GIA BẠCH MÃ", "3300305784", "", "", ValuePresence.Provided)));
        Assert.Equal("A&V", store.NewCustomerCode(new Party("CÔNG TY CỔ PHẦN ĐẦU TƯ VÀ PHÁT TRIỂN LÂM NGHIỆP A&V", "0107286506", "", "",
            ValuePresence.Provided)));
        Assert.Equal("LỘC TIẾN", store.NewCustomerCode(new Party("Trường Mầm non Lộc Tiến", "3301493428", "", "", ValuePresence.Provided,
            PartyKind.Organization)));
        Assert.Equal("NAM HÒA", store.NewCustomerCode(new Party("CÔNG TY TNHH NHÀ NƯỚC MỘT THÀNH VIÊN LÂM NGHIỆP NAM HÒA", "3300100096",
            "", "", ValuePresence.Provided)));
        // A long remainder becomes initials; a used multi-word code gets " 2"; nothing distinctive left falls back to initials.
        Assert.Equal("CTGVPTLS", store.NewCustomerCode(new Party("VIỆN CẢI THIỆN GIỐNG VÀ PHÁT TRIỂN LÂM SẢN", "0101483843", "", "",
            ValuePresence.Provided, PartyKind.Organization)));
        store.AddCreated(new("PHÚ LỘC", "TRUNG TÂM DỊCH VỤ SỰ NGHIỆP CÔNG XÃ PHÚ LỘC", "3301747760", ""));
        Assert.Equal("PHÚ LỘC 2", store.NewCustomerCode(new Party("KHO BẠC NHÀ NƯỚC PHÚ LỘC", "3300498416-003", "", "", ValuePresence.Provided,
            PartyKind.Organization)));
        Assert.Equal("CTCPNN", store.NewCustomerCode(new Party("CÔNG TY CỔ PHẦN NÔNG NGHIỆP", "3300000000", "", "", ValuePresence.Provided)));
        Assert.Equal("HTT", store.NewCustomerCode(new Party("Hoàng Trọng Tiếp", "", "", "", ValuePresence.Missing)));
        // Đ → D, text in brackets dropped, a used code gets a number.
        Assert.Equal("DVA", store.NewCustomerCode(new Party("Đỗ Văn An (046085016878)", "046085016878", "", "", ValuePresence.Provided)));
        Assert.Equal("LTH2", store.NewCustomerCode(new Party("Lê Thị Hương", "046172000663", "", "", ValuePresence.Provided)));
        // An organisation without a usable MST falls back to initials; HTNY is taken.
        Assert.Equal("HTNY2", store.NewCustomerCode(new Party("Huỳnh Thị Như Ý", "", "", "", ValuePresence.Missing)));
    }

    [Fact]
    public void CreatedCustomerIsFoundUntilAFreshExportListsIt()
    {
        var store = Store();
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");
        var buyer = new Party("TRUNG TÂM DỊCH VỤ SỰ NGHIỆP CÔNG XÃ PHÚ LỘC", "3301747760", "Xã Phú Lộc", "", ValuePresence.Provided);
        Assert.Null(BuyerCodes.Resolve(buyer, store));
        Assert.True(BuyerCodes.CanCreate(buyer, store));

        store.AddCreated(new("3301747760", buyer.Name, buyer.TaxCode, buyer.Address));

        Assert.Equal("3301747760", BuyerCodes.Resolve(buyer, store)?.Code);
        Assert.Equal("Xã Phú Lộc", store.FindByCode("3301747760")?.Address);
        // Survives a restart.
        var reloaded = Store();
        Assert.Equal("3301747760", BuyerCodes.Resolve(buyer, reloaded)?.Code);
        Assert.Equal(Customers.Length, reloaded.CustomerCount);
        Assert.Equal(1, reloaded.CreatedCount);

        reloaded.Replace(PartyRole.Customer, [.. Customers, new("3301747760", buyer.Name, buyer.TaxCode)], "Khach_hang.xlsx");
        Assert.Equal(0, reloaded.CreatedCount);
        Assert.Equal("3301747760", BuyerCodes.Resolve(buyer, reloaded)?.Code);
    }

    [Fact]
    public void CustomersAreCreatedOnlyAfterTheCustomerListWasSearched()
    {
        var empty = Store();
        var person = new Party("Hoàng Trọng Tiếp", "", "", "", ValuePresence.Missing);
        Assert.False(BuyerCodes.CanCreate(person, empty));
        Assert.False(BuyerCodes.CanCreate(person, null));

        var store = Store("loaded.json");
        store.Replace(PartyRole.Customer, Customers, "Khach_hang.xlsx");
        Assert.True(BuyerCodes.CanCreate(person, store));
        Assert.False(BuyerCodes.CanCreate(person with { Code = "TIẾP" }, store));
        Assert.False(BuyerCodes.CanCreate(new Party("BÁN CHO NGƯỜI TIÊU DÙNG", "", "", "", ValuePresence.Missing), store));
        Assert.False(BuyerCodes.CanCreate(new Party("Khách hàng không cung cấp thông tin", "", "", "", ValuePresence.Missing), store));
    }

    [Theory]
    [InlineData("046172000663", true)]
    [InlineData("3301747760", false)]
    [InlineData("0100101611-008", false)]
    [InlineData("", false)]
    public void TwelveDigitBuyerTaxCodeIsAPersonalId(string taxCode, bool personal)
        => Assert.Equal(personal, BuyerCodes.IsPersonalId(taxCode));
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

    [Theory]
    [InlineData("Lê Thị Hương", PartyKind.Unspecified, true)]
    [InlineData("TRUNG TÂM DỊCH VỤ SỰ NGHIỆP CÔNG XÃ PHÚ LỘC", PartyKind.Unspecified, false)]
    [InlineData("Nguyễn Văn A", PartyKind.Organization, false)]
    public void IndividualBuyerIsAPersonNotAnOrganization(string name, PartyKind kind, bool individual)
        => Assert.Equal(individual, BuyerCodes.IsIndividual(new Party(name, "046172000663", "", "", ValuePresence.Provided, kind)));
}
