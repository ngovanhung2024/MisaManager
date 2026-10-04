using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class StateStoreTests : IDisposable
{
    private sealed record Sample(string Name, decimal Amount, DateOnly? Day = null);

    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- JsonStateStore ----

    [Fact]
    public async Task WrittenDocumentIsReadBackWithExactDecimals()
    {
        var store = new JsonStateStore(temp.Root);
        var sample = new Sample("Phân Ure Đạm Phú Mỹ loại 1 (bao)", 1542857.250m, new DateOnly(2026, 10, 4));

        await store.WriteAsync("sample", "sample.json", sample, Token);

        Assert.Equal(sample, await store.ReadAsync<Sample>("sample", "sample.json", Token));
        Assert.Equal("1542857.250", (await store.ReadAsync<Sample>("sample", "sample.json", Token))!.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ReadingAMissingDocumentReturnsDefault()
        => Assert.Null(await new JsonStateStore(temp.Root).ReadAsync<Sample>("sample", "nothing.json", Token));

    [Fact]
    public async Task EnvelopeCarriesTypeSchemaAndVersions()
    {
        var store = new JsonStateStore(temp.Root);
        await store.WriteAsync("sample", "runs/abc/state.json", new Sample("a", 1m), Token);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("runs", "abc", "state.json"), Token));
        var root = document.RootElement;

        Assert.Equal("sample", root.GetProperty("documentType").GetString());
        Assert.Equal(ProductIdentity.StateSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("appVersion").GetString()));
        Assert.Equal("1", root.GetProperty("data").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task OverwritingKeepsCreatedAtAndAdvancesUpdatedAt()
    {
        var store = new JsonStateStore(temp.Root);
        await store.WriteAsync("sample", "sample.json", new Sample("a", 1m), Token);
        var first = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("sample.json"), Token)).RootElement.Clone();

        await Task.Delay(20, Token);
        await store.WriteAsync("sample", "sample.json", new Sample("b", 2m), Token);
        var second = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("sample.json"), Token)).RootElement;

        Assert.Equal(first.GetProperty("createdAt").GetDateTimeOffset(), second.GetProperty("createdAt").GetDateTimeOffset());
        Assert.True(second.GetProperty("updatedAt").GetDateTimeOffset() > first.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal("b", (await store.ReadAsync<Sample>("sample", "sample.json", Token))!.Name);
    }

    [Fact]
    public async Task WritingLeavesNoTemporaryFiles()
    {
        var store = new JsonStateStore(temp.Root);

        for (var index = 0; index < 5; index++) await store.WriteAsync("sample", "sample.json", new Sample("a", index), Token);

        Assert.Equal([temp.Combine("sample.json")], Directory.GetFiles(temp.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConcurrentWritersNeverCorruptTheDocument()
    {
        var store = new JsonStateStore(temp.Root);

        await Task.WhenAll(Enumerable.Range(0, 25).Select(index => Task.Run(() => store.WriteAsync("sample", "sample.json", new Sample("n" + index, index), Token), Token)));

        var last = await store.ReadAsync<Sample>("sample", "sample.json", Token);
        Assert.StartsWith("n", last!.Name);
        Assert.Empty(Directory.GetFiles(temp.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReadingWithTheWrongDocumentTypeIsRejected()
    {
        var store = new JsonStateStore(temp.Root);
        await store.WriteAsync("sample", "sample.json", new Sample("a", 1m), Token);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync<Sample>("other", "sample.json", Token));
    }

    [Fact]
    public async Task OverwritingADocumentOfAnotherTypeIsRefused()
    {
        var store = new JsonStateStore(temp.Root);
        await store.WriteAsync("sample", "sample.json", new Sample("a", 1m), Token);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync("other", "sample.json", new Sample("b", 2m), Token));
        Assert.Equal("a", (await store.ReadAsync<Sample>("sample", "sample.json", Token))!.Name);
    }

    [Fact]
    public async Task CorruptJsonIsReportedAsCorruptAndNeverOverwritten()
    {
        var path = temp.WriteText("bad.json", "{ \"documentType\": ");
        var store = new JsonStateStore(temp.Root);

        var read = await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync<Sample>("sample", "bad.json", Token));
        await Assert.ThrowsAnyAsync<Exception>(() => store.WriteAsync("sample", "bad.json", new Sample("a", 1m), Token));

        Assert.Contains("corrupt", read.Message);
        Assert.Equal("{ \"documentType\": ", await File.ReadAllTextAsync(path, Token));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public async Task UnsupportedSchemaVersionsAreRejected(int version)
    {
        temp.WriteText("old.json", $"{{\"documentType\":\"sample\",\"schemaVersion\":{version},\"appVersion\":\"0.1\",\"createdAt\":\"2026-10-04T00:00:00+00:00\",\"updatedAt\":\"2026-10-04T00:00:00+00:00\",\"data\":{{\"name\":\"a\",\"amount\":\"1\"}}}}");

        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonStateStore(temp.Root).ReadAsync<Sample>("sample", "old.json", Token));
    }

    [Fact]
    public async Task UpdatedBeforeCreatedIsRejected()
    {
        temp.WriteText("time.json", "{\"documentType\":\"sample\",\"schemaVersion\":1,\"appVersion\":\"0.1\",\"createdAt\":\"2026-10-05T00:00:00+00:00\",\"updatedAt\":\"2026-10-04T00:00:00+00:00\",\"data\":{\"name\":\"a\",\"amount\":\"1\"}}");

        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonStateStore(temp.Root).ReadAsync<Sample>("sample", "time.json", Token));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("a/../../outside.json")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PathsOutsideTheRootAreRejected(string relative)
    {
        var store = new JsonStateStore(temp.Combine("state"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("sample", relative, new Sample("a", 1m), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync<Sample>("sample", relative, Token));
        Assert.False(File.Exists(temp.Combine("outside.json")));
    }

    [Fact]
    public async Task RootedPathsAreRejected()
    {
        var store = new JsonStateStore(temp.Combine("state"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("sample", temp.Combine("elsewhere.json"), new Sample("a", 1m), Token));
    }

    [Fact]
    public async Task AlreadyCancelledTokenAbortsTheWrite()
    {
        var store = new JsonStateStore(temp.Root);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync("sample", "sample.json", new Sample("a", 1m), cancelled.Token));
        Assert.False(File.Exists(temp.Combine("sample.json")));
    }

    [Fact]
    public void RootPathIsNormalised()
        => Assert.Equal(Path.GetFullPath(temp.Combine("a", "..", "state")), new JsonStateStore(temp.Combine("a", "..", "state")).RootPath);

    // ---- FileDigest ----

    [Fact]
    public async Task FileDigestIsLowerCaseSha256OfTheContent()
    {
        var path = temp.WriteText("hash.txt", "Hóa đơn 1000");
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, Token))).ToLowerInvariant();

        Assert.Equal(expected, await FileDigest.Sha256Async(path, Token));
    }

    [Fact]
    public async Task FileDigestOfEmptyFileIsTheEmptyHash()
    {
        var path = temp.WriteText("empty.txt", "");

        Assert.Equal(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), await FileDigest.Sha256Async(path, Token));
    }

    // ---- StateFiles ----

    [Fact]
    public void GenerationPathAcceptsTimestampedIds()
    {
        var path = StateFiles.GenerationPath(temp.Root, "20261004-101530-0a1b2c3d");

        Assert.Equal(Path.Combine(Path.GetFullPath(temp.Root), "20261004-101530-0a1b2c3d"), path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-10-04")]
    [InlineData("20261004-101530-0A1B2C3D")]
    [InlineData("20261004-101530-0a1b2c3")]
    [InlineData("../20261004-101530-0a1b2c3d")]
    [InlineData("20261004-101530-0a1b2c3d/x")]
    public void GenerationPathRejectsMalformedIds(string id)
        => Assert.Throws<InvalidDataException>(() => StateFiles.GenerationPath(temp.Root, id));

    [Theory]
    [InlineData("runs/abc/workflow.json")]
    [InlineData("session.json")]
    [InlineData("runs\\abc\\workflow.json")]
    [InlineData("native-tab-1.json")]
    public void UnderRootAcceptsPlainRelativePaths(string relative)
        => Assert.StartsWith(Path.GetFullPath(temp.Root), StateFiles.UnderRoot(temp.Root, relative));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("..")]
    [InlineData("../x.json")]
    [InlineData("a/../b.json")]
    [InlineData("a\\..\\b.json")]
    [InlineData("./x.json")]
    [InlineData("a//b.json")]
    [InlineData("C:evil.json")]
    [InlineData("a/b:stream")]
    [InlineData("/etc/passwd")]
    public void UnderRootRejectsEscapesAndOddPaths(string relative)
        => Assert.Throws<InvalidDataException>(() => StateFiles.UnderRoot(temp.Root, relative));

    [Fact]
    public void WriteAtomicCreatesDirectoriesAndWritesContractJson()
    {
        var path = temp.Combine("a", "b", "value.json");

        StateFiles.WriteAtomic(path, new Sample("Hesman 60WG 18g", 608000m));

        var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal("608000", json.GetProperty("amount").GetString());
        Assert.Equal("Hesman 60WG 18g", json.GetProperty("name").GetString());
    }

    [Fact]
    public void WriteAtomicReplacesExistingContentWithoutLeavingTemporaryFiles()
    {
        var path = temp.Combine("value.json");

        StateFiles.WriteAtomic(path, new Sample("a", 1m));
        StateFiles.WriteAtomic(path, new Sample("b", 2m));

        Assert.Equal("b", JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("name").GetString());
        Assert.Equal([path], Directory.GetFiles(temp.Root));
    }

    [Fact]
    public void WriteAtomicWritesPlainNumbersForPrimitives()
    {
        var path = temp.Combine("sequence.json");

        StateFiles.WriteAtomic(path, 42L);

        Assert.Equal(42L, JsonSerializer.Deserialize<long>(File.ReadAllText(path)));
    }

    [Fact]
    public void FilesEnumeratesNestedFilesOnly()
    {
        temp.WriteText("a.json", "1");
        temp.WriteText("sub/b.json", "2");
        temp.WriteText("sub/deeper/c.json", "3");

        var names = StateFiles.Files(temp.Root).Select(path => Path.GetFileName(path)!).Order().ToArray();

        Assert.Equal(["a.json", "b.json", "c.json"], names);
    }

    [Fact]
    public void AbandonedTempFilesAreOnlyOldTmpFiles()
    {
        var old = temp.WriteText("x.json.tmp", "1");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));
        var fresh = temp.WriteText("y.json.tmp", "1");
        var oldJson = temp.WriteText("z.json", "1");
        File.SetLastWriteTimeUtc(oldJson, DateTime.UtcNow.AddMinutes(-5));

        Assert.True(StateFiles.IsAbandonedTemp(old));
        Assert.False(StateFiles.IsAbandonedTemp(fresh));
        Assert.False(StateFiles.IsAbandonedTemp(oldJson));
    }

    [Fact]
    public void RejectReparseAcceptsOrdinaryAndMissingPaths()
    {
        StateFiles.RejectReparse(temp.WriteText("ok.json", "1"));
        StateFiles.RejectReparse(temp.Combine("not-there", "yet.json"));
    }

    [Fact]
    public void RejectReparseRefusesSymbolicLinks()
    {
        var target = Directory.CreateDirectory(temp.Combine("target")).FullName;
        var link = temp.Combine("link");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symbolic links is not permitted on this machine: " + error.Message);
        }

        Assert.Throws<InvalidDataException>(() => StateFiles.RejectReparse(Path.Combine(link, "state.json")));
        Assert.Throws<InvalidDataException>(() => StateFiles.UnderRoot(temp.Root, "link/state.json"));
        Assert.Throws<InvalidDataException>(() => StateFiles.Files(link).ToArray());
    }

    // ---- CommandOutputPath ----

    [Fact]
    public void NoOutputMeansStandardOutput() => Assert.Null(CommandOutputPath.Resolve(null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOutputIsRejected(string output)
        => Assert.Throws<ArgumentException>(() => CommandOutputPath.Resolve(output));

    [Theory]
    [InlineData("report.txt")]
    [InlineData("report")]
    public void OnlyJsonOutputIsAccepted(string name)
        => Assert.Throws<ArgumentException>(() => CommandOutputPath.Resolve(temp.Combine(name)));

    [Fact]
    public void JsonOutputIsResolvedToAFullPath()
    {
        var resolved = CommandOutputPath.Resolve(temp.Combine("out", "..", "REPORT.JSON"));

        Assert.Equal(Path.GetFullPath(temp.Combine("REPORT.JSON")), resolved);
    }

    [Fact]
    public void OutputInsideTheSolutionRootIsRefused()
    {
        temp.WriteText("MisaManager.sln", "");

        var error = Assert.Throws<ArgumentException>(() => CommandOutputPath.Resolve(temp.Combine("result.json")));

        Assert.Contains("project root", error.Message);
        Assert.NotNull(CommandOutputPath.Resolve(temp.Combine("scratch", "result.json")));
    }

    [Fact]
    public void AutomationExceptionCarriesCodeAndInputFlag()
    {
        var inner = new InvalidOperationException("x");
        var error = new AutomationException("FOCUS_LOST", "Mất focus", inputMayHaveBeenSent: true, inner);

        Assert.Equal("FOCUS_LOST", error.Code);
        Assert.True(error.InputMayHaveBeenSent);
        Assert.Same(inner, error.InnerException);
        Assert.Equal("Mất focus", error.Message);
        Assert.Equal(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(error.Message)), error.Message);
    }
}
