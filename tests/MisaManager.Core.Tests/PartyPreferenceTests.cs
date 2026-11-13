using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

/// <summary>
/// Report 0.2.186, HĐ 392 (Lộc Thủy): MST 3300482529 is carried by two supplier codes (THANH LƯƠNG, TL) and the
/// purchase stopped with SUPPLIER_AMBIGUOUS. The user picks the code once; the choice survives a reload of the list.
/// </summary>
public sealed class PartyPreferenceTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private const string ThanhLuongTax = "3300482529";
    private static readonly PartyCatalogEntry[] Suppliers =
    [
        new("THANH LƯƠNG", "CÔNG TY CỔ PHẦN XĂNG DẦU THANH LƯƠNG", ThanhLuongTax),
        new("TL", "Công ty CP xăng dầu Thanh Lương", ThanhLuongTax),
        new("NCC_PBH", "CÔNG TY CỔ PHẦN PHÂN BÓN HUẾ", "3300555111")
    ];

    private PartyCatalogStore Store()
    {
        var store = new PartyCatalogStore(temp.Combine("party-catalog.json"));
        store.Replace(PartyRole.Supplier, Suppliers, "Nha_cung_cap.xlsx");
        return store;
    }

    [Fact]
    public void SharedMstStopsUntilTheUserChoosesTheCode()
    {
        var store = Store();

        var error = Assert.Throws<AutomationException>(() => store.FindSupplier(ThanhLuongTax, "392"));
        Assert.Equal("SUPPLIER_AMBIGUOUS", error.Code);
        Assert.Contains("THANH LƯƠNG, TL", error.Message);
        Assert.Contains("Chọn mã khách hàng/nhà cung cấp trùng MST", error.Message);
        Assert.Equal("NCC_PBH", store.FindSupplier("3300555111", "1")?.Code);
        var group = Assert.Single(store.SharedTaxGroups());
        Assert.Equal((PartyRole.Supplier, ThanhLuongTax), (group.Role, group.TaxCode));
        Assert.Equal(["THANH LƯƠNG", "TL"], group.Entries.Select(entry => entry.Code));

        store.SetPreferred([new PartyPreference(PartyRole.Supplier, ThanhLuongTax, "TL")]);

        Assert.Equal("TL", store.FindSupplier(ThanhLuongTax, "392")?.Code);
        Assert.Equal("TL", new PartyCatalogStore(store.FilePath).FindSupplier(" 3300482529 ", "392")?.Code);
    }

    [Fact]
    public void ChoiceSurvivesReloadButNotACodeThatLeftTheList()
    {
        var store = Store();
        store.SetPreferred([new PartyPreference(PartyRole.Supplier, ThanhLuongTax, "TL")]);

        store.Replace(PartyRole.Supplier, Suppliers.Reverse(), "Nha_cung_cap_2.xlsx");
        Assert.Equal("TL", store.FindSupplier(ThanhLuongTax, "392")?.Code);

        // TL merged away in MISA: the remaining code is the only one, the stale choice is ignored.
        store.Replace(PartyRole.Supplier, Suppliers.Where(entry => entry.Code != "TL"), "Nha_cung_cap_3.xlsx");
        Assert.Equal("THANH LƯƠNG", store.FindSupplier(ThanhLuongTax, "392")?.Code);

        // A new duplicate that is not the chosen code needs a new choice.
        store.Replace(PartyRole.Supplier, [.. Suppliers.Where(entry => entry.Code != "TL"),
            new PartyCatalogEntry("TL2", "THANH LƯƠNG 2", ThanhLuongTax)], "Nha_cung_cap_4.xlsx");
        Assert.Equal("SUPPLIER_AMBIGUOUS", Assert.Throws<AutomationException>(() => store.FindSupplier(ThanhLuongTax, "392")).Code);
    }

    [Fact]
    public void CustomerSharingTheBuyersMstUsesTheChosenCode()
    {
        var store = new PartyCatalogStore(temp.Combine("party-catalog.json"));
        store.Replace(PartyRole.Customer,
            [new("LV", "CÔNG TY TNHH LÁ VIỆT", "3301234567"), new("LV-HUE", "CÔNG TY TNHH LÁ VIỆT", "3301234567")], "Khach_hang.xlsx");

        var error = Assert.Throws<AutomationException>(() => store.FindCode("CÔNG TY TNHH LÁ VIỆT", "3301234567"));
        Assert.Equal("CUSTOMER_AMBIGUOUS", error.Code);
        Assert.Contains("trùng MST", error.Message);

        store.SetPreferred([new PartyPreference(PartyRole.Customer, "3301234567", "LV-HUE")]);

        Assert.Equal("LV-HUE", store.FindCode("CÔNG TY TNHH LÁ VIỆT", "3301234567")?.Code);
        // The supplier choice of the same MST is separate.
        Assert.Null(store.PreferredSnapshot().SingleOrDefault(choice => choice.Role == PartyRole.Supplier));
        // Without the MST the name alone still cannot decide.
        Assert.Throws<AutomationException>(() => store.FindCode("CÔNG TY TNHH LÁ VIỆT"));
    }

    [Fact]
    public void OlderCatalogFileWithoutChoicesStillLoads()
    {
        var path = temp.Combine("old.json");
        File.WriteAllText(path, """
            {"schemaVersion":1,"updatedAt":"2026-11-01T00:00:00+00:00","customerSource":null,"supplierSource":"a.xlsx",
             "customers":[],"suppliers":[{"code":"TL","name":"TL","taxCode":"3300482529","address":""}]}
            """);

        var store = new PartyCatalogStore(path);

        Assert.Empty(store.PreferredSnapshot());
        Assert.Equal("TL", store.FindSupplier(ThanhLuongTax, "392")?.Code);
    }
}
