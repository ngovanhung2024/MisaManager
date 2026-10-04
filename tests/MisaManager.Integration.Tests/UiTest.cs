using System.Runtime.CompilerServices;
using MisaManager.Core;

namespace MisaManager.Integration.Tests;

public abstract class UiTest
{
    public const string Gate = "MISAMANAGER_UI_TESTS";

    protected static void RequireDesktop()
        => Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable(Gate) == "1",
            $"UI test: cần Windows có desktop và biến môi trường {Gate}=1.");

    /// <summary>Runs a body against a fresh fake MISA; on failure saves a screenshot and the fake's state next to the results.</summary>
    protected static async Task WithFakeAsync(Func<FakeMisa, AutomationHarness, CancellationToken, Task> body,
        FakeScenario? scenario = null, IEnumerable<ItemCatalogEntry>? catalog = null, [CallerMemberName] string name = "")
    {
        RequireDesktop();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var misa = await FakeMisa.StartAsync(scenario, timeout.Token);
        await using var harness = new AutomationHarness(misa.Root, catalog);
        try { await body(misa, harness, timeout.Token); }
        catch
        {
            var shot = FakeMisa.Screenshot(name);
            var tree = UiaDump.Dump(misa.Window.WindowHandle);
            var dumpDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults", "uia");
            Directory.CreateDirectory(dumpDirectory);
            await File.WriteAllTextAsync(Path.Combine(dumpDirectory, name + ".txt"), tree, CancellationToken.None);
            TestContext.Current.TestOutputHelper?.WriteLine($"Screenshot: {shot ?? "(không chụp được)"}; state: {misa.StatePath}; logs: {misa.Root}");
            TestContext.Current.TestOutputHelper?.WriteLine(tree.Length > 6000 ? tree[..6000] : tree);
            throw;
        }
    }

    protected static string Describe(LiveRunResult result)
        => $"outcome={result.Outcome}, status={result.Context.Snapshot.Status}, code={result.Context.Snapshot.LastErrorCode}, "
            + $"message={result.Context.Snapshot.LastMessage}, error={result.Error}";

    protected static string TempDirectory(FakeMisa misa) => Path.Combine(misa.Root, "source");
}
