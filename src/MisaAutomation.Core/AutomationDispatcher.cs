using System.Collections.Concurrent;

namespace MisaAutomation.Core;

// UIA objects are created, read and disposed on one MTA thread. Callbacks only signal.
public sealed class AutomationDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    public AutomationDispatcher()
    {
        thread = new Thread(() => { foreach (var action in queue.GetConsumingEnumerable()) action(); })
            { IsBackground = true, Name = "MISA UIA owner" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }
    public Task<T> RunAsync<T>(Func<T> action, CancellationToken token = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            queue.Add(() =>
            {
                try { token.ThrowIfCancellationRequested(); completion.SetResult(action()); }
                catch (OperationCanceledException) { completion.SetCanceled(token); }
                catch (Exception error) { completion.SetException(error); }
            }, token);
        }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }
    public void Dispose()
    {
        queue.CompleteAdding();
        if (!thread.Join(TimeSpan.FromSeconds(5)))
            throw new AutomationException("PROVIDER_BUSY", "UIA owner is still running; restart the engine before more input.");
        queue.Dispose();
    }
}
