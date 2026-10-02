using System.Diagnostics;
using MisaManager.Core;

namespace MisaManager.Automation;

internal static class NativeWait
{
    // Poll readback only. Never replay the action that started an asynchronous UI update.
    public static async Task<T> UntilAsync<T>(Func<Task<T>> observe, Func<T, bool> ready,
        string code, string message, CancellationToken token, int timeoutMilliseconds = 5000,
        bool returnLastMismatch = false)
    {
        var started = Stopwatch.GetTimestamp();
        var delay = 20;
        string? pending = null;
        T? last = default;
        var hasLast = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var value = await observe();
                if (ready(value)) return value;
                last = value;
                hasLast = true;
                pending = null;
            }
            catch (AutomationException error) when (!error.InputMayHaveBeenSent
                && error.Code is "TARGET_NOT_FOUND" or "NATIVE_READ_TIMEOUT" or "NATIVE_ELEMENT_UNAVAILABLE")
            {
                pending = error.Message;
            }
            var remaining = timeoutMilliseconds - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0)
            {
                if (returnLastMismatch && hasLast && pending is null) return last!;
                throw new AutomationException(code, pending is null ? message : message + " " + pending);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, remaining)), token);
            delay = Math.Min(delay * 2, 200);
        }
    }
}
