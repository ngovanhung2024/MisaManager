using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class ItemCatalogTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private string CatalogPath => temp.Combine("state", "item-catalog.json");

    private ItemCatalogStore NewStore(params ItemCatalogEntry[] entries)
    {
        var store = new ItemCatalogStore(CatalogPath);
        foreach (var entry in entries) store.Add(entry);
        return store;
    }

    private static ItemCatalogEntry Item(string code, string name, string unit, string conversion = "") => new(code, name, unit, conversion);

    // ---- registered units ----

    [Fact]
    public void ClearEmptiesTheWholeDatabase()
    {
        var export = temp.Combine("export.xlsx");
        File.WriteAllText(export, "");
        var store = NewStore();
        store.Merge([Item("HH_1", "Xi măng", "Tấn"), Item("HH_2", "Cát", "m3")], export);
        store.Add(Item("HH_3", "Đá", "m3"));

        Assert.Equal(3, store.Clear());

        var reloaded = new ItemCatalogStore(CatalogPath);
        Assert.Empty(reloaded.Snapshot());
        Assert.Equal(0, reloaded.PendingCount);
        Assert.Null(reloaded.SourceFile);
        Assert.True(reloaded.HasImportedProvenance);
    }

    [Fact]
    public void MainUnitOnlyReadCannotProveConversionUnitAbsent()
    {
        var store = NewStore(new("HH_111", "Phân NPK Cà Mau Gold", "Tấn", UnitListIncomplete: true),
            Item("HH_08", "Phân Ure", "kg", "Bao"));

        Assert.Null(store.UnitAbsenceProof("HH_111", "kg"));
        Assert.True(new ItemCatalogStore(CatalogPath).FindByCode("HH_111")!.UnitListIncomplete);
    }

    [Fact]
    public void CatalogProvesKgAbsentForAnItemRegisteredOnlyInTons()
    {
        var store = NewStore(Item("HH_111", "Phân NPK Cà Mau Gold", "Tấn"), Item("HH_08", "Phân Ure", "kg", "Bao"));

        var proof = store.UnitAbsenceProof("hh_111", "Kg");

        Assert.Equal("ItemCatalog:HH_111:[\"T\\u1EA5n\"]", proof);
    }

    [Theory]
    [InlineData("Tấn", "Bao, kg")]
    [InlineData("Tấn", "KG")]
    [InlineData("kg", "")]
    public void CatalogGivesNoProofWhenKgIsRegistered(string unit, string conversion)
    {
        var store = NewStore(Item("HH_111", "Phân NPK", unit, conversion), Item("HH_08", "Phân Ure", "kg", "Bao"));

        Assert.Null(store.UnitAbsenceProof("HH_111", "kg"));
    }

    [Fact]
    public void CatalogWithoutConversionColumnGivesNoProof()
        // No item lists a conversion unit: the export may simply lack that column.
        => Assert.Null(NewStore(Item("HH_111", "Phân NPK", "Tấn")).UnitAbsenceProof("HH_111", "kg"));

    [Fact]
    public void UnknownCodeGivesNoProof()
        => Assert.Null(NewStore(Item("HH_111", "Phân NPK", "Tấn"), Item("HH_08", "Phân Ure", "kg", "Bao")).UnitAbsenceProof("HH_999", "kg"));

    // ---- lookup by name and unit ----

    [Fact]
    public void ImportedPackagingNameWinsOverRetainedGeneratedMappingAfterRestart()
    {
        var store = NewStore(Item("HH_340", "Sông Gianh HC-15 bột (25kg)", "kg"));
        store.Merge([Item("HH_177", "Sông Gianh HC-15 bột (bao 25kg)", "Tấn", "kg")], temp.Combine("export.xlsx"));

        Assert.Equal("HH_177", new ItemCatalogStore(CatalogPath).Find("Sông Gianh HC-15 bột (25kg)", "Kg")!.Code);
    }

    [Fact]
    public void PackagingWeightAndProductVariantStayDistinct()
    {
        var store = NewStore(Item("HH_177", "Sông Gianh HC-15 bột (bao 25kg)", "Tấn", "kg"),
            Item("HH_306", "Sông Gianh HC-15 bột (bao 10kg)", "Tấn", "kg"));

        Assert.Equal("HH_177", store.Find("Sông Gianh HC-15 bột (25kg)", "kg")!.Code);
        Assert.Equal("HH_306", store.Find("Sông Gianh HC-15 bột (10kg)", "kg")!.Code);
        Assert.Null(store.Find("Sông Gianh HC-16 bột (25kg)", "kg"));
    }

    [Fact]
    public void TwoExportedPackagingMatchesStayAmbiguous()
    {
        var store = NewStore(Item("HH_340", "Sông Gianh HC-15 bột (25kg)", "kg"));
        store.Merge([Item("HH_177", "Sông Gianh HC-15 bột (bao 25kg)", "Tấn", "kg"),
            Item("HH_178", "Sông Gianh HC-15 bột (25kg)", "kg")], temp.Combine("export.xlsx"));

        Assert.Equal("ITEM_AMBIGUOUS", Assert.Throws<AutomationException>(() => store.Find("Sông Gianh HC-15 bột (25kg)", "kg")).Code);
    }

    [Fact]
    public void GeneratedItemWithoutAnExportedMatchIsStillUsable()
    {
        var store = NewStore();
        store.Merge([Item("HH_177", "Sông Gianh HC-15 bột (bao 25kg)", "Tấn", "kg")], temp.Combine("export.xlsx"));
        store.BeginPending(new("HH_340", "Hàng mới", "Gói", DateTimeOffset.UtcNow));
        store.ConfirmPending("HH_340");

        Assert.Equal("HH_340", new ItemCatalogStore(CatalogPath).Find("Hàng mới", "Gói")!.Code);
    }

    [Fact]
    public void RetainedCodeWithoutExportOrCreationEvidenceIsNeverSelected()
    {
        var store = NewStore(Item("HH_341", "Fu-army 40EC 100ml (ĐN)", "Chai"));
        store.Merge([Item("HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai")], temp.Combine("export.xlsx"));

        var reopened = new ItemCatalogStore(CatalogPath);
        Assert.Null(reopened.Find("Fu-army 40EC 100ml (ĐN)", "Chai"));
        Assert.NotNull(reopened.FindByCode("HH_341"));
        Assert.Equal("HH_342", reopened.NextCode());
    }

    [Fact]
    public void NewStoreOnAMissingFileIsEmpty()
    {
        var store = new ItemCatalogStore(CatalogPath);

        Assert.Equal(0, store.Count);
        Assert.Null(store.UpdatedAt);
        Assert.Null(store.SourceFile);
        Assert.Null(store.Find("Phân kali", "kg"));
        Assert.False(File.Exists(CatalogPath));
    }

    [Fact]
    public void FindReturnsTheItemWithTheSameNameAndUnit()
    {
        var store = NewStore(Item("HH_02", "Phân NPK 16.16.813S QN", "kg"), Item("HH_03", "Phân kali", "kg"));

        Assert.Equal("HH_03", store.Find("Phân kali", "kg")!.Code);
    }

    [Theory]
    [InlineData("PHÂN KALI", "KG")]
    [InlineData("  phân   kali ", "kg")]
    [InlineData("Phan kali", "kg", false)]
    [InlineData("Phân ka li", "k g")]
    public void FindIgnoresCaseAndSpacingButNotDiacritics(string name, string unit, bool found = true)
    {
        var store = NewStore(Item("HH_03", "Phân kali", "kg"));

        Assert.Equal(found, store.Find(name, unit) is not null);
    }

    [Fact]
    public void FindIgnoresToneMarkPlacementInNamesAndUnits()
    {
        var store = NewStore(Item("HH_20", "Thuốc trừ sâu Hoà Bình", "Chai"));

        Assert.Equal("HH_20", store.Find("Thuốc trừ sâu Hòa Bình", "Chai")!.Code);
    }

    [Fact]
    public void FindIgnoresSpacesInsideUnitsLikeChai90Ml()
    {
        var store = NewStore(Item("HH_21", "Rông đỏ 200SL", "Chai 90 ml"));

        Assert.Equal("HH_21", store.Find("Rông đỏ 200SL", "chai 90ml")!.Code);
    }

    [Fact]
    public void FindReturnsNullForUnknownNames()
    {
        var store = NewStore(Item("HH_03", "Phân kali", "kg"));

        Assert.Null(store.Find("Phân đạm", "kg"));
    }

    [Fact]
    public void SameNameWithDifferentUnitsPicksTheMatchingUnit()
    {
        var store = NewStore(Item("HH_30", "Hesman 60WG", "Gói"), Item("HH_31", "Hesman 60WG", "Thùng"));

        Assert.Equal("HH_31", store.Find("Hesman 60WG", "Thùng")!.Code);
        Assert.Equal("HH_30", store.Find("Hesman 60WG", "Gói")!.Code);
    }

    [Fact]
    public void ConversionUnitCountsAsAMatchingUnit()
    {
        var store = NewStore(Item("HH_40", "Phân NPK Cà Mau Gold", "Tấn", "kg"));

        Assert.Equal("HH_40", store.Find("Phân NPK Cà Mau Gold", "kg")!.Code);
        Assert.Equal("HH_40", store.Find("Phân NPK Cà Mau Gold", "Tấn")!.Code);
    }

    // ---- ambiguity / duplicates ----

    [Fact]
    public void TwoCodesWithTheSameNameAndUnitAreAmbiguous()
    {
        var store = NewStore(Item("HH_10", "Phân kali", "kg"), Item("HH_11", "PHÂN KALI", "kg"));

        var error = Assert.Throws<AutomationException>(() => store.Find("Phân kali", "kg"));

        Assert.Equal("ITEM_AMBIGUOUS", error.Code);
        Assert.Contains("HH_10", error.Message);
        Assert.Contains("HH_11", error.Message);
    }

    [Fact]
    public void KnownNameWithOnlyOtherUnitsIsAConflictNotAMiss()
    {
        var store = NewStore(Item("HH_50", "Phân Ure Đạm Phú Mỹ loại 1 (bao)", "Bao"));

        var error = Assert.Throws<AutomationException>(() => store.Find("Phân Ure Đạm Phú Mỹ loại 1 (bao)", "kg"));

        Assert.Equal("ITEM_UNIT_CONFLICT", error.Code);
        Assert.Contains("HH_50", error.Message);
        Assert.Contains("Bao", error.Message);
        Assert.False(error.InputMayHaveBeenSent);
    }

    [Fact]
    public void ConflictMessageDescribesUnitAndConversionUnit()
    {
        var store = NewStore(Item("HH_51", "Phân NPK Cà Mau Gold", "Tấn", "Bao"));

        var error = Assert.Throws<AutomationException>(() => store.Find("Phân NPK Cà Mau Gold", "Lít"));

        Assert.Contains("Tấn/Bao", error.Message);
    }

    [Fact]
    public void KilogramsCanBeResolvedToTheTonItemOnlyWhenAllowed()
    {
        var store = NewStore(Item("CM GOLD 20.10.10", "Phân NPK Cà Mau Gold 20-10-10-25kg", "Tấn"));
        const string name = "Phân NPK Cà Mau Gold 20-10-10-25kg";

        Assert.Equal("ITEM_UNIT_CONFLICT", Assert.Throws<AutomationException>(() => store.Find(name, "kg")).Code);
        Assert.Equal("CM GOLD 20.10.10", store.Find(name, "kg", allowKgToTon: true)!.Code);
        Assert.Equal("CM GOLD 20.10.10", store.Find(name, "Kg", allowKgToTon: true)!.Code);
    }

    [Fact]
    public void KilogramToTonConversionDoesNotApplyToOtherUnits()
    {
        var store = NewStore(Item("HH_60", "Phân kali", "Tấn"));

        Assert.Equal("ITEM_UNIT_CONFLICT", Assert.Throws<AutomationException>(() => store.Find("Phân kali", "Bao", allowKgToTon: true)).Code);
    }

    [Fact]
    public void KilogramToTonConversionStaysConflictWhenTwoTonItemsExist()
    {
        var store = NewStore(Item("HH_61", "Phân kali", "Tấn"), Item("HH_62", "Phân kali", "Tấn"));

        Assert.Equal("ITEM_AMBIGUOUS", Assert.Throws<AutomationException>(() => store.Find("Phân kali", "Tấn")).Code);
        Assert.Equal("ITEM_UNIT_CONFLICT", Assert.Throws<AutomationException>(() => store.Find("Phân kali", "kg", allowKgToTon: true)).Code);
    }

    [Fact]
    public void SingleItemWithoutAUnitIsTheSameItemForAnyUnit()
    {
        var store = NewStore(Item("HH_04", "Phân ĐT Lót thúc 17.12.5", ""));

        Assert.Equal("HH_04", store.Find("Phân ĐT Lót thúc 17.12.5", "kg")!.Code);
    }

    [Fact]
    public void SeveralUnitlessItemsWithTheSameNameStayAConflict()
    {
        var store = NewStore(Item("HH_04", "Phân ĐT Lót thúc 17.12.5", ""), Item("HH_05", "Phân ĐT Lót thúc 17.12.5", ""));

        Assert.Equal("ITEM_UNIT_CONFLICT", Assert.Throws<AutomationException>(() => store.Find("Phân ĐT Lót thúc 17.12.5", "kg")).Code);
    }

    [Fact]
    public void AddingAnExistingCodeIsRejectedAndNothingChanges()
    {
        var store = NewStore(Item("HH_01", "Chery 15g", "Gói"));

        var error = Assert.Throws<AutomationException>(() => store.Add(Item("HH_01", "Khác", "kg")));

        Assert.Equal("ITEM_CODE_EXISTS", error.Code);
        Assert.Equal("Chery 15g", Assert.Single(store.Snapshot()).Name);
    }

    [Fact]
    public void ItemCodesAreCaseSensitiveWhenAdding()
    {
        var store = NewStore(Item("HH_01", "Chery 15g", "Gói"));

        store.Add(Item("hh_01", "Khác", "kg"));

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void BlankNamesAndCodesAreDroppedAndTextIsTrimmedOnMerge()
    {
        var store = NewStore();
        var source = temp.WriteText("export.xlsx", "x");

        store.Merge([Item(" HH_01 ", "  Chery 15g ", " Gói "), Item("", "Không mã", "kg"), Item("HH_02", "  ", "kg")], source);

        var only = Assert.Single(store.Snapshot());
        Assert.Equal(new ItemCatalogEntry("HH_01", "Chery 15g", "Gói"), only);
    }

    // ---- canonical unit ----

    [Fact]
    public void CanonicalUnitReturnsTheMostCommonSpelling()
    {
        var store = NewStore(Item("A", "a", "kg"), Item("B", "b", "kg"), Item("C", "c", "Kg"), Item("D", "d", "Chai 90 ml"));

        Assert.Equal("kg", store.CanonicalUnit("KG"));
        Assert.Equal("Chai 90 ml", store.CanonicalUnit("chai 90ml"));
    }

    [Fact]
    public void CanonicalUnitLooksAtConversionUnitsAndBreaksTiesAlphabetically()
    {
        var store = NewStore(Item("A", "a", "Tấn", "kg"), Item("B", "b", "Bao", "Kg"));

        Assert.Equal("Kg", store.CanonicalUnit("kg"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Lít")]
    public void CanonicalUnitIsNullForBlankOrUnknownUnits(string unit)
        => Assert.Null(NewStore(Item("A", "a", "kg")).CanonicalUnit(unit));

    // ---- next code ----

    [Fact]
    public void NextCodeStartsAtOneInAnEmptyCatalog() => Assert.Equal("HH_01", NewStore().NextCode());

    [Fact]
    public void NextCodeIsAboveTheHighestKnownNumber()
    {
        var store = NewStore(Item("HH_01", "a", "kg"), Item("HH_09", "b", "kg"), Item("HH_05", "c", "kg"));

        Assert.Equal("HH_10", store.NextCode());
    }

    [Fact]
    public void NextCodeIgnoresCodesOutsideTheSeriesAndKeepsTwoDigitPadding()
    {
        var store = NewStore(Item("HH _KG", "a", "kg"), Item("CM GOLD 20.10.10", "b", "Tấn"), Item("HH_003", "c", "kg"), Item("hh_7", "d", "kg"));

        Assert.Equal("HH_08", store.NextCode());
    }

    [Fact]
    public void NextCodeGrowsPastTwoDigits()
        => Assert.Equal("HH_101", NewStore(Item("HH_100", "a", "kg")).NextCode());

    [Fact]
    public void NextCodeAlsoSkipsPendingCodes()
    {
        var store = NewStore(Item("HH_03", "a", "kg"));
        store.BeginPending(new PendingItemCreation("HH_12", "Mới", "kg", DateTimeOffset.UtcNow));

        Assert.Equal("HH_13", store.NextCode());
    }

    [Theory]
    [InlineData("HH_20", "HH_20")]
    [InlineData("HH_04", "HH_04")]
    [InlineData("HH_02", "HH_04")]
    [InlineData("HH_03", "HH_04")]
    [InlineData("KH_99", "HH_04")]
    [InlineData("không phải mã", "HH_04")]
    public void NextCodeFollowsMisaProposalOnlyWhenItIsAhead(string proposed, string expected)
    {
        var store = NewStore(Item("HH_01", "a", "kg"), Item("HH_03", "b", "kg"));

        Assert.Equal(expected, store.NextCode(proposed: proposed));
    }

    [Fact]
    public void NextCodeSupportsACustomPrefix()
    {
        var store = NewStore(Item("VT_07", "a", "kg"), Item("HH_30", "b", "kg"));

        Assert.Equal("VT_08", store.NextCode("VT_"));
    }

    // ---- pending creations ----

    [Fact]
    public void PendingItemsAreFoundByNameAndUnitBeforeTheyAreConfirmed()
    {
        var store = NewStore();
        var pending = new PendingItemCreation("HH_01", "Rông đỏ 200SL", "Chai", DateTimeOffset.UtcNow, "run-1");

        store.BeginPending(pending);

        Assert.Equal(1, store.PendingCount);
        Assert.Equal(pending, store.FindPending("RÔNG ĐỎ 200sl", "chai"));
        Assert.Null(store.FindPending("Rông đỏ 200SL", "Gói"));
        Assert.Null(store.Find("Rông đỏ 200SL", "Chai"));
    }

    [Fact]
    public void PendingCreationIsDurableBeforeConfirmation()
    {
        var store = NewStore();
        store.BeginPending(new PendingItemCreation("HH_01", "Rông đỏ 200SL", "Chai", DateTimeOffset.UtcNow, "run-1"));

        var reopened = new ItemCatalogStore(CatalogPath);

        Assert.Equal("HH_01", Assert.Single(reopened.PendingSnapshot()).Code);
        Assert.Equal("run-1", reopened.PendingSnapshot()[0].RunId);
    }

    [Fact]
    public void BeginPendingRejectsCodesAlreadyKnownOrPending()
    {
        var store = NewStore(Item("HH_01", "Chery 15g", "Gói"));
        store.BeginPending(new PendingItemCreation("HH_02", "A", "kg", DateTimeOffset.UtcNow));

        Assert.Equal("ITEM_CODE_EXISTS", Assert.Throws<AutomationException>(() => store.BeginPending(new("hh_01", "B", "kg", DateTimeOffset.UtcNow))).Code);
        Assert.Equal("ITEM_CODE_EXISTS", Assert.Throws<AutomationException>(() => store.BeginPending(new("HH_02", "C", "kg", DateTimeOffset.UtcNow))).Code);
        Assert.Equal(1, store.PendingCount);
    }

    [Fact]
    public void ConfirmPendingPromotesTheItemToTheCatalog()
    {
        var store = NewStore();
        store.BeginPending(new PendingItemCreation("HH_07", "Hesman 60WG 18g", "Gói", DateTimeOffset.UtcNow));

        store.ConfirmPending("HH_07");

        Assert.Equal(0, store.PendingCount);
        Assert.Equal("HH_07", store.Find("Hesman 60WG 18g", "Gói")!.Code);
        Assert.Equal(1, new ItemCatalogStore(CatalogPath).Count);
    }

    [Fact]
    public void ConfirmingAnUnknownPendingCodeFails()
        => Assert.Equal("ITEM_PENDING_MISSING", Assert.Throws<AutomationException>(() => NewStore().ConfirmPending("HH_99")).Code);

    [Fact]
    public void CancelPendingDropsOnlyThatCode()
    {
        var store = NewStore();
        store.BeginPending(new PendingItemCreation("HH_01", "A", "kg", DateTimeOffset.UtcNow));
        store.BeginPending(new PendingItemCreation("HH_02", "B", "kg", DateTimeOffset.UtcNow));

        store.CancelPending("HH_01");
        store.CancelPending("HH_404");

        Assert.Equal("HH_02", Assert.Single(store.PendingSnapshot()).Code);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void TwoUnconfirmedCreationsForTheSameItemAreUncertain()
    {
        var store = NewStore();
        store.BeginPending(new PendingItemCreation("HH_01", "Hesman 60WG", "Gói", DateTimeOffset.UtcNow));
        store.BeginPending(new PendingItemCreation("HH_02", "Hesman 60WG", "Gói", DateTimeOffset.UtcNow));

        var error = Assert.Throws<AutomationException>(() => store.FindPending("Hesman 60WG", "Gói"));

        Assert.Equal("ITEM_CREATE_UNCERTAIN", error.Code);
        Assert.Contains("HH_01", error.Message);
        Assert.Contains("HH_02", error.Message);
    }

    // ---- merge with a MISA export ----

    [Fact]
    public void CompleteNativeRefreshRemovesStaleMappingsButPreservesUnresolvedCreations()
    {
        var store = NewStore(Item("HH_340", "Sông Gianh HC-15 bột (25kg)", "kg"));
        store.BeginPending(new("HH_341", "Fu-army 40EC 100ml (ĐN)", "Chai", DateTimeOffset.UtcNow));

        store.RefreshNative([Item("HH_340", "Hàng khác", "kg"), Item("HH_350", "Sông Gianh HC-15 bột (25kg)", "kg")]);

        var reopened = new ItemCatalogStore(CatalogPath);
        Assert.Equal("HH_350", reopened.Find("Sông Gianh HC-15 bột (25kg)", "kg")!.Code);
        Assert.Equal("HH_340", reopened.Find("Hàng khác", "kg")!.Code);
        Assert.Equal("HH_341", Assert.Single(reopened.PendingSnapshot()).Code);
    }

    [Fact]
    public void MergeLetsFileRowsOverrideSameCodeAndKeepsToolCreatedCodes()
    {
        var store = NewStore(Item("HH_01", "Chery 15g", "Gói"), Item("HH_90", "Tool đã tạo", "kg"));
        var source = temp.WriteText("export.xlsx", "x");

        var count = store.Merge([Item("HH_01", "Chery 15g (đã đổi tên)", "Gói"), Item("HH_02", "Phân NPK 16.16.813S QN", "kg")], source);

        Assert.Equal(3, count);
        Assert.Equal(3, store.Count);
        Assert.Equal("Chery 15g (đã đổi tên)", store.Snapshot().Single(item => item.Code == "HH_01").Name);
        Assert.Contains(store.Snapshot(), item => item.Code == "HH_90");
        Assert.Equal(Path.GetFullPath(source), Path.GetFullPath(store.SourceFile!));
    }

    [Fact]
    public void MergeKeepsTheFirstRowWhenTheExportRepeatsACode()
    {
        var store = NewStore();

        store.Merge([Item("HH_01", "Lần một", "kg"), Item("HH_01", "Lần hai", "kg")], temp.WriteText("export.xlsx", "x"));

        Assert.Equal("Lần một", Assert.Single(store.Snapshot()).Name);
    }

    [Fact]
    public void MergeDoesNotCreateDuplicatesWhenTheSameExportIsLoadedTwice()
    {
        var store = NewStore();
        var rows = new[] { Item("HH_01", "Chery 15g", "Gói"), Item("HH_02", "Phân kali", "kg") };
        var source = temp.WriteText("export.xlsx", "x");

        store.Merge(rows, source);
        store.Merge(rows, source);

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void MergeSettlesPendingCodesPresentInTheExport()
    {
        var store = NewStore();
        store.BeginPending(new PendingItemCreation("HH_07", "Hesman 60WG 18g", "Gói", DateTimeOffset.UtcNow.AddHours(2)));

        store.Merge([Item("HH_07", "Hesman 60WG 18g", "Gói")], temp.WriteText("export.xlsx", "x"));

        Assert.Equal(0, store.PendingCount);
        Assert.Equal("HH_07", store.Find("Hesman 60WG 18g", "Gói")!.Code);
    }

    [Fact]
    public void MergeDropsPendingCodesAbsentFromAnExportWrittenAfterTheyStarted()
    {
        var store = NewStore();
        store.BeginPending(new PendingItemCreation("HH_07", "Hesman 60WG 18g", "Gói", DateTimeOffset.UtcNow.AddHours(-1)));

        store.Merge([Item("HH_01", "Chery 15g", "Gói")], temp.WriteText("export.xlsx", "x"));

        Assert.Equal(0, store.PendingCount);
        Assert.Null(store.Find("Hesman 60WG 18g", "Gói"));
    }

    [Fact]
    public void MergeKeepsPendingCodesStartedAfterTheExportWasWritten()
    {
        var store = NewStore();
        var source = temp.WriteText("export.xlsx", "x");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(-2));
        store.BeginPending(new PendingItemCreation("HH_07", "Hesman 60WG 18g", "Gói", DateTimeOffset.UtcNow));

        store.Merge([Item("HH_01", "Chery 15g", "Gói")], source);

        Assert.Equal("HH_07", Assert.Single(store.PendingSnapshot()).Code);
    }

    [Fact]
    public void MergeKeepsPendingCodesWhenTheExportFileCannotBeDated()
    {
        var store = NewStore();
        store.BeginPending(new PendingItemCreation("HH_07", "Hesman 60WG 18g", "Gói", DateTimeOffset.UtcNow.AddHours(-1)));

        store.Merge([Item("HH_01", "Chery 15g", "Gói")], temp.Combine("does-not-exist.xlsx"));

        Assert.Equal(1, store.PendingCount);
    }

    // ---- persistence ----

    [Fact]
    public void CatalogRoundTripsThroughTheFile()
    {
        var store = NewStore(Item("CM GOLD 20.10.10", "Phân NPK Cà Mau Gold 20-10-10-25kg", "Tấn", "kg"), Item("HH_01", "Chery 15g", "Gói"));
        var source = temp.WriteText("export.xlsx", "x");
        store.Merge([Item("HH_02", "Phân NPK 16.16.813S QN", "kg")], source);

        var reopened = new ItemCatalogStore(CatalogPath);

        Assert.Equal(store.Snapshot().OrderBy(item => item.Code), reopened.Snapshot().OrderBy(item => item.Code));
        Assert.Equal(store.SourceFile, reopened.SourceFile);
        Assert.NotNull(reopened.UpdatedAt);
        Assert.Equal("HH_02", reopened.Find("Phân NPK 16.16.813S QN", "kg")!.Code);
        // Provenance survives the reload: a cached code missing from the export is still not auto-selected.
        Assert.Null(reopened.Find("Phân NPK Cà Mau Gold 20-10-10-25kg", "kg"));
    }

    [Fact]
    public void SavingLeavesNoTemporaryFilesBehind()
    {
        var store = NewStore(Item("HH_01", "Chery 15g", "Gói"));
        store.Add(Item("HH_02", "Phân kali", "kg"));

        Assert.Equal([CatalogPath], Directory.GetFiles(Path.GetDirectoryName(CatalogPath)!));
    }

    [Fact]
    public void CorruptCatalogFileIsReportedNotSilentlyReplaced()
    {
        temp.WriteText("state/item-catalog.json", "{ not json");

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => new ItemCatalogStore(CatalogPath));
        Assert.Equal("{ not json", File.ReadAllText(CatalogPath));
    }

    [Fact]
    public void NullJsonDocumentIsRejectedAsCorrupt()
    {
        temp.WriteText("state/item-catalog.json", "null");

        Assert.Throws<InvalidDataException>(() => new ItemCatalogStore(CatalogPath));
    }

    [Fact]
    public void PendingEntriesWithoutACodeAreIgnoredOnLoad()
    {
        temp.WriteText("state/item-catalog.json", """
            {"schemaVersion":1,"updatedAt":"2026-10-04T00:00:00+00:00","sourceFile":null,"items":[],
             "pending":[{"code":"","name":"x","unit":"kg","startedAt":"2026-10-04T00:00:00+00:00"},
                        {"code":"HH_05","name":"y","unit":"kg","startedAt":"2026-10-04T00:00:00+00:00"}]}
            """);

        var store = new ItemCatalogStore(CatalogPath);

        Assert.Equal("HH_05", Assert.Single(store.PendingSnapshot()).Code);
    }

    [Fact]
    public void FailedSaveRollsBackTheInMemoryChange()
    {
        var store = NewStore(Item("HH_01", "Chery 15g", "Gói"));
        File.Delete(CatalogPath);
        Directory.CreateDirectory(CatalogPath); // a directory in the way makes the final swap fail

        Assert.ThrowsAny<Exception>(() => store.Add(Item("HH_02", "Phân kali", "kg")));

        Assert.Equal(1, store.Count);
        Assert.Null(store.Find("Phân kali", "kg"));
    }

    [Fact]
    public void FailedPendingSaveDoesNotRecordTheCodeInMemory()
    {
        var store = NewStore();
        Directory.CreateDirectory(CatalogPath);

        Assert.ThrowsAny<Exception>(() => store.BeginPending(new PendingItemCreation("HH_01", "A", "kg", DateTimeOffset.UtcNow)));

        Assert.Equal(0, store.PendingCount);
    }

    [Fact]
    public void ChangedEventFiresForEveryMutation()
    {
        var store = NewStore();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Add(Item("HH_01", "Chery 15g", "Gói"));
        store.Merge([Item("HH_02", "Phân kali", "kg")], temp.WriteText("export.xlsx", "x"));
        store.BeginPending(new PendingItemCreation("HH_03", "A", "kg", DateTimeOffset.UtcNow));
        store.ConfirmPending("HH_03");
        store.BeginPending(new PendingItemCreation("HH_04", "B", "kg", DateTimeOffset.UtcNow));
        store.CancelPending("HH_04");

        Assert.Equal(6, raised);
    }

    [Fact]
    public void ConcurrentAddsAllLand()
    {
        var store = NewStore();

        Parallel.For(0, 20, index => store.Add(Item($"HH_{index:00}", $"Hàng {index}", "kg")));

        Assert.Equal(20, store.Count);
        Assert.Equal(20, new ItemCatalogStore(CatalogPath).Count);
    }

    [Fact]
    public void NameKeyIsTheCompactTextKey()
        => Assert.Equal(TextKey.Compact("Chai 90 ml"), ItemCatalogStore.NameKey("chai 90ml"));
}
