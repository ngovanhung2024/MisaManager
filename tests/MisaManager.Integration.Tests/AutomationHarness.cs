using MisaManager.Automation;
using MisaManager.Core;

namespace MisaManager.Integration.Tests;

/// <summary>
/// The production wiring of src/MisaManager.App/AppServices.cs (profile → input → sales-voucher
/// gateway → WorkflowRunner) against isolated temp state. Keep it in step with AppServices.
/// </summary>
public sealed class AutomationHarness : IAsyncDisposable
{
    public AutomationHarness(string root, IEnumerable<ItemCatalogEntry>? catalog = null, IEnumerable<PartyCatalogEntry>? customers = null)
    {
        Profile = WorkflowProfile.Load(Path.Combine(AppContext.BaseDirectory, "profiles", "misa-sme2023-r20.json"));
        Input = new NativeInputService();
        Catalog = new ItemCatalogStore(Path.Combine(root, "item-catalog.json"));
        if (catalog is not null) Catalog.Import(catalog, Path.Combine(root, "catalog.xlsx"));
        Parties = new PartyCatalogStore(Path.Combine(root, "party-catalog.json"));
        if (customers is not null) Parties.Replace(PartyRole.Customer, customers, Path.Combine(root, "Khach_hang.xlsx"));
        Groups = new ItemGroupStore(Path.Combine(root, "item-groups.json"));
        Gateway = new NativeWorkflowGateway(new MisaWindowDiscovery(), Input, Profile) { ItemCatalog = Catalog, PartyCatalog = Parties, ItemGroups = Groups };
        Runner = new WorkflowRunner(Gateway, new JsonStateStore(Path.Combine(root, "state")), new RunEventStore(Path.Combine(root, "logs")));
    }

    public WorkflowProfile Profile { get; }
    public NativeInputService Input { get; }
    public ItemCatalogStore Catalog { get; }
    public PartyCatalogStore Parties { get; }
    public ItemGroupStore Groups { get; }
    public NativeWorkflowGateway Gateway { get; }
    public WorkflowRunner Runner { get; }

    public Task<LiveRunResult> RunAsync(InvoiceDocument invoice, FakeMisa misa, CancellationToken token)
        => Runner.RunLiveAsync(invoice, "", misa.WindowIdentityText, token, liveValidation: true, Profile.Revision,
            allowKgToTon: true, executionMode: WorkflowExecutionMode.LiveProduction);

    public async Task<LiveRunResult> RunNewSessionAsync(InvoiceDocument invoice, FakeMisa misa, CancellationToken token)
    {
        var abandoned = await Runner.AbandonStoppedSessionAsync(token);
        Assert.True(abandoned is null || abandoned.SessionDetached,
            "Checkpoint cũ không được bỏ trước khi bắt đầu phiên mới.");
        Assert.Null(Runner.Current);
        return await RunAsync(invoice, misa, token);
    }

    /// <summary>Enters every value of the invoice but stops before Cất, leaving the voucher open in the fake.</summary>
    public async Task<WorkflowBatchResult> EnterWithoutSavingAsync(InvoiceDocument invoice, FakeMisa misa, CancellationToken token)
    {
        var started = await Runner.StartAsync(invoice, "", misa.WindowIdentityText, token, liveValidation: true, Profile.Revision,
            allowKgToTon: true, executionMode: WorkflowExecutionMode.LiveProduction);
        return await Runner.RunUntilAsync(started.Snapshot.RunId, invoice.Fingerprint, WorkflowStatus.Ready,
            WorkflowStatus.AwaitingSave, 20, token, allowKgToTon: true);
    }

    public ValueTask DisposeAsync() => Input.DisposeAsync();
}
