using MisaManager.Automation;

namespace MisaManager.Integration.Tests;

/// <summary>Pre-run selector check and the reusable <see cref="MisaScreen"/> API on the fake R20.</summary>
public sealed class ScreenTests : UiTest
{
    [Fact]
    public Task ScreenCheckResolvesEveryProfileSelectorOnTheOpenScreens() => WithFakeAsync(async (misa, harness, token) =>
    {
        var list = await harness.Input.CheckScreensAsync(misa.Window, harness.Profile.Screens, token);
        Assert.True(list.Ok, list.Details);
        var sales = list.Screens.Single(screen => screen.Key == WorkflowProfile.SalesList);
        Assert.True(sales.Open, list.Details);
        // R20's module tab strip is outside the module panel the profile scopes to.
        Assert.Contains(sales.Elements, item => item.Key == "module_tab" && item.Status == ScreenCheckStatus.Ok);
        // The report never carries the licence/data company from the window title.
        Assert.Equal(harness.Profile.VersionTitle, list.WindowTitle);
        Assert.False(list.Screens.Single(screen => screen.Key == WorkflowProfile.SalesVoucher).Open, list.Details);
        Assert.False(list.Screens.Single(screen => screen.Key == WorkflowProfile.ItemNew).Open, list.Details);

        var salesScreen = await MisaScreen.TryOpenAsync(harness.Input, misa.Window, harness.Profile.Screen(WorkflowProfile.SalesList), token);
        Assert.NotNull(salesScreen);
        await salesScreen.ClickAsync("add", token: token);
        await MisaScreen.OpenAsync(harness.Input, misa.Window, harness.Profile.Screen(WorkflowProfile.SalesVoucher), token);

        var editor = await harness.Input.CheckScreensAsync(misa.Window, harness.Profile.Screens, token);
        Assert.True(editor.Ok, editor.Details);
        var voucher = editor.Screens.Single(screen => screen.Key == WorkflowProfile.SalesVoucher);
        Assert.True(voucher.Open, editor.Details);
        Assert.Contains(voucher.Elements, item => item.Key == "voucher_number" && item.Status == ScreenCheckStatus.Ok);
        Assert.Contains(voucher.Elements, item => item.Key == "number" && item.Status == ScreenCheckStatus.OtherTab);
        Assert.Contains(voucher.Elements, item => item.Key == "grid:items" && item.Status == ScreenCheckStatus.Ok);
    });

    [Fact]
    public Task MisaScreenOpensTheEditorReadsFieldsSwitchesTabsAndCloses() => WithFakeAsync(async (misa, harness, token) =>
    {
        var sales = await MisaScreen.TryOpenAsync(harness.Input, misa.Window, harness.Profile.Screen(WorkflowProfile.SalesList), token)
            ?? throw new InvalidOperationException("Phân hệ Bán hàng chưa mở.");
        await sales.ClickAsync("add", token: token);
        var voucher = await MisaScreen.OpenAsync(harness.Input, misa.Window, harness.Profile.Screen(WorkflowProfile.SalesVoucher), token);

        Assert.StartsWith("PT0", await voucher.ReadAsync("voucher_number", token));
        await voucher.SelectTabAsync("invoice", token);
        Assert.Equal("1C26MLV", await voucher.ReadAsync("series", token));
        Assert.Equal("Tiền mặt", await voucher.ReadAsync("payment", token));
        Assert.Equal(0, await voucher.Grid("items").RowCountAsync(token));

        await voucher.ClickAsync("close", leavesScope: true, token: token);
        Assert.Null(await MisaScreen.TryOpenAsync(harness.Input, misa.Window, harness.Profile.Screen(WorkflowProfile.SalesVoucher), token));
    });
}
