namespace MisaManager.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void ProductIdentityIsStable() => Assert.Equal("MisaManager", ProductIdentity.TechnicalName);
}
