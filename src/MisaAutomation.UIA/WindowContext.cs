using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MisaAutomation.Core;

namespace MisaAutomation.UIA;

internal static class WindowContext
{
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int kind, out int info, int size, out int returned);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    private delegate bool EnumProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    internal static bool MatchesWindowBounds(WindowInfo window, Bounds bounds)
    {
        var hwnd = (nint)window.Handle;
        return !IsIconic(hwnd) && GetWindowRect(hwnd, out var rectangle)
            && rectangle.Left == bounds.X && rectangle.Top == bounds.Y
            && rectangle.Right - rectangle.Left == bounds.Width
            && rectangle.Bottom - rectangle.Top == bounds.Height;
    }

    internal static bool TryReadControlVisibility(nint control, WindowInfo window, Bounds bounds, out bool visible)
    {
        visible = false;
        var owner = (nint)window.Handle;
        if (control == 0 || control == owner || !IsWindow(control)
            || GetAncestor(control, 2) != owner
            || GetWindowThreadProcessId(control, out var pid) == 0 || pid != window.ProcessId
            || !GetWindowRect(control, out var rectangle)) return false;
        // A custom UIA element may expose its parent's HWND. Only its own box proves visibility.
        if (Math.Abs(rectangle.Left - bounds.X) > 2 || Math.Abs(rectangle.Top - bounds.Y) > 2
            || Math.Abs(rectangle.Right - bounds.X - bounds.Width) > 2
            || Math.Abs(rectangle.Bottom - bounds.Y - bounds.Height) > 2) return false;
        visible = IsWindowVisible(control) && !IsIconic(owner);
        return true;
    }

    internal static bool TryReadControlEnabled(nint control, WindowInfo window, Bounds bounds, out bool enabled)
    {
        enabled = false;
        if (!TryReadControlVisibility(control, window, bounds, out var visible) || !visible) return false;
        enabled = IsWindowEnabled(control);
        return true;
    }

    public static WindowInfo Read(long handle, int? expectedPid = null)
    {
        var hwnd = (nint)handle;
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd))
            throw new AutomationException("WINDOW_CLOSED", "The selected MISA window is unavailable.");
        GetWindowThreadProcessId(hwnd, out var pid);
        if (expectedPid.HasValue && pid != expectedPid)
            throw new AutomationException("WINDOW_CHANGED", "The selected handle belongs to another process.");
        using var process = Process.GetProcessById((int)pid);
        if (!process.ProcessName.StartsWith("MISA.SME", StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("NOT_MISA", "The selected process is not MISA SME.");
        var title = new StringBuilder(4096);
        GetWindowText(hwnd, title, title.Capacity);
        return new WindowInfo(handle, (int)pid, title.ToString(), Kind(title.ToString()), IsWindowEnabled(hwnd));
    }
    public static string Kind(string title)
    {
        var name = Fold(title);
        if (name.StartsWith("themvattu")) return "new_item";
        if (name.StartsWith("timnhanh")) return "find";
        if (name.StartsWith("banhanghoadichvutrongnuoc") || name.StartsWith("chungtubanhang")) return "sale";
        return name.StartsWith("misasme") ? "main" : "dialog";
    }
    public static string Fold(string text) => string.Concat(text.ToLowerInvariant().Replace('\u0111', 'd')
        .Normalize(NormalizationForm.FormD).Where(char.IsLetterOrDigit));
    public static IReadOnlyList<WindowInfo> List(int? pid = null)
    {
        var result = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var owner);
            if (pid.HasValue && owner != pid) return true;
            try { var window = Read(hwnd.ToInt64(), pid); if (window.Title.Length > 0) result.Add(window); }
            catch (AutomationException error) when (error.Code is "NOT_MISA" or "WINDOW_CLOSED") { }
            catch (ArgumentException) { /* Window owner exited during enumeration. */ }
            return true;
        }, 0);
        return result;
    }

    private static bool Elevated(Process process)
    {
        var handle = OpenProcess(0x1000, false, process.Id);
        if (handle == 0) throw new AutomationException("PERMISSION_UNREADABLE", "Cannot inspect the process with query-limited access.");
        nint token;
        try
        {
            if (!OpenProcessToken(handle, 8, out token))
                throw new AutomationException("PERMISSION_UNREADABLE", "Cannot inspect process elevation.");
        }
        finally { CloseHandle(handle); }
        try
        {
            if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _))
                throw new AutomationException("PERMISSION_UNREADABLE", "Cannot inspect token elevation.");
            return elevated != 0;
        }
        finally { CloseHandle(token); }
    }
    public static void CheckPermission(int pid)
    {
        using var target = Process.GetProcessById(pid);
        using var current = Process.GetCurrentProcess();
        if (Elevated(target) && !Elevated(current))
            throw new AutomationException("ELEVATION_REQUIRED", "MISA runs as administrator. Start the automation app at the same elevation; it will not elevate itself.");
    }
}
