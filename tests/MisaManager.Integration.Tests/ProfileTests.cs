using MisaManager.Automation;

namespace MisaManager.Integration.Tests;

/// <summary>Profile/fake consistency checks that need no desktop.</summary>
public sealed class ProfileTests : UiTest
{
    private static WorkflowProfile Load() => WorkflowProfile.Load(Path.Combine(AppContext.BaseDirectory, "profiles", "misa-sme2023-r20.json"));

    [Fact]
    public void ProfileDeclaresEveryScreenTheSalesWorkflowDrives()
    {
        var profile = Load();
        Assert.Equal("MISA SME 2023 R20 Enterprise", profile.VersionTitle);
        foreach (var key in new[] { WorkflowProfile.SalesList, WorkflowProfile.SalesVoucher, WorkflowProfile.ItemNew, WorkflowProfile.TaxRoundingPrompt, "item_lookup" })
            Assert.True(profile.Screens.ContainsKey(key), key);
        Assert.Equal("KHH", profile.DefaultItemStock);
        var customer = Assert.IsType<CustomerDialogProfile>(profile.NewCustomer);
        Assert.Equal(("Thêm Khách hàng", "F9", "Mã (*)"), (customer.Title, customer.OpenKey, customer.Code.Label));
        Assert.True(customer.Code.ByLabel && customer.Address.ByLabel && !customer.Save.ByLabel);
        // R20 does not expose the kind options as RadioButtons: they must be found by caption only.
        Assert.Equal(("", ""), (customer.Organization.ControlType, customer.Individual.ControlType));
        Assert.Equal("Hủy bỏ", customer.Cancel.Name);
    }

    [Fact]
    public void PurchaseProfileDeclaresTheMuaHangScreensAndAccounts()
    {
        var profile = WorkflowProfile.Load(Path.Combine(AppContext.BaseDirectory, "profiles", "misa-sme2023-r20.json"), VoucherKind.Purchase);
        Assert.True(profile.IsPurchase);
        Assert.Equal(WorkflowProfile.PurchaseList, profile.ListScreen);
        foreach (var key in new[] { WorkflowProfile.PurchaseList, WorkflowProfile.PurchaseVoucher })
            Assert.True(profile.Screens.ContainsKey(key), key);
        Assert.Equal(("KHH", "1561", "331"), (profile.PurchaseStock, profile.StockAccount, profile.PayableAccount));
        // Real R20 (failure report 0.2.132): the "Mua hàng hóa, dịch vụ" list is form frmPUVoucherService, not frmPUVoucher.
        Assert.Equal("frmPUVoucherService", profile.SalesScopeId);
        // Real R20 (failure report 0.2.133) has no "TabItem Key PUVoucherService": the tab is selected by caption + TabItem.
        Assert.Equal(new NativeSelector("", "Mua hàng hóa, dịch vụ", "TabItem"), profile.SalesTab);
        Assert.False(Load().IsPurchase);
    }

    [Fact]
    public Task FakeMisaExposesEveryRequiredPurchaseSelector() => WithFakeAsync(async (misa, harness, token) =>
    {
        var profile = harness.PurchaseProfile;
        var opened = await harness.Input.CheckScreensAsync(misa.Window, profile.Screens, token);
        Assert.True(opened.Ok, opened.Details);
        var list = opened.Screens.Single(screen => screen.Key == WorkflowProfile.PurchaseList);
        Assert.True(list.Open, opened.Details);
        Assert.All(list.Elements.Where(item => item.Required), item => Assert.True(item.Status == ScreenCheckStatus.Ok, item.Key + ": " + opened.Details));
        Assert.Contains(list.Elements, item => item.Key == "module_tab" && item.Status == ScreenCheckStatus.Ok);
        await using var native = new NativeObservationService();
        var observation = await native.ObserveAsync(misa.Window.WindowHandle, null, token, profile.SalesScopeId);
        Assert.True(profile.IsSalesSurface(observation.Elements));
        Assert.False(opened.Screens.Single(screen => screen.Key == WorkflowProfile.SalesList).Open, opened.Details);

        var purchases = await MisaScreen.TryOpenAsync(harness.Input, misa.Window, profile.Screen(WorkflowProfile.PurchaseList), token)
            ?? throw new InvalidOperationException("Phân hệ Mua hàng chưa mở.");
        await purchases.ClickAsync("add", token: token);
        var voucher = await MisaScreen.OpenAsync(harness.Input, misa.Window, profile.Screen(WorkflowProfile.PurchaseVoucher), token);

        var editor = await harness.Input.CheckScreensAsync(misa.Window, profile.Screens, token);
        Assert.True(editor.Ok, editor.Details);
        var detail = editor.Screens.Single(screen => screen.Key == WorkflowProfile.PurchaseVoucher);
        Assert.True(detail.Open, editor.Details);
        Assert.All(detail.Elements.Where(item => item.Required),
            item => Assert.True(item.Status is ScreenCheckStatus.Ok or ScreenCheckStatus.OtherTab, item.Key + ": " + editor.Details));
        Assert.Contains(detail.Elements, item => item.Key == "grid:items" && item.Status == ScreenCheckStatus.Ok);
        Assert.StartsWith("NK0", await voucher.ReadAsync("voucher_number", token));

        await voucher.ClickAsync("close", leavesScope: true, token: token);
        Assert.Null(await MisaScreen.TryOpenAsync(harness.Input, misa.Window, profile.Screen(WorkflowProfile.PurchaseVoucher), token));
    }, new FakeScenario(Purchase: true));

    [Fact]
    public void AutomationIdsAreUniqueWithinEachScreen()
    {
        foreach (var screen in Load().Screens.Values)
        {
            var ids = screen.Elements.Values.Select(element => element.AutomationId).Where(id => id.Length > 0).ToArray();
            Assert.True(ids.Length == ids.Distinct(StringComparer.Ordinal).Count(), screen.Key);
        }
    }

    [Fact]
    public void FakeMisaExecutableIsBuiltNextToTheTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "MISA.SME.exe chỉ được tạo khi build trên Windows.");
        Assert.True(File.Exists(FakeMisa.Executable), FakeMisa.Executable);
    }
}
