using System.Diagnostics;
using MisaManager.Core;

namespace MisaManager.Automation;

internal static class NativeWait
{
    // Read failures that only mean "MISA is still redrawing"; polling again is safe because observe() only reads.
    // GRID_ROW_CHANGED: R20 phiếu nhập redraws the row after Thành tiền and the code cell briefly reads blank
    // (report 0.2.142). A row whose code really changed still fails when the wait times out.
    private static readonly HashSet<string> TransientReadCodes = new(StringComparer.Ordinal)
    {
        "TARGET_NOT_FOUND", "TARGET_AMBIGUOUS", "NATIVE_READ_TIMEOUT", "NATIVE_ELEMENT_UNAVAILABLE", "NATIVE_BUSY",
        "GRID_ROWS_INVALID", "GRID_COLUMNS_INVALID", "GRID_NOT_STABLE", "GRID_CHANGED", "GRID_ROW_CHANGED",
        "SCOPE_IDENTITY_CHANGED", "STALE_NATIVE_FRAME"
    };

    // Poll readback only. Never replay the action that started an asynchronous UI update.
    // The default leaves room for a slow MISA (server lookups, cold dialogs); callers pass less where speed matters.
    // readFailureTimeoutMilliseconds: while the latest observation is a transient read failure (not a value),
    // keep polling up to this long, so a nested wait that used up timeoutMilliseconds still gets a fresh pass.
    public static async Task<T> UntilAsync<T>(Func<Task<T>> observe, Func<T, bool> ready,
        string code, string message, CancellationToken token, int timeoutMilliseconds = 10000,
        bool returnLastMismatch = false, int readFailureTimeoutMilliseconds = 0)
    {
        var started = Stopwatch.GetTimestamp();
        var delay = 20;
        string? pending = null;
        Exception? pendingError = null;
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
                pendingError = null;
            }
            catch (AutomationException error) when (!error.InputMayHaveBeenSent && TransientReadCodes.Contains(error.Code))
            {
                (pending, pendingError) = (error.Message, error);
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException
                or FlaUI.Core.Exceptions.ElementNotAvailableException or FlaUI.Core.Exceptions.PropertyNotSupportedException)
            {
                (pending, pendingError) = (error.Message, error);
            }
            var limit = pending is null ? timeoutMilliseconds : Math.Max(timeoutMilliseconds, readFailureTimeoutMilliseconds);
            var remaining = limit - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0)
            {
                if (returnLastMismatch && hasLast && pending is null) return last!;
                // The read failure stays attached so Copy lỗi shows the file:line where MISA's element vanished.
                throw new AutomationException(code, pending is null ? message : message + " " + pending, false, pendingError);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, remaining)), token);
            delay = Math.Min(delay * 2, 200);
        }
    }
}
