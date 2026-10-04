using System.Text.Json;
using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class RunEventStoreTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly InvoiceDocument invoice = InvoiceBuilder.Valid();

    public void Dispose() => temp.Dispose();

    private string StateRoot => temp.Combine("state");
    private string LogRoot => temp.Combine("state", "logs");

    private RunEventStore NewStore() => new(LogRoot);

    private string EventsPath(string runId) => Path.Combine(LogRoot, "events-" + runId + ".jsonl");

    private static RunEvent[] ReadEvents(string path)
        => File.ReadAllLines(path).Where(line => line.Trim().Length > 0)
            .Select(line => JsonSerializer.Deserialize<RunEvent>(line, ContractJson.Options)!).ToArray();

    private void Append(RunEventStore store, string runId, WorkflowStatus status = WorkflowStatus.Ready, string? message = null)
        => store.Append(WorkflowFixtures.Context(invoice, runId, status, message));

    // ---- append ----

    [Fact]
    public void AppendWritesOneJsonLinePerCheckpointInsideTheLogRoot()
    {
        var store = NewStore();
        var run = WorkflowFixtures.NewRunId();

        Append(store, run, WorkflowStatus.Ready);
        Append(store, run, WorkflowStatus.VoucherCreated, "Đã tạo chứng từ");

        var events = ReadEvents(EventsPath(run));
        Assert.Equal(2, events.Length);
        Assert.Equal([WorkflowStatus.Ready, WorkflowStatus.VoucherCreated], events.Select(item => item.Status));
        Assert.Equal("Đã tạo chứng từ", events[1].Message);
        Assert.Equal(Path.GetFullPath(LogRoot), store.RootPath);
    }

    [Fact]
    public void EventsCarryInvoiceIdentityAndSourceEpoch()
    {
        var store = NewStore();
        var run = WorkflowFixtures.NewRunId();

        Append(store, run);

        var record = Assert.Single(ReadEvents(EventsPath(run)));
        Assert.Equal(run, record.RunId);
        Assert.Equal(invoice.InvoiceId, record.InvoiceId);
        Assert.Equal(invoice.Revision, record.InvoiceRevision);
        Assert.Equal(invoice.Source.Sha256, record.SourceEpoch);
        Assert.Equal(ProductIdentity.StateSchemaVersion, record.SchemaVersion);
        Assert.Equal("Checkpoint persisted.", record.Message);
        Assert.InRange(record.OccurredAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void SequenceNumbersIncreaseAcrossRuns()
    {
        var store = NewStore();
        var first = WorkflowFixtures.NewRunId();
        var second = WorkflowFixtures.NewRunId();

        Append(store, first);
        Append(store, second);
        Append(store, first);

        Assert.Equal([1L, 3L], ReadEvents(EventsPath(first)).Select(item => item.Sequence));
        Assert.Equal([2L], ReadEvents(EventsPath(second)).Select(item => item.Sequence));
    }

    [Fact]
    public void SequenceContinuesAfterARestart()
    {
        var run = WorkflowFixtures.NewRunId();
        var first = NewStore();
        Append(first, run);
        Append(first, run);

        var restarted = NewStore();
        Append(restarted, run);

        Assert.Equal([1L, 2L, 3L], ReadEvents(EventsPath(run)).Select(item => item.Sequence));
    }

    [Fact]
    public void SequenceIsNeverReusedEvenWhenTheEventFileIsGone()
    {
        var run = WorkflowFixtures.NewRunId();
        var first = NewStore();
        Append(first, run);
        Append(first, run);
        File.Delete(EventsPath(run));

        Append(NewStore(), run);

        Assert.Equal([3L], ReadEvents(EventsPath(run)).Select(item => item.Sequence));
    }

    [Fact]
    public void ConcurrentAppendsGetUniqueSequenceNumbers()
    {
        var store = NewStore();
        var run = WorkflowFixtures.NewRunId();

        Parallel.For(0, 20, _ => Append(store, run));

        var sequences = ReadEvents(EventsPath(run)).Select(item => item.Sequence).ToArray();
        Assert.Equal(Enumerable.Range(1, 20).Select(number => (long)number), sequences.Order());
    }

    [Fact]
    public void TornLastLineDoesNotCorruptTheNextRecord()
    {
        var run = WorkflowFixtures.NewRunId();
        var store = NewStore();
        Append(store, run);
        File.AppendAllText(EventsPath(run), "{\"runId\":\"" + run + "\",\"sequ"); // a crash mid-append

        var restarted = NewStore();
        Append(restarted, run);

        var lines = File.ReadAllLines(EventsPath(run));
        Assert.Equal(3, lines.Length);
        Assert.Equal(2L, JsonSerializer.Deserialize<RunEvent>(lines[2], ContractJson.Options)!.Sequence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcde")]
    public void InvalidRunIdsAreRejectedWithoutWritingAnything(string runId)
    {
        var store = NewStore();

        Assert.Throws<InvalidDataException>(() => Append(store, runId));

        Assert.False(Directory.Exists(LogRoot) && Directory.GetFiles(LogRoot).Length > 0);
    }

    [Fact]
    public void NegativeSequenceWatermarkIsTreatedAsCorruptAudit()
    {
        Directory.CreateDirectory(LogRoot);
        File.WriteAllText(Path.Combine(LogRoot, "sequence.json"), "-5");

        Assert.Throws<InvalidDataException>(() => NewStore());
    }

    // ---- protection and pruning ----

    [Fact]
    public void NonEventFilesAreProtectedExceptNativeTabSnapshots()
    {
        var store = NewStore();

        Assert.True(store.IsProtectedFile(Path.Combine(LogRoot, "sequence.json")));
        Assert.True(store.IsProtectedFile(Path.Combine(LogRoot, "screenshot-20261004.png")));
        Assert.False(store.IsProtectedFile(Path.Combine(LogRoot, "native-tab-1.json")));
        Assert.True(store.IsProtectedFile(Path.Combine(LogRoot, "native-tab-1.txt")));
    }

    [Theory]
    [InlineData("saved")]
    [InlineData("discarded")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public void EventFileOfAFinishedRunIsNotProtected(string status)
    {
        var store = NewStore();
        var run = WorkflowFixtures.NewRunId();
        Append(store, run, WorkflowStatus.Saved);
        WorkflowFixtures.WriteCheckpoint(StateRoot, run, status);

        Assert.False(store.IsProtectedFile(EventsPath(run)));
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("awaiting_save")]
    [InlineData("save_uncertain")]
    [InlineData("input_uncertain")]
    [InlineData("ready")]
    public void EventFileOfAnUnfinishedRunIsProtected(string status)
    {
        var store = NewStore();
        var run = WorkflowFixtures.NewRunId();
        Append(store, run);
        WorkflowFixtures.WriteCheckpoint(StateRoot, run, status);

        Assert.True(store.IsProtectedFile(EventsPath(run)));
    }

    [Fact]
    public void FinishedRunWithInputStillPendingIsProtected()
    {
        var store = NewStore();
        var run = WorkflowFixtures.NewRunId();
        Append(store, run);
        WorkflowFixtures.WriteCheckpoint(StateRoot, run, "failed", inputPending: true);

        Assert.True(store.IsProtectedFile(EventsPath(run)));
    }

    [Fact]
    public void EventFileWithoutAnyCheckpointIsProtected()
    {
        var store = NewStore();
        var run = WorkflowFixtures.NewRunId();
        Append(store, run);

        Assert.True(store.IsProtectedFile(EventsPath(run)));
    }

    [Fact]
    public void CheckpointWithAnotherSchemaVersionKeepsTheAuditProtected()
    {
        var store = NewStore();
        var run = WorkflowFixtures.NewRunId();
        Append(store, run);
        WorkflowFixtures.WriteCheckpoint(StateRoot, run, "saved", schemaVersion: 2);

        Assert.True(store.IsProtectedFile(EventsPath(run)));
    }

    [Fact]
    public void UnreadableOrEmptyEventFilesAreProtected()
    {
        var store = NewStore();
        var garbage = temp.WriteText("state/logs/events-garbage.jsonl", "not json at all\n");
        var empty = temp.WriteText("state/logs/events-empty.jsonl", "");

        Assert.True(store.IsProtectedFile(garbage));
        Assert.True(store.IsProtectedFile(empty));
    }

    [Fact]
    public void PruneRemovesOldFinishedAuditsAndKeepsEverythingUnresolved()
    {
        var store = NewStore();
        var finished = WorkflowFixtures.NewRunId();
        var unfinished = WorkflowFixtures.NewRunId();
        var recent = WorkflowFixtures.NewRunId();
        Append(store, finished, WorkflowStatus.Saved);
        Append(store, unfinished, WorkflowStatus.Paused);
        Append(store, recent, WorkflowStatus.Saved);
        WorkflowFixtures.WriteCheckpoint(StateRoot, finished, "saved");
        WorkflowFixtures.WriteCheckpoint(StateRoot, unfinished, "paused");
        WorkflowFixtures.WriteCheckpoint(StateRoot, recent, "saved");
        var oldSnapshot = temp.WriteText("state/logs/native-tab-1.json", "{}");
        foreach (var path in new[] { EventsPath(finished), EventsPath(unfinished), oldSnapshot })
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60));

        var report = store.Prune();

        Assert.False(File.Exists(EventsPath(finished)));
        Assert.False(File.Exists(oldSnapshot));
        Assert.True(File.Exists(EventsPath(unfinished)));
        Assert.True(File.Exists(EventsPath(recent)));
        Assert.True(File.Exists(Path.Combine(LogRoot, "sequence.json")));
        Assert.Equal(2, report.DeletedCount);
    }

    [Fact]
    public void PruneOnAnEmptyStoreDoesNothing()
    {
        var report = NewStore().Prune();

        Assert.Equal(0, report.DeletedCount);
        Assert.Equal(0, report.FileCount);
    }
}
