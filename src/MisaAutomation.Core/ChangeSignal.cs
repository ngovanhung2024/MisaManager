namespace MisaAutomation.Core;

// A versioned broadcast avoids losing an event between observation and waiting.
public sealed class ChangeSignal : IDisposable
{
    private readonly object gate = new();
    private TaskCompletionSource<long> next = NewSource();
    private long version;
    private bool disposed;
    private static TaskCompletionSource<long> NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Version { get { lock (gate) return version; } }
    public void Pulse()
    {
        lock (gate)
        {
            if (disposed) return;
            var previous = next;
            next = NewSource();
            previous.TrySetResult(++version);
        }
    }
    public void Wait(long observedVersion, TimeSpan maximum, CancellationToken token)
        => WaitAsync(observedVersion, maximum, token).GetAwaiter().GetResult();
    public async Task WaitAsync(long observedVersion, TimeSpan maximum, CancellationToken token)
    {
        Task task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (observedVersion != version) return;
            task = next.Task;
        }
        try { await task.WaitAsync(maximum, token).ConfigureAwait(false); }
        catch (TimeoutException) { /* Providers may omit events; bounded polling is intentional. */ }
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; next.TrySetCanceled(); }
    }
}

public sealed class SmartWait(ChangeSignal changes)
{
    public T Until<T>(Func<T> observe, Func<T, bool> accepted, TimeSpan timeout,
        CancellationToken token, TimeSpan? pollInterval = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(150);
        if (poll <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var version = changes.Version;
            var value = observe();
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed >= timeout)
                throw new AutomationException("TIMEOUT", "MISA observation completed after the deadline; its result remains unverified.");
            if (accepted(value)) return value;
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new AutomationException("TIMEOUT", "Expected MISA state was not observed before the deadline.");
            changes.Wait(version, remaining < poll ? remaining : poll, token);
        }
    }
}
