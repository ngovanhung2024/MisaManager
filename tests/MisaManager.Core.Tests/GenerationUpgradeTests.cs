using MisaManager.Core.Tests.TestData;

namespace MisaManager.Core.Tests;

public sealed class GenerationUpgradeTests : IDisposable
{
    private readonly TempDirectory temp = new();
    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task UpgradeCopiesStateWithoutNeedingTheBlockedUiContext()
    {
        var store = new GenerationStore(temp.Root);
        var previous = store.EnsureActive("0.2.48.0");
        var state = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(state);
        var relative = Path.Combine("runs", "saved", "state.json");
        var source = Path.Combine(temp.Root, previous.GenerationId, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, state);
        var context = new BlockedUiContext();
        var upgrade = Task.Run(() =>
        {
            var original = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try { return store.EnsureActive("0.2.53.0"); }
            finally { SynchronizationContext.SetSynchronizationContext(original); }
        }, TestContext.Current.CancellationToken);

        GenerationPointer active;
        try { active = await upgrade.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
        finally
        {
            // Release queued callbacks on failure so the regression cannot leave file handles open.
            context.Release();
            await upgrade.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, context.PostCount);
        Assert.Equal("0.2.53.0", active.AppVersion);
        Assert.NotEqual(previous.GenerationId, active.GenerationId);
        Assert.Equal(active, store.ReadActive());
        Assert.Equal(state, File.ReadAllBytes(source));
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(temp.Root, active.GenerationId, relative)));
        Assert.Equal(active, store.EnsureActive("0.2.53.0"));
    }

    private sealed class BlockedUiContext : SynchronizationContext
    {
        private readonly object gate = new();
        private readonly List<(SendOrPostCallback Callback, object? State)> pending = [];
        private bool released;
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (gate)
            {
                PostCount++;
                if (!released) { pending.Add((callback, state)); return; }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        public void Release()
        {
            lock (gate)
            {
                released = true;
                foreach (var (callback, state) in pending) ThreadPool.QueueUserWorkItem(_ => callback(state));
                pending.Clear();
            }
        }
    }
}
