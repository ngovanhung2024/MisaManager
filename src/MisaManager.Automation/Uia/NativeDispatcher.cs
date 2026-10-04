using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MisaManager.Core;

namespace MisaManager.Automation;

// COM objects are owned, used and released on one bounded MTA queue.
public sealed class NativeDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> queue = new(32);
    private readonly Thread thread;
    private readonly object startGate = new();
    private readonly List<IDisposable> resources = [];
    private int unavailable;
    private int stopping;
    private long operations;
    private long completed;
    private long totalMilliseconds;
    private long lastMilliseconds;
    private readonly ConcurrentQueue<long> samples = new();

    public NativeDispatcher()
    {
        thread = new Thread(() =>
        {
            try { foreach (var action in queue.GetConsumingEnumerable()) action(); }
            finally
            {
                foreach (var resource in resources.AsEnumerable().Reverse())
                {
                    try { resource.Dispose(); }
                    catch (Exception error) { System.Diagnostics.Trace.TraceError("Native disposal: {0}", error); }
                }
            }
        }) { IsBackground = true, Name = "MisaManager UIA3" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public async Task<T> RunAsync<T>(Func<T> action, CancellationToken token = default, bool maySendInput = false)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref unavailable) != 0 || Volatile.Read(ref stopping) != 0)
            throw new AutomationException("NATIVE_OWNER_UNAVAILABLE", "MISA chưa trả lời thao tác trước đó; chờ MISA hết bận rồi thử lại.");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var finished = false;
        var abandoned = false;
        // The owner thread runs one call at a time, so once an abandoned in-flight call returns
        // nothing else touches UIA concurrently: lift the block instead of requiring an app restart.
        // Workflow state still records that input may have been sent, so nothing is replayed blindly.
        void Finish()
        {
            lock (startGate)
            {
                finished = true;
                if (abandoned && Volatile.Read(ref started) != 0) Interlocked.Exchange(ref unavailable, 0);
            }
        }
        try
        {
            if (!queue.TryAdd(() =>
            {
                try
                {
                    lock (startGate)
                    {
                        if (abandoned) throw new OperationCanceledException("Native call expired before it started.");
                        token.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref unavailable) != 0) throw new AutomationException("NATIVE_OWNER_UNAVAILABLE", "Native provider đã bị dừng.");
                        Interlocked.Exchange(ref started, 1);
                    }
                    var result = action();
                    Finish();
                    completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    Finish();
                    completion.TrySetCanceled(token);
                }
                catch (Exception error)
                {
                    Finish();
                    completion.TrySetException(error);
                }
            })) throw queue.IsAddingCompleted ? new InvalidOperationException("Native queue is stopping.")
                : new AutomationException("NATIVE_BUSY", "Hàng đợi thao tác MISA đang đầy; thử lại sau.");
        }
        catch (InvalidOperationException) when (queue.IsAddingCompleted)
        { throw new AutomationException("NATIVE_OWNER_UNAVAILABLE", "Native owner đang đóng."); }
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref operations);
        try
        {
            // Grid writes and provider recreation can take longer than the
            // per-call default while MISA remains responsive. Keep the
            // cancellation/uncertainty semantics, but allow enough time for
            // the native provider to finish a single bounded operation.
            var value = await completion.Task.WaitAsync(TimeSpan.FromSeconds(90), token);
            var elapsed = ElapsedMilliseconds(startedAt);
            Interlocked.Increment(ref completed);
            Interlocked.Exchange(ref lastMilliseconds, elapsed);
            Interlocked.Add(ref totalMilliseconds, elapsed);
            samples.Enqueue(elapsed);
            while (samples.Count > 512 && samples.TryDequeue(out _)) { }
            return value;
        }
        catch (COMException error) when (!maySendInput && completion.Task.IsFaulted
            && error.HResult == unchecked((int)0x80040201) && !token.IsCancellationRequested)
        {
            // UIA elements can disappear while a saved voucher closes. Re-read
            // the scope; do not reinterpret this as absence or replay its action.
            throw new AutomationException("NATIVE_ELEMENT_UNAVAILABLE",
                "Native element disappeared during readback; fresh readback is required.", false, error);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException
            || error is COMException { HResult: unchecked((int)0x80131505) })
        {
            // A completed read timeout is safe to observe again; a write or still-running
            // provider call must retain the interruption/uncertainty path below.
            if (!maySendInput && (error is TimeoutException
                    || error is COMException { HResult: unchecked((int)0x80131505) }) && completion.Task.IsFaulted
                && !token.IsCancellationRequested)
                throw new AutomationException("NATIVE_READ_TIMEOUT", "Native read timed out after the provider returned; fresh readback is required.", false, error);
            // Only an in-flight native call can outlive its caller. Queued/finished work is safe to cancel.
            lock (startGate)
            {
                abandoned = true;
                if (started != 0 && !finished) Interlocked.Exchange(ref unavailable, 1);
            }
            _ = completion.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new AutomationException("NATIVE_CALL_INTERRUPTED", "Native call bị gián đoạn; không chạy lại input trước khi đọc và xử lý trạng thái.",
                maySendInput && Volatile.Read(ref started) != 0, error);
        }
    }

    public NativeDispatcherMetrics Metrics
    {
        get
        {
            var values = samples.ToArray();
            Array.Sort(values);
            return new(operations, completed, lastMilliseconds,
                completed == 0 ? 0 : (double)Interlocked.Read(ref totalMilliseconds) / completed,
                Percentile(values, .50), Percentile(values, .95), queue.Count, queue.BoundedCapacity,
                Volatile.Read(ref unavailable) != 0, Volatile.Read(ref stopping) != 0);
        }
    }

    private static long Percentile(long[] values, double percentile)
        => values.Length == 0 ? 0 : values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * percentile) - 1)];

    private static long ElapsedMilliseconds(long started)
        => (long)(1000d * (System.Diagnostics.Stopwatch.GetTimestamp() - started) / System.Diagnostics.Stopwatch.Frequency);

    public T Own<T>(Func<T> factory) where T : IDisposable
    {
        if (Thread.CurrentThread != thread) throw new InvalidOperationException("Native resource must be created on its owner thread.");
        var resource = factory();
        resources.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        queue.CompleteAdding();
        if (!thread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Native owner did not stop; restart is required before further input.");
        queue.Dispose();
    }
}

public sealed record NativeDispatcherMetrics(long Operations, long Completed, long LastMilliseconds,
    double AverageMilliseconds, long P50Milliseconds, long P95Milliseconds, int QueueLength, int QueueCapacity,
    bool Unavailable, bool Stopping);
