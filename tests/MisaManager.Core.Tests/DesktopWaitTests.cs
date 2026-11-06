using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

/// <summary>
/// VPS report 0.2.169, HĐ 81: Remote Desktop hidden/closed → Windows has no input desktop → every activation fails.
/// The runner waits for the desktop (no retry budget) but never past possibly-sent input.
/// </summary>
public sealed class DesktopWaitTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => directory.Dispose();

    private async Task<(WorkflowRunner Runner, WorkflowContext Started, InvoiceDocument Invoice)> StartAsync(Native native)
    {
        var path = directory.WriteText("hoa-don.xlsx", "source");
        var invoice = InvoiceBuilder.Valid();
        var source = invoice.Source;
        invoice = invoice with { Source = new SourceReference(path, await FileDigest.Sha256Async(path, Token), source.Worksheet,
            source.FirstRow, source.LastRow) };
        invoice = invoice with { Fingerprint = InvoiceFingerprint.Compute(invoice) };
        var runner = new WorkflowRunner(native, new JsonStateStore(directory.Combine("state")), new RunEventStore(directory.Combine("logs")))
            { DesktopWaitInterval = TimeSpan.FromMilliseconds(5) };
        native.Runner = runner;
        return (runner, await runner.StartAsync(invoice, "company", "window", Token), invoice);
    }

    [Fact]
    public async Task ADisconnectedSessionLongerThanTheRetryBudgetIsWaitedOut()
    {
        var native = new Native { Unavailable = InterferenceRetry.StepRetries + 5 };
        var (runner, started, invoice) = await StartAsync(native);

        var batch = await runner.RunUntilAsync(started.Snapshot.RunId, invoice.Fingerprint, WorkflowStatus.Ready,
            WorkflowStatus.PreflightPassed, 1, Token);

        Assert.Equal(WorkflowStatus.PreflightPassed, batch.Context.Snapshot.Status);
        Assert.Equal(InterferenceRetry.StepRetries + 6, native.Calls);
        Assert.False(batch.InputMayHaveBeenSent);
    }

    [Fact]
    public async Task InputThatMayHaveBeenSentIsNeverReplayedWhileWaiting()
    {
        var native = new Native { Unavailable = 3, Sent = true };
        var (runner, started, invoice) = await StartAsync(native);

        var batch = await runner.RunUntilAsync(started.Snapshot.RunId, invoice.Fingerprint, WorkflowStatus.Ready,
            WorkflowStatus.PreflightPassed, 1, Token);

        Assert.Equal(1, native.Calls);
        Assert.Equal(WorkflowStatus.InputUncertain, batch.Context.Snapshot.Status);
        Assert.Equal(InterferenceRetry.DesktopUnavailableCode, batch.Context.Snapshot.LastErrorCode);
    }

    [Fact]
    public async Task StopWhileWaitingPausesWithoutFailingTheInvoice()
    {
        var native = new Native { Unavailable = 100, StopOnCall = 2 };
        var (runner, started, invoice) = await StartAsync(native);

        var batch = await runner.RunUntilAsync(started.Snapshot.RunId, invoice.Fingerprint, WorkflowStatus.Ready,
            WorkflowStatus.PreflightPassed, 1, Token);

        Assert.Equal(2, native.Calls);
        Assert.Equal(WorkflowStatus.Paused, batch.Context.Snapshot.Status);
        Assert.Equal(WorkflowStatus.Ready, batch.Context.Snapshot.PausedFromStatus);
    }

    [Fact]
    public void DesktopUnavailableIsNotSpentAsInterference()
        => Assert.False(InterferenceRetry.IsTransient(InterferenceRetry.DesktopUnavailableCode));

    private sealed class Native : IWorkflowNativeGateway
    {
        public int Unavailable { get; init; }
        public bool Sent { get; init; }
        public int StopOnCall { get; init; }
        public int Calls { get; private set; }
        public WorkflowRunner? Runner { get; set; }

        public Task<WorkflowObservation> ExecuteAsync(WorkflowContext context, WorkflowTransition transition,
            Func<WorkflowProgress, CancellationToken, Task> progress, CancellationToken token)
        {
            Calls++;
            if (Calls == StopOnCall) Runner!.StopSignal.Request();
            return Task.FromResult(Calls <= Unavailable
                ? new WorkflowObservation(false, Sent ? WorkflowStatus.InputUncertain : WorkflowStatus.Blocked, "",
                    "Phiên Windows trên VPS đã ngắt kết nối.", Sent ? InputDisposition.Uncertain : InputDisposition.NotSent, Sent,
                    InterferenceRetry.DesktopUnavailableCode)
                : new WorkflowObservation(true, transition.To, "frame", "ok"));
        }

        public Task<WorkflowObservation> ObserveAsync(WorkflowContext context, CancellationToken token) => throw new InvalidOperationException();
        public Task<ReconciliationResult> ReconcileAsync(WorkflowContext context, CancellationToken token) => throw new InvalidOperationException();
    }
}
