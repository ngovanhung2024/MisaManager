using MisaManager.Automation;

namespace MisaManager.Integration.Tests;

/// <summary>Profile/fake consistency checks that need no desktop.</summary>
public sealed class ProfileTests
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
