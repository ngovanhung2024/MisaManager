using MisaManager.Core.Tests.TestData;
using MisaManager.Import;

namespace MisaManager.Core.Tests;

public sealed class ItemGroupTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ItemGroupStore Store() => new(temp.Combine("item-groups.json"));

    private static readonly ItemGroupEntry[] MisaGroups =
        [new("CCDC", "Công cụ dụng cụ"), new("DV", "Dịch vụ"), new("HH", "Hàng hóa"), new("NVL", "Nguyên vật liệu"), new("TP", "Thành phẩm")];

    [Theory]
    [InlineData("Phân NPK 16-16-8+13S HCQN", "kg", "HH")]
    [InlineData("Phân kali", "kg", "HH")]
    [InlineData("Jinggangmeisu 5SL 450ml (ĐN)", "Chai", "HH")]
    [InlineData("Thuốc trừ sâu Regent 800WG", "Gói", "HH")]
    [InlineData("Phí vận chuyển", "Chuyến", "DV")]
    [InlineData("Chi phí mua hàng", "", "DV")]
    [InlineData("Lệ phí xăng dầu", "", "DV")]
    [InlineData("LE PHI XANG DAU", "", "DV")]
    [InlineData("Dịch vụ phun thuốc", "", "DV")]
    [InlineData("Cước vận chuyển phân bón", "Tấn", "DV")]
    [InlineData("Phun thuốc", "Lần", "DV")]
    [InlineData("Thuê kho", "Tháng", "DV")]
    [InlineData("Nguyên liệu Urê", "kg", "NVL")]
    [InlineData("Nguyên vật liệu bao bì", "Cái", "NVL")]
    public void NameAndUnitDecideTheGroup(string name, string unit, string expected)
        => Assert.Equal(expected, ItemGroupStore.Classify(name, unit));

    [Fact]
    public void PrefixFollowsOnlyLoadedGroups()
    {
        var store = Store();
        // No group list: the legacy HH_ series.
        Assert.Equal("HH_", store.CodePrefix("Phí vận chuyển", "Lần"));

        store.Replace([new("HH", "Hàng hóa")], "Nhom.xlsx");
        Assert.Equal("HH_", store.CodePrefix("Phí vận chuyển", "Lần"));

        store.Replace(MisaGroups, "Nhom.xlsx");
        Assert.Equal("DV_", store.CodePrefix("Phí vận chuyển", "Lần"));
        Assert.Equal("NVL_", store.CodePrefix("Nguyên liệu Urê", "kg"));
        Assert.Equal("HH_", store.CodePrefix("Phân kali", "kg"));
    }

    [Fact]
    public void GroupListSurvivesRestartAndClear()
    {
        Store().Replace([.. MisaGroups, new(" DV ", "trùng")], "Nhom.xlsx");

        var reopened = Store();
        Assert.Equal(5, reopened.Count);
        Assert.Equal("Nhom.xlsx", reopened.Source);
        Assert.Equal("NVL", reopened.GroupOfCode("NVL_01"));
        Assert.Equal("HH", reopened.GroupOfCode("HH_12"));
        Assert.Null(reopened.GroupOfCode("HH _CAI"));
        Assert.Null(reopened.GroupOfCode("CM GOLD 20.10.10"));

        reopened.Clear();
        Assert.Equal(0, Store().Count);
    }

    [Fact]
    public void NewCodesAreNumberedPerGroup()
    {
        var catalog = new ItemCatalogStore(temp.Combine("item-catalog.json"));
        catalog.Merge([new("HH_336", "a", "kg"), new("HH_01", "b", "kg"), new("DV_02", "c", "")], temp.Combine("catalog.xlsx"));

        Assert.Equal("HH_337", catalog.NextCode("HH_", proposed: "HH_337"));
        // MISA proposes its own HH number; it must not leak into another group.
        Assert.Equal("DV_03", catalog.NextCode("DV_", proposed: "HH_337"));
        Assert.Equal("NVL_01", catalog.NextCode("NVL_", proposed: "HH_337"));
    }

    [Fact]
    public void AuditReportsAmbiguousNamesAndCodesOutsideTheGroups()
    {
        var groups = Store();
        ItemCatalogEntry[] items =
        [
            new("HH_03", "Phân kali", "kg"), new("HH_50", "Phân kali", "kg"),
            new("HH_133", "Bamy 75WP 25g", "Chai"), new("HH_135", "Bamy 75WP 25g", "Gói"),
            new("HH _CAI", "HANG HOA CAI", "Cái"), new("CM GOLD 20.10.10", "Phân NPK Cà Mau Gold 20-10-10-25kg", "Tấn")
        ];

        var withoutGroups = CatalogAudit.Items(items, groups);
        Assert.Equal(2, withoutGroups.Count);
        Assert.Contains(withoutGroups, finding => finding.Contains("'HH _CAI'", StringComparison.Ordinal));
        var ambiguous = Assert.Single(withoutGroups, finding => finding.Contains("ITEM_AMBIGUOUS", StringComparison.Ordinal));
        Assert.Contains("HH_03, HH_50", ambiguous, StringComparison.Ordinal);
        Assert.DoesNotContain("HH_133", ambiguous, StringComparison.Ordinal);

        groups.Replace(MisaGroups, "Nhom.xlsx");
        var outside = Assert.Single(CatalogAudit.Items(items, groups), finding => finding.Contains("không bắt đầu", StringComparison.Ordinal));
        Assert.StartsWith("2 mã", outside, StringComparison.Ordinal);
        Assert.Contains("'CM GOLD 20.10.10'", outside, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditTellsPunctuationOnlyDuplicatesApartFromRealDuplicates()
    {
        ItemCatalogEntry[] items =
        [
            new("HH_120", "FUJI ONE 40EC 100CC PET", "Chai"), new("HH_203", "FUJI ONE 40EC-100CC PET", "Chai"),
            new("HH_03", "Phân kali", "kg"), new("HH_50", "Phân kali", "kg")
        ];

        var findings = CatalogAudit.Items(items, Store());

        var spelling = Assert.Single(findings, finding => finding.Contains("HH_120", StringComparison.Ordinal));
        Assert.StartsWith("Tên gần trùng", spelling, StringComparison.Ordinal);
        Assert.Contains("HH_203 'FUJI ONE 40EC-100CC PET'", spelling, StringComparison.Ordinal);
        var duplicate = Assert.Single(findings, finding => finding.Contains("HH_03", StringComparison.Ordinal));
        Assert.Contains("cột 'Mã hàng' của file Excel hóa đơn", duplicate, StringComparison.Ordinal);
    }

    /// <summary>Layout of MISA's "Danh sách nhóm vật tư, hàng hóa, dịch vụ" export (the attached Nhom_vat_tu__hang_hoa__dich_vu.xlsx).</summary>
    [Fact]
    public async Task GroupExportIsReadWithoutFooterAndStoppedGroups()
    {
        var book = new WorkbookBuilder();
        var sheet = book.Sheet("Nhom_vat_tu__hang_hoa__dich_vu");
        sheet.Row(1, "DANH SÁCH NHÓM VẬT TƯ, HÀNG HÓA, DỊCH VỤ").Merge("A1:C1");
        sheet.Row(2, "Mã nhóm vật tư, hàng hóa, dịch vụ", "Tên nhóm vật tư, hàng hóa, dịch vụ", "Ngừng theo dõi");
        sheet.Row(3, "DV", "Dịch vụ", false);
        sheet.Row(4, "HH", "Hàng hóa", false);
        sheet.Row(5, "OTO", "Xe ô tô", true);
        sheet.Row(6, "Số dòng = 3");

        var groups = await ItemGroupWorkbook.ReadAsync(new WorkbookReader(), book.Save(temp.Combine("Nhom.xlsx")), Token);

        Assert.Equal([new ItemGroupEntry("DV", "Dịch vụ"), new ItemGroupEntry("HH", "Hàng hóa")], groups);
    }

    [Fact]
    public async Task WorkbookWithoutGroupCodeColumnIsRejected()
    {
        var book = new WorkbookBuilder();
        book.Sheet("S").Row(1, "Mã", "Tên").Row(2, "HH_01", "x");

        var error = await Assert.ThrowsAsync<WorkbookImportException>(() =>
            ItemGroupWorkbook.ReadAsync(new WorkbookReader(), book.Save(temp.Combine("sai.xlsx")), Token));
        Assert.Equal("ITEM_GROUP_HEADER_MISSING", error.Code);
    }
}
