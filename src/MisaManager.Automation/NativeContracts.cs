using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MisaManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MisaManager.Automation;

public sealed record WindowIdentity(int ProcessId, DateTime ProcessStartUtc, nint WindowHandle,
    string ExecutablePath, string Title, int Dpi, Bounds ClientBounds, string ProcessArchitecture);
public sealed record NativeFrame(WindowIdentity Window, string TabIdentity, long InputEpoch, DateTimeOffset ObservedAt);
public sealed record Readback(string Status, string? Value, string Source, DateTimeOffset ObservedAt,
    string? Reason, NativeFrame Frame);
public sealed record InputAttempt(string OperationId, string Destination, InputDisposition Disposition,
    bool InputMayHaveBeenSent, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? ErrorCode);
public sealed record NativeCheckpointMetric(long Count, long LastMilliseconds, long P50Milliseconds, long P95Milliseconds);
public sealed record NativeWorkflowMetrics(long NativeQueries, long Readbacks, long InputOperations,
    long TabSelections, long StartedAtUnixMilliseconds,
    IReadOnlyDictionary<string, NativeCheckpointMetric>? Checkpoints = null,
    long WorkflowCount = 0, long LastWorkflowMilliseconds = 0, long WorkflowP50Milliseconds = 0,
    long WorkflowP95Milliseconds = 0);
public sealed record AutomationCapability(string ProfileId, string MisaVersion, string Status,
    IReadOnlyList<string> Capabilities, string? BlockedReason = null);

public interface IMisaWindowDiscovery
{
    IReadOnlyList<WindowIdentity> FindCandidates();
    WindowIdentity Bind(nint handle, int? expectedProcessId = null);
}

public interface INativeReadback
{
    NativeFrame CaptureFrame();
    IReadOnlyList<ElementIdentity> Observe(NativeFrame frame, string? query = null);
    Readback ReadValue(NativeFrame frame, ElementIdentity target);
}

public interface IMisaInputExecutor
{
    Task<InputAttempt> ExecuteAsync(NativeFrame frame, ElementIdentity target, string value,
        CancellationToken cancellationToken = default);
}

public sealed record ElementIdentity(string Token, string AutomationId, string Name, string ControlType,
    Bounds Bounds, nint NativeHandle, int ProcessId, nint WindowHandle, string[] Patterns,
    string ParentToken, bool Enabled, bool Offscreen, string? Value,
    string ClassName = "", string FrameworkType = "", string HelpText = "",
    string RuntimeId = "", bool KeyboardFocusable = false, bool HasKeyboardFocus = false,
    bool IsContentElement = false, bool IsControlElement = false, string ItemStatus = "");

public sealed class MisaWindowDiscovery : IMisaWindowDiscovery
{
    public IReadOnlyList<WindowIdentity> FindCandidates()
    {
        var result = new List<WindowIdentity>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                // ProcessName needs no process access; skip MainModule enumeration for every other process.
                if (!MisaProcessName.IsMatch(process.ProcessName) || process.HasExited) continue;
                var path = process.MainModule?.FileName ?? "";
                if (!IsMisaExecutable(path)) continue;
                var handle = SessionWindow(process.Id, process.MainWindowHandle);
                if (handle == nint.Zero) continue;
                result.Add(Bind(handle, process.Id));
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or AutomationException)
            { Trace.TraceWarning("Window discovery: {0}: {1}", process.Id, error.Message); }
            finally { process.Dispose(); }
        }
        return result;
    }

    public WindowIdentity Bind(nint handle, int? expectedProcessId = null)
    {
        if (handle == nint.Zero || PInvoke.GetWindowThreadProcessId((HWND)handle, out var pid) == 0 || (expectedProcessId.HasValue && pid != expectedProcessId))
            throw new AutomationException("WINDOW_NOT_FOUND", "Không xác định được cửa sổ MISA theo HWND/PID.");
        using var process = Process.GetProcessById((int)pid);
        if (!IsTopLevelWindow(handle)) throw new AutomationException("WINDOW_IDENTITY_CHANGED", "HWND không còn là cửa sổ chính của process đã chọn.");
        var path = process.MainModule?.FileName ?? "";
        if (!IsMisaExecutable(path)) throw new AutomationException("NOT_MISA", "Process được chọn không phải MISA.");
        if (!PInvoke.GetClientRect((HWND)handle, out var rect)) throw new AutomationException("WINDOW_BOUNDS_UNREADABLE", "Không đọc được client bounds MISA.");
        var origin = new System.Drawing.Point();
        if (!PInvoke.ClientToScreen((HWND)handle, ref origin)) throw new AutomationException("WINDOW_BOUNDS_UNREADABLE", "Không đọc được client origin MISA.");
        var architecture = IsWow64Process2(process.Handle, out var machine, out var nativeMachine)
            ? (machine == 0 ? nativeMachine : machine) switch { 0x014c => "x86", 0x8664 => "x64", 0xaa64 => "arm64", _ => "unknown" }
            : "unknown";
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
            throw new AutomationException("DPI_UNAVAILABLE", "Windows không hỗ trợ đọc DPI theo cửa sổ MISA.");
        var dpi = PInvoke.GetDpiForWindow((HWND)handle);
        return new(process.Id, process.StartTime.ToUniversalTime(), handle, path, GetWindowTitle(handle),
            checked((int)dpi), new(origin.X, origin.Y, rect.right - rect.left, rect.bottom - rect.top), architecture);
    }

    public WindowIdentity Focus(nint handle, int? expectedProcessId = null)
    {
        var identity = Bind(handle, expectedProcessId);
        if (PInvoke.IsIconic((HWND)handle)) PInvoke.ShowWindow((HWND)handle, SHOW_WINDOW_CMD.SW_RESTORE);
        NativeKeyboard.Activate(handle, handle, identity.ProcessId);
        return Bind(handle, expectedProcessId);
    }

    // Re-reads the mutable window state of an already proven binding. The
    // caller holds the process handle open, so its PID cannot be reused.
    internal static WindowIdentity? TryRefresh(WindowIdentity bound, Process process)
    {
        try
        {
            var handle = (HWND)bound.WindowHandle;
            process.Refresh();
            if (process.HasExited || PInvoke.GetWindowThreadProcessId(handle, out var pid) == 0
                || pid != (uint)bound.ProcessId || !IsTopLevelWindow(bound.WindowHandle)
                || !PInvoke.GetClientRect(handle, out var rect))
                return null;
            var origin = new System.Drawing.Point();
            if (!PInvoke.ClientToScreen(handle, ref origin) || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
                return null;
            var dpi = PInvoke.GetDpiForWindow(handle);
            return bound with
            {
                Title = GetWindowTitle(bound.WindowHandle),
                Dpi = checked((int)dpi),
                ClientBounds = new(origin.X, origin.Y, rect.right - rect.left, rect.bottom - rect.top)
            };
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static readonly Regex MisaProcessName = new(@"^MISA\.SME(?:\d{4})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SessionTitle = new(@"^MISA SME \d{4}\b.* - ", RegexOptions.CultureInvariant);

    // Process.MainWindowHandle is "first visible unowned window in z-order", so it flips to MISA's
    // unowned popups (Thêm vật tư, notifications) and to the login/"Mở dữ liệu" window. Bind by
    // structure instead: an unowned top-level window of the process, preferring the logged-in session.
    internal static bool IsTopLevelWindow(nint handle)
        => PInvoke.IsWindow((HWND)handle)
            && (nint)PInvoke.GetAncestor((HWND)handle, GET_ANCESTOR_FLAGS.GA_ROOT) == handle
            && (nint)PInvoke.GetWindow((HWND)handle, GET_WINDOW_CMD.GW_OWNER) == nint.Zero
            && (PInvoke.IsWindowVisible((HWND)handle) || PInvoke.IsIconic((HWND)handle));

    public static bool IsSessionTitle(string title) => SessionTitle.IsMatch(title);

    private static nint SessionWindow(int processId, nint fallback)
    {
        var windows = new List<nint>();
        EnumWindows((handle, _) =>
        {
            if (PInvoke.GetWindowThreadProcessId((HWND)handle, out var pid) != 0 && pid == (uint)processId
                && IsTopLevelWindow(handle)) windows.Add(handle);
            return true;
        }, nint.Zero);
        return windows.FirstOrDefault(handle => IsSessionTitle(GetWindowTitle(handle)))
            is var session and not 0 ? session
            : windows.Contains(fallback) ? fallback
            : windows.FirstOrDefault(handle => GetWindowTitle(handle).StartsWith("MISA", StringComparison.Ordinal));
    }

    private delegate bool EnumWindowsProc(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    private static bool IsMisaExecutable(string path)
        => Regex.IsMatch(Path.GetFileName(path), @"^MISA\.SME(?:\d{4})?\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string GetWindowTitle(nint handle)
    {
        var buffer = new char[512];
        var length = PInvoke.GetWindowText((HWND)handle, buffer);
        return new string(buffer, 0, length);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);

}

// Avoids Process.GetProcessById and MainModule enumeration on every frame.
// Any mismatch or failure falls back to the full MisaWindowDiscovery.Bind.
internal sealed class BoundWindowTracker : IDisposable
{
    private Process? process;
    private WindowIdentity? bound;

    public WindowIdentity Refresh(WindowIdentity expected)
    {
        if (process is not null && bound is not null && bound.ProcessId == expected.ProcessId
            && bound.WindowHandle == expected.WindowHandle && bound.ProcessStartUtc == expected.ProcessStartUtc
            && bound.ExecutablePath == expected.ExecutablePath)
        {
            var fast = MisaWindowDiscovery.TryRefresh(bound, process);
            if (fast is not null) return bound = fast;
        }
        Reset();
        var current = new MisaWindowDiscovery().Bind(expected.WindowHandle, expected.ProcessId);
        Track(current);
        return current;
    }

    private void Track(WindowIdentity current)
    {
        Process? candidate = null;
        try
        {
            candidate = Process.GetProcessById(current.ProcessId);
            _ = candidate.SafeHandle;
            if (candidate.StartTime.ToUniversalTime() != current.ProcessStartUtc) return;
            process = candidate;
            bound = current;
            candidate = null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning("Window tracking: {0}", error.Message);
        }
        finally { candidate?.Dispose(); }
    }

    private void Reset()
    {
        process?.Dispose();
        process = null;
        bound = null;
    }

    public void Dispose() => Reset();
}
