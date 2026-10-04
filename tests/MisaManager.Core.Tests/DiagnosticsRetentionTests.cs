using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class DiagnosticsRetentionTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private string Write(string relative, int bytes, int ageDays = 0)
    {
        var path = temp.Combine(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    [Fact]
    public void MissingFolderIsReportedAsEmpty()
    {
        var report = DiagnosticsRetention.Inspect(temp.Combine("nothing"));

        Assert.Equal(0, report.FileCount);
        Assert.Equal(0, report.TotalBytes);
        Assert.False(report.OverQuota);
        Assert.Contains("chưa tồn tại", report.Message);
    }

    [Fact]
    public void InspectCountsFilesAndBytesRecursively()
    {
        Write("a.log", 100);
        Write("shots/b.png", 250);
        Write("shots/deeper/c.png", 50);

        var report = DiagnosticsRetention.Inspect(temp.Root);

        Assert.Equal(3, report.FileCount);
        Assert.Equal(400, report.TotalBytes);
        Assert.False(report.OverQuota);
        Assert.All(report.Entries, entry => Assert.False(entry.Protected));
    }

    [Fact]
    public void InspectFlagsProtectedFilesFromThePredicate()
    {
        var keep = Write("events-keep.jsonl", 10);
        Write("other.log", 10);

        var report = DiagnosticsRetention.Inspect(temp.Root, path => path == keep);

        Assert.Equal([keep], report.Entries.Where(entry => entry.Protected).Select(entry => entry.Path));
    }

    [Fact]
    public void InspectReportsOverQuotaWithoutDeletingAnything()
    {
        Write("a.log", 600);
        Write("b.log", 600);

        var report = DiagnosticsRetention.Inspect(temp.Root, quotaBytes: 1000);

        Assert.True(report.OverQuota);
        Assert.Equal(0, report.DeletedCount);
        Assert.Equal(2, Directory.GetFiles(temp.Root).Length);
    }

    [Fact]
    public void PruneDeletesOldUnprotectedFilesEvenUnderQuota()
    {
        var old = Write("old.log", 10, ageDays: 45);
        var fresh = Write("fresh.log", 10, ageDays: 1);

        var report = DiagnosticsRetention.Prune(temp.Root);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.Equal(1, report.DeletedCount);
        Assert.Equal(10, report.DeletedBytes);
        Assert.Equal(1, report.FileCount);
        Assert.Contains("Đã dọn 1 file", report.Message);
    }

    [Fact]
    public void PruneNeverDeletesProtectedFilesNoMatterHowOld()
    {
        var keep = Write("events-open.jsonl", 10, ageDays: 400);
        var drop = Write("screenshot.png", 10, ageDays: 400);

        DiagnosticsRetention.Prune(temp.Root, path => path == keep);

        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(drop));
    }

    [Fact]
    public void PruneUsesTheGivenMaximumAge()
    {
        var recent = Write("recent.log", 10, ageDays: 3);
        var older = Write("older.log", 10, ageDays: 10);

        DiagnosticsRetention.Prune(temp.Root, maximumAge: TimeSpan.FromDays(7));

        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(older));
    }

    [Fact]
    public void PruneOverQuotaDeletesOldestFirstUntilUnderQuota()
    {
        var oldest = Write("1-oldest.log", 400, ageDays: 5);
        var middle = Write("2-middle.log", 400, ageDays: 3);
        var newest = Write("3-newest.log", 400, ageDays: 1);

        var report = DiagnosticsRetention.Prune(temp.Root, quotaBytes: 900);

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
        Assert.Equal(1, report.DeletedCount);
        Assert.Equal(400, report.DeletedBytes);
        Assert.False(report.OverQuota);
    }

    [Fact]
    public void PruneStopsAtProtectedFilesEvenWhenStillOverQuota()
    {
        var keepA = Write("events-a.jsonl", 600, ageDays: 5);
        var keepB = Write("events-b.jsonl", 600, ageDays: 4);
        var other = Write("c.log", 100, ageDays: 1);

        var report = DiagnosticsRetention.Prune(temp.Root, path => Path.GetFileName(path).StartsWith("events-", StringComparison.Ordinal), quotaBytes: 500);

        Assert.True(File.Exists(keepA));
        Assert.True(File.Exists(keepB));
        Assert.False(File.Exists(other));
        Assert.True(report.OverQuota);
    }

    [Fact]
    public void PruneReChecksProtectionJustBeforeDeleting()
    {
        var path = Write("flip.log", 10, ageDays: 90);
        var calls = 0;

        DiagnosticsRetention.Prune(temp.Root, _ => ++calls > 1);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PruneOfAMissingFolderIsANoOp()
    {
        var report = DiagnosticsRetention.Prune(temp.Combine("nothing"));

        Assert.Equal(0, report.DeletedCount);
        Assert.Equal(0, report.FileCount);
    }

    [Fact]
    public void DefaultsAreThirtyDaysAndHalfAGibibyte()
    {
        Assert.Equal(TimeSpan.FromDays(30), DiagnosticsRetention.DefaultAge);
        Assert.Equal(512L * 1024 * 1024, DiagnosticsRetention.DefaultQuotaBytes);
    }
}
