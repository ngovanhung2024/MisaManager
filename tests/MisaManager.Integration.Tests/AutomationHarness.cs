using MisaManager.Automation;
using MisaManager.Core;

namespace MisaManager.Integration.Tests;

/// <summary>
/// The production wiring of src/MisaManager.App/AppServices.cs (profile → input → sales-voucher
/// gateway → WorkflowRunner) against isolated temp state. Keep it in step with AppServices.
/// </summary>
public sealed class AutomationHarness : IAsyncDisposable
{
    public AutomationHarness(string root, IEnumerable<ItemCatalogEntry>? catalog = null)
    {
        Profile = WorkflowProfile.Load(Path.Combine(AppContext.BaseDirectory, "profiles", "misa-sme2023-r20.json"));
        Input = new NativeInputService();
        Catalog = new ItemCatalogStore(Path.Combine(root, "item-catalog.json"));
        if (catalog is not null) Catalog.Merge(catalog, Path.Combine(root, "catalog.xlsx"));
        Gateway = new NativeWorkflowGateway(new MisaWindowDiscovery(), Input, Profile) { ItemCatalog = Catalog };
        Runner = new WorkflowRunner(Gateway, new JsonStateStore(Path.Combine(root, "state")), new RunEventStore(Path.Combine(root, "logs")));
    }

    public WorkflowProfile Profile { get; }
    public NativeInputService Input { get; }
    public ItemCatalogStore Catalog { get; }
    public NativeWorkflowGateway Gateway { get; }
    public WorkflowRunner Runner { get; }

    public Task<LiveRunResult> RunAsync(InvoiceDocument invoice, FakeMisa misa, CancellationToken token)
        => Runner.RunLiveAsync(invoice, "", misa.WindowIdentityText, token, liveValidation: true, Profile.Revision,
            allowKgToTon: true, executionMode: WorkflowExecutionMode.LiveProduction);

    public ValueTask DisposeAsync() => Input.DisposeAsync();
}
