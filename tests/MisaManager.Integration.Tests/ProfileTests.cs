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
        // Real R20 (failure report 0.2.136): the purchase receipt shows the supplier name in txtCAAccountObjectName.
        Assert.Equal("txtCAAccountObjectName", profile.Fields["customer_name"].AutomationId);
        // Real R20 (failure report 0.2.139): "Mẫu số HĐ" is combo cboInvTemplateNo; the label picked its inner textbox (FOCUS_FAILED).
        Assert.Equal("cboInvTemplateNo", profile.Fields["template"].AutomationId);
        // Real R20 (failure report 0.2.145): creTotalAmountOC does not exist on phiếu nhập; totals are found by caption.
        Assert.Equal(("Tổng tiền hàng", "Tiền thuế GTGT", "Tổng tiền thanh toán"),
            (profile.Fields["subtotal"].Label, profile.Fields["tax_total"].Label, profile.Fields["grand_total"].Label));
        Assert.False(Load().IsPurchase);
        // Sales "Mẫu số HĐ" is the same combo as on phiếu nhập.
        Assert.Equal("cboInvTemplateNo", Load().Fields["template"].AutomationId);
    }

    [Fact]
    public void PurchaseProfileHasAnExpenseVariantForMuaHangKhongQuaKho()
    {
        var stock = WorkflowProfile.Load(Path.Combine(AppContext.BaseDirectory, "profiles", "misa-sme2023-r20.json"), VoucherKind.Purchase);
        var expense = stock.ForExpense();
        Assert.False(stock.IsExpense);
        Assert.True(expense.IsExpense && expense.IsPurchase);
        Assert.Same(expense, stock.ForExpense());
        Assert.Equal(("Phiếu nhập", "Chứng từ ghi nợ"), (stock.ReceiptTab, expense.ReceiptTab));
        Assert.Equal(("TK Kho", "TK chi phí"), (stock.Columns["debit_account"], expense.Columns["debit_account"]));
        Assert.True(stock.Columns.ContainsKey("stock") && !expense.Columns.ContainsKey("stock"));
        Assert.Equal(("Chứng từ mua hàng", "ComboBox"), (stock.VoucherType?.Label, stock.VoucherType?.ControlType));
        Assert.Equal(("1. Mua hàng trong nước nhập kho", "2. Mua hàng trong nước không qua kho"),
            (stock.StockVoucherType, stock.ExpenseVoucherType));
        // The fake's purchase editor draws its Kho columns, receipt tab and voucher-type options from the same profile.
        Assert.Equal(stock.Fields["voucher_number"].AutomationId, expense.Fields["voucher_number"].AutomationId);
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
