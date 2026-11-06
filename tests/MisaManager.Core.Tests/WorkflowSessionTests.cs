using System.Collections.Immutable;
using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class WorkflowSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void OldWorkflowJsonDefaultsToAnAttachedSession()
    {
        var context = WorkflowFixtures.Context(InvoiceBuilder.Valid(), WorkflowFixtures.NewRunId());
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(context, ContractJson.Options))!;
        json.AsObject().Remove("sessionDetached");

        var restored = System.Text.Json.JsonSerializer.Deserialize<WorkflowContext>(json.ToJsonString(), ContractJson.Options)!;

        Assert.False(restored.SessionDetached);
        Assert.Equal(context.Snapshot.RunId, restored.Snapshot.RunId);
    }

    [Fact]
    public async Task AStopBeforeInputAllowsTheSameInvoiceToStartInANewMisaSession()
    {
        using var directory = new TempDirectory();
        var native = new Native();
        var runner = new WorkflowRunner(native, new JsonStateStore(directory.Combine("state")), new RunEventStore(directory.Combine("logs")));
        var invoice = InvoiceBuilder.Valid();
        var old = await runner.StartAsync(invoice, "company", "old-window", Token);
        await runner.PauseAsync(Token);
        await runner.AbandonStoppedSessionAsync(Token);

        var next = await runner.StartAsync(invoice, "company", "new-window", Token);

        Assert.NotEqual(old.Snapshot.RunId, next.Snapshot.RunId);
        Assert.Equal(WorkflowStatus.Ready, next.Snapshot.Status);
        Assert.Empty(native.Actions);
    }

    [Fact]
    public async Task FailureToPersistDetachmentKeepsTheActiveCheckpointAndItsUncertainty()
    {
        using var directory = new TempDirectory();
        var (originalRunner, state, native, old) = await SeedAsync(directory, WorkflowStatus.SaveUncertain);
        var failureStore = new FailingStore(state);
        var runner = new WorkflowRunner(native, failureStore, new RunEventStore(directory.Combine("logs")));
        await runner.InitializeAsync(Token);
        failureStore.FailDetachment = true;

        await Assert.ThrowsAsync<IOException>(() => runner.AbandonStoppedSessionAsync(Token));

        Assert.Equal(old.Snapshot.RunId, runner.Current!.Snapshot.RunId);
        Assert.Equal(WorkflowStatus.SaveUncertain, runner.Current.Snapshot.Status);
        Assert.False(runner.Current.SessionDetached);
        var restored = new WorkflowRunner(native, state, new RunEventStore(directory.Combine("logs")));
        await restored.InitializeAsync(Token);
        Assert.Equal(old.Snapshot.RunId, restored.Current!.Snapshot.RunId);
        Assert.False(restored.Current.SessionDetached);
        Assert.Empty(native.Actions);
    }
    [Fact]
    public async Task DetachedCheckpointIsNotRestoredButHistoryKeepsItsEvidence()
    {
        using var directory = new TempDirectory();
        var (runner, store, native, original) = await SeedAsync(directory, WorkflowStatus.InputUncertain);

        var detached = await runner.AbandonStoppedSessionAsync(Token);

        Assert.Null(runner.Current);
        Assert.True(detached!.SessionDetached);
        Assert.Equal(original.Snapshot.Status, detached.Snapshot.Status);
        Assert.Equal(original.Snapshot.LastInputDisposition, detached.Snapshot.LastInputDisposition);
        Assert.Equal(original.NativeValues, detached.NativeValues);
        var restored = new WorkflowRunner(native, store, new RunEventStore(directory.Combine("logs")));
        await restored.InitializeAsync(Token);
        Assert.Null(restored.Current);
        Assert.True(Assert.Single(await restored.ReadHistoryAsync(Token)).SessionDetached);
        Assert.Empty(native.Actions);
    }

    [Theory]
    [InlineData(WorkflowStatus.Paused)]
    [InlineData(WorkflowStatus.Blocked)]
    [InlineData(WorkflowStatus.InputUncertain)]
    [InlineData(WorkflowStatus.SaveUncertain)]
    public async Task DetachedRunDoesNotBlockAnotherInvoiceOrReplayOldInput(WorkflowStatus status)
    {
        using var directory = new TempDirectory();
        var (runner, _, native, old) = await SeedAsync(directory, status);
        await runner.AbandonStoppedSessionAsync(Token);
        var invoice = new InvoiceBuilder().WithNumber("1001").Build();

        var next = await runner.StartAsync(invoice, old.CompanyIdentity, "new-window", Token);

        Assert.Equal(WorkflowStatus.Ready, next.Snapshot.Status);
        Assert.NotEqual(old.Snapshot.RunId, next.Snapshot.RunId);
        Assert.False(next.SessionDetached);
        Assert.Empty(native.Actions);
    }

    [Theory]
    [InlineData(WorkflowStatus.InputUncertain)]
    [InlineData(WorkflowStatus.SaveUncertain)]
    public async Task SameInvoiceNeedsFreshReadOnlyProofEvenIfItsRevisionChanges(WorkflowStatus status)
    {
        using var directory = new TempDirectory();
        var (runner, _, native, old) = await SeedAsync(directory, status);
        await runner.AbandonStoppedSessionAsync(Token);
        var invoice = old.Invoice with { Revision = 2, Fingerprint = "changed-fingerprint" };

        var error = await Assert.ThrowsAsync<AutomationException>(() => runner.StartAsync(invoice, old.CompanyIdentity, old.WindowIdentity, Token));

        Assert.Equal("DETACHED_INVOICE_UNCERTAIN", error.Code);
        Assert.Equal([WorkflowRunner.ResolveStaleAction], native.Actions);
        Assert.Null(runner.Current);
    }

    [Theory]
    [InlineData(WorkflowStatus.Saved, WorkflowStatus.Discarded)]
    [InlineData(WorkflowStatus.Discarded, WorkflowStatus.Ready)]
    public async Task RegisterProofDecidesWhetherAFreshRunCanEnterTheSameInvoice(WorkflowStatus proof, WorkflowStatus expected)
    {
        using var directory = new TempDirectory();
        var (runner, _, native, old) = await SeedAsync(directory, WorkflowStatus.SaveUncertain);
        await runner.AbandonStoppedSessionAsync(Token);
        native.Proof = new(true, proof, "register", "complete register proof");

        var next = await runner.StartAsync(old.Invoice, old.CompanyIdentity, old.WindowIdentity, Token);

        Assert.Equal(expected, next.Snapshot.Status);
        Assert.NotEqual(old.Snapshot.RunId, next.Snapshot.RunId);
        Assert.Equal(proof == WorkflowStatus.Saved ? WorkflowRunner.ExistingInvoiceCode : null, next.Snapshot.LastErrorCode);
        Assert.Equal([WorkflowRunner.ResolveStaleAction], native.Actions);
        var journal = (await runner.ReadHistoryAsync(Token)).Single(run => run.Snapshot.RunId == old.Snapshot.RunId);
        Assert.True(journal.SessionDetached);
        Assert.Equal(proof, journal.Snapshot.Status);
    }

    // "Tạm dừng" (Paused) is the user's case: a run stopped in an earlier MISA session. The refusal sends nothing, so a
    // batch may skip the invoice and go on instead of stopping.
    [Theory]
    [InlineData(WorkflowStatus.SaveUncertain)]
    [InlineData(WorkflowStatus.Paused)]
    public async Task SameUncertainInvoiceCannotBeReplayedInAnotherMisaSession(WorkflowStatus status)
    {
        using var directory = new TempDirectory();
        var (runner, _, native, old) = await SeedAsync(directory, status);
        await runner.AbandonStoppedSessionAsync(Token);

        var error = await Assert.ThrowsAsync<AutomationException>(() => runner.StartAsync(old.Invoice, old.CompanyIdentity, "other-window", Token));

        Assert.Empty(native.Actions);
        Assert.Null(runner.Current);
        Assert.True(WorkflowRunner.RefusedBeforeStart(error));
    }

    [Fact]
    public void OnlyARefusalWithoutInputLetsABatchSkipTheInvoice()
    {
        Assert.False(WorkflowRunner.RefusedBeforeStart(new AutomationException("DETACHED_INVOICE_UNCERTAIN", "x", inputMayHaveBeenSent: true)));
        Assert.False(WorkflowRunner.RefusedBeforeStart(new AutomationException("EXISTING_EDITOR", "x")));
        Assert.False(WorkflowRunner.RefusedBeforeStart(new InvalidOperationException("Hóa đơn 81 của lần chạy trước chưa xác định được kết quả")));
    }

    private static async Task<(WorkflowRunner Runner, JsonStateStore Store, Native Native, WorkflowContext Old)> SeedAsync(
        TempDirectory directory, WorkflowStatus status)
    {
        var store = new JsonStateStore(directory.Combine("state"));
        var old = WorkflowFixtures.Context(InvoiceBuilder.Valid(), WorkflowFixtures.NewRunId(), status);
        old = old with
        {
            NativeValues = ImmutableDictionary<string, string>.Empty.Add("voucher_number", "BH_1"),
            Snapshot = old.Snapshot with
            {
                PausedFromStatus = WorkflowStatus.AwaitingSave,
                LastInputDisposition = InputDisposition.Uncertain,
                InputMayHaveBeenSent = true
            }
        };
        await store.WriteAsync("workflow-session", "session.json", old, Token);
        var native = new Native();
        var runner = new WorkflowRunner(native, store, new RunEventStore(directory.Combine("logs")));
        await runner.InitializeAsync(Token);
        return (runner, store, native, old);
    }

    private sealed class Native : IWorkflowNativeGateway
    {
        public List<string> Actions { get; } = [];
        public WorkflowObservation Proof { get; set; } = new(false, WorkflowStatus.SaveUncertain, "", "still unknown");
        public Task<WorkflowObservation> ExecuteAsync(WorkflowContext context, WorkflowTransition transition,
            Func<WorkflowProgress, CancellationToken, Task> progress, CancellationToken token)
        {
            Actions.Add(transition.Action);
            return Task.FromResult(Proof);
        }
        public Task<WorkflowObservation> ObserveAsync(WorkflowContext context, CancellationToken token)
            => throw new InvalidOperationException("Detached checkpoints cannot resume.");
        public Task<ReconciliationResult> ReconcileAsync(WorkflowContext context, CancellationToken token)
            => throw new InvalidOperationException("Detached checkpoints cannot reconcile.");
    }

    private sealed class FailingStore(JsonStateStore state) : IStateStore
    {
        public bool FailDetachment { get; set; }
        public string RootPath => state.RootPath;
        public Task<T?> ReadAsync<T>(string documentType, string path, CancellationToken token = default)
            => state.ReadAsync<T>(documentType, path, token);
        public Task WriteAsync<T>(string documentType, string path, T value, CancellationToken token = default)
        {
            // Fail the session marker after the journal was written successfully.
            if (FailDetachment && documentType == "workflow-session" && value is WorkflowContext { SessionDetached: true })
                throw new IOException("session write failed");
            return state.WriteAsync(documentType, path, value, token);
        }
    }
}


