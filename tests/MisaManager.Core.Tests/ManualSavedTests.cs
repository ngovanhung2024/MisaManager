using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class ManualSavedTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => directory.Dispose();

    private static InvoiceDocument Invoice(string number)
    {
        var invoice = new InvoiceBuilder().WithNumber(number).Build();
        invoice = invoice with { InvoiceId = "invoice" + number };
        return invoice with { Fingerprint = InvoiceFingerprint.Compute(invoice) };
    }

    private (WorkflowRunner Runner, JsonStateStore Store) Create()
    {
        var store = new JsonStateStore(directory.Combine("state"));
        return (new WorkflowRunner(new NoNative(), store, new RunEventStore(directory.Combine("logs"))), store);
    }

    private static async Task<WorkflowContext> SeedRunAsync(JsonStateStore store, InvoiceDocument invoice, WorkflowStatus status,
        string? errorCode = null, bool session = false)
    {
        var run = WorkflowFixtures.Context(invoice, WorkflowFixtures.NewRunId(), status);
        run = run with { Snapshot = run.Snapshot with { LastErrorCode = errorCode } };
        await store.WriteAsync("workflow-context", Path.Combine("runs", run.Snapshot.RunId, "workflow.json"), run, Token);
        await store.WriteAsync("reconciliation", Path.Combine("reconciliation", "runs", run.Snapshot.RunId, "readbacks.json"), "x", Token);
        await store.WriteAsync("live-verification", Path.Combine("verification", "runs", run.Snapshot.RunId + ".json"), "x", Token);
        if (session) await store.WriteAsync("workflow-session", "session.json", run, Token);
        return run;
    }

    private static string RunDirectory(JsonStateStore store, WorkflowContext run) => Path.Combine(store.RootPath, "runs", run.Snapshot.RunId);

    [Fact]
    public async Task MarkingReplacesOldRunsWithOneSavedRecordThatSurvivesRestart()
    {
        var (runner, store) = Create();
        var invoice = Invoice("1544");
        var uncertain = await SeedRunAsync(store, invoice, WorkflowStatus.SaveUncertain);
        var unfinished = await SeedRunAsync(store, invoice, WorkflowStatus.Failed, BuyerCodes.MissingCode);

        var marked = await runner.MarkSavedManuallyAsync(invoice, Token);

        Assert.Equal(WorkflowStatus.Saved, marked.Snapshot.Status);
        Assert.Equal(WorkflowRunner.ManualSavedCode, marked.Snapshot.LastErrorCode);
        Assert.True(WorkflowRunner.IsManualSaved(marked.Snapshot));
        Assert.False(Directory.Exists(RunDirectory(store, uncertain)));
        Assert.False(Directory.Exists(RunDirectory(store, unfinished)));
        Assert.False(File.Exists(Path.Combine(store.RootPath, "reconciliation", "runs", uncertain.Snapshot.RunId, "readbacks.json")));
        Assert.False(File.Exists(Path.Combine(store.RootPath, "verification", "runs", unfinished.Snapshot.RunId + ".json")));

        var restarted = new WorkflowRunner(new NoNative(), new JsonStateStore(store.RootPath), new RunEventStore(directory.Combine("logs")));
        var history = Assert.Single(await restarted.ReadHistoryAsync(Token));
        Assert.Equal(marked.Snapshot.RunId, history.Snapshot.RunId);
        Assert.True(WorkflowRunner.IsManualSavedFor(history, invoice));
        Assert.Equal(invoice.Fingerprint, history.Invoice.Fingerprint);
    }

    [Fact]
    public async Task MarkingKeepsEveryOtherInvoicesRecords()
    {
        var (runner, store) = Create();
        var mine = Invoice("1544");
        var other = Invoice("1545");
        await SeedRunAsync(store, mine, WorkflowStatus.InputUncertain);
        var otherSaved = await SeedRunAsync(store, other, WorkflowStatus.Saved);
        var otherUncertain = await SeedRunAsync(store, other, WorkflowStatus.SaveUncertain);

        await runner.MarkSavedManuallyAsync(mine, Token);

        var history = await runner.ReadHistoryAsync(Token);
        Assert.Equal(3, history.Count);
        Assert.Contains(history, run => run.Snapshot.RunId == otherSaved.Snapshot.RunId);
        Assert.Contains(history, run => run.Snapshot.RunId == otherUncertain.Snapshot.RunId);
        Assert.False(WorkflowRunner.IsManualSavedFor(otherSaved, mine));
        Assert.DoesNotContain(history, run => WorkflowRunner.IsManualSavedFor(run, other));
    }

    [Fact]
    public async Task MarkingClearsTheActiveSessionOfThatInvoiceSoNewRunsAreNotBlocked()
    {
        var (_, store) = Create();
        var invoice = Invoice("1544");
        var active = await SeedRunAsync(store, invoice, WorkflowStatus.SaveUncertain, session: true);
        var runner = new WorkflowRunner(new NoNative(), store, new RunEventStore(directory.Combine("logs")));
        await runner.InitializeAsync(Token);
        Assert.Equal(active.Snapshot.RunId, runner.Current!.Snapshot.RunId);

        await runner.MarkSavedManuallyAsync(invoice, Token);

        Assert.Null(runner.Current);
        Assert.False(File.Exists(Path.Combine(store.RootPath, "session.json")));
        var next = await runner.StartAsync(Invoice("1546"), "company", "window", Token);
        Assert.Equal(WorkflowStatus.Ready, next.Snapshot.Status);
        var restarted = new WorkflowRunner(new NoNative(), store, new RunEventStore(directory.Combine("logs")));
        await restarted.InitializeAsync(Token);
        Assert.Equal(next.Snapshot.RunId, restarted.Current!.Snapshot.RunId);
    }

    [Fact]
    public async Task MarkingClearsASessionThatWasNotLoadedYet()
    {
        var (runner, store) = Create();
        var invoice = Invoice("1544");
        await SeedRunAsync(store, invoice, WorkflowStatus.Paused, session: true);

        await runner.MarkSavedManuallyAsync(invoice, Token);

        Assert.Null(runner.Current);
        Assert.False(File.Exists(Path.Combine(store.RootPath, "session.json")));
    }

    [Fact]
    public async Task AnActiveSessionOfAnotherInvoiceIsLeftAlone()
    {
        var (_, store) = Create();
        var active = await SeedRunAsync(store, Invoice("1545"), WorkflowStatus.Paused, session: true);
        var runner = new WorkflowRunner(new NoNative(), store, new RunEventStore(directory.Combine("logs")));
        await runner.InitializeAsync(Token);

        await runner.MarkSavedManuallyAsync(Invoice("1544"), Token);

        Assert.Equal(active.Snapshot.RunId, runner.Current!.Snapshot.RunId);
        Assert.True(File.Exists(Path.Combine(store.RootPath, "session.json")));
    }

    [Fact]
    public async Task AMarkSurvivesAnEditOfTheInvoiceBecauseItFollowsTheInvoiceId()
    {
        var (runner, _) = Create();
        var invoice = Invoice("1544");
        var marked = await runner.MarkSavedManuallyAsync(invoice, Token);
        var edited = invoice with { Revision = 2, Notes = "sửa" };
        edited = edited with { Fingerprint = InvoiceFingerprint.Compute(edited) };

        Assert.NotEqual(invoice.Fingerprint, edited.Fingerprint);
        Assert.True(WorkflowRunner.IsManualSavedFor(marked, edited));
    }

    [Theory]
    [InlineData(WorkflowStatus.Saved, null)]
    [InlineData(WorkflowStatus.Discarded, WorkflowRunner.ExistingInvoiceCode)]
    public async Task AnInvoiceThatIsAlreadyDoneIsRefusedAndItsEvidenceKept(WorkflowStatus status, string? code)
    {
        var (runner, store) = Create();
        var invoice = Invoice("1544");
        var done = await SeedRunAsync(store, invoice, status, code);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MarkSavedManuallyAsync(invoice, Token));

        Assert.Equal(done.Snapshot.RunId, Assert.Single(await runner.ReadHistoryAsync(Token)).Snapshot.RunId);
    }

    [Fact]
    public async Task AnInvoiceWithoutAnyRunCanBeMarked()
    {
        var (runner, _) = Create();

        var marked = await runner.MarkSavedManuallyAsync(Invoice("1544"), Token);

        Assert.Equal(marked.Snapshot.RunId, Assert.Single(await runner.ReadHistoryAsync(Token)).Snapshot.RunId);
    }

    [Theory]
    [InlineData(WorkflowStatus.Saved, null)]
    [InlineData(WorkflowStatus.Saved, WorkflowRunner.ManualSavedCode)]
    [InlineData(WorkflowStatus.Discarded, WorkflowRunner.ExistingInvoiceCode)]
    public async Task ResetDeletesTheInvoicesRecordsAndKeepsOtherInvoices(WorkflowStatus status, string? code)
    {
        var (runner, store) = Create();
        var mine = Invoice("1544");
        var done = await SeedRunAsync(store, mine, status, code);
        var older = await SeedRunAsync(store, mine, WorkflowStatus.Failed);
        var other = await SeedRunAsync(store, Invoice("1545"), WorkflowStatus.Saved);

        Assert.Equal(2, await runner.ResetToReadyAsync(mine, Token));

        Assert.False(Directory.Exists(RunDirectory(store, done)));
        Assert.False(Directory.Exists(RunDirectory(store, older)));
        Assert.False(File.Exists(Path.Combine(store.RootPath, "verification", "runs", done.Snapshot.RunId + ".json")));
        Assert.Equal(other.Snapshot.RunId, Assert.Single(await runner.ReadHistoryAsync(Token)).Snapshot.RunId);
    }

    [Fact]
    public async Task ResetClearsAFinishedSessionOfTheInvoice()
    {
        var (_, store) = Create();
        var invoice = Invoice("1544");
        await SeedRunAsync(store, invoice, WorkflowStatus.Saved, session: true);
        var runner = new WorkflowRunner(new NoNative(), store, new RunEventStore(directory.Combine("logs")));
        await runner.InitializeAsync(Token);

        await runner.ResetToReadyAsync(invoice, Token);

        Assert.Null(runner.Current);
        Assert.False(File.Exists(Path.Combine(store.RootPath, "session.json")));
        Assert.Empty(await runner.ReadHistoryAsync(Token));
    }

    [Fact]
    public async Task ResetIsRefusedWhileTheInvoiceHasAnUnfinishedSession()
    {
        var (_, store) = Create();
        var invoice = Invoice("1544");
        var active = await SeedRunAsync(store, invoice, WorkflowStatus.SaveUncertain, session: true);
        var runner = new WorkflowRunner(new NoNative(), store, new RunEventStore(directory.Combine("logs")));
        await runner.InitializeAsync(Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ResetToReadyAsync(invoice, Token));

        Assert.Equal(active.Snapshot.RunId, runner.Current!.Snapshot.RunId);
        Assert.True(Directory.Exists(RunDirectory(store, active)));
    }

    private sealed class NoNative : IWorkflowNativeGateway
    {
        public Task<WorkflowObservation> ObserveAsync(WorkflowContext context, CancellationToken token) => throw new InvalidOperationException();
        public Task<WorkflowObservation> ExecuteAsync(WorkflowContext context, WorkflowTransition transition,
            Func<WorkflowProgress, CancellationToken, Task> progress, CancellationToken token) => throw new InvalidOperationException();
        public Task<ReconciliationResult> ReconcileAsync(WorkflowContext context, CancellationToken token) => throw new InvalidOperationException();
    }
}
