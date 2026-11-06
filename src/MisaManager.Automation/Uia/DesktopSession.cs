using System.Diagnostics;
using System.Runtime.InteropServices;
using MisaManager.Core;

namespace MisaManager.Automation;

/// <summary>
/// Whether this Windows session can take foreground changes and SendInput. A closed Remote Desktop (disconnected
/// session), a locked screen or another input desktop leaves MISA's desktop without input: Windows then rejects
/// SetForegroundWindow/SendInput and every activation fails, however often it is retried.
/// </summary>
public static class DesktopSession
{
    public const string UnavailableCode = InterferenceRetry.DesktopUnavailableCode;

    private const string Guidance = " Tool đang chờ, chưa gửi phím/chuột nào và sẽ tự chạy tiếp khi màn hình VPS hoạt động lại. "
        + "Muốn tắt Remote Desktop mà tool vẫn chạy: dùng ••• → \"Ngắt Remote Desktop, giữ MISA chạy\" thay cho nút X.";

    /// <summary>Why input cannot reach MISA now, or null when the session's desktop takes input.</summary>
    public static string? Blocker()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (ConnectState() is { } state && state != WtsActive)
            return "Phiên Windows trên VPS đã ngắt kết nối (Remote Desktop đã đóng) nên Windows không cho đưa MISA lên trước." + Guidance;
        var input = OpenInputDesktop(0, false, DesktopReadObjects);
        if (input == 0)
            return "Windows đang khóa màn hình (hoặc đang hiện hộp UAC) nên không nhận phím/chuột." + Guidance;
        try
        {
            var own = GetThreadDesktop(GetCurrentThreadId());
            if (own != 0 && !string.Equals(Name(input), Name(own), StringComparison.OrdinalIgnoreCase))
                return "Màn hình nhập liệu của Windows đang ở desktop khác (khóa màn hình/đăng nhập), không phải desktop của MISA." + Guidance;
        }
        finally { CloseDesktop(input); }
        return null;
    }

    /// <param name="activationFailed">
    /// Activation just failed repeatedly. In a Remote Desktop session with no foreground window at all, the RDP window
    /// is minimised/hidden on the user's PC: the session still reports Active, but Windows takes no foreground change.
    /// </param>
    internal static void ThrowIfUnavailable(bool activationFailed = false)
    {
        if (Blocker() is { } reason) throw new AutomationException(UnavailableCode, reason);
        if (activationFailed && IsRemote && GetForegroundWindow() == 0)
            throw new AutomationException(UnavailableCode,
                "Windows trên VPS không có cửa sổ nào ở foreground (cửa sổ Remote Desktop đang thu nhỏ/ẩn) nên không đưa được MISA lên trước. "
                + "Mở lại cửa sổ Remote Desktop, hoặc trên máy tính của bạn đặt registry HKCU\\Software\\Microsoft\\Terminal Server Client "
                + "→ DWORD RemoteDesktop_SuppressWhenMinimized = 2 rồi kết nối lại." + Guidance);
    }

    private static int SessionId { get { using var self = Process.GetCurrentProcess(); return self.SessionId; } }

    /// <summary>True when this process runs in a Remote Desktop session (not on the VPS console).</summary>
    public static bool IsRemote => OperatingSystem.IsWindows() && GetSystemMetrics(SmRemoteSession) != 0;

    /// <summary>
    /// Keeps the display on and the machine awake while a run works on MISA, so an idle VPS console neither sleeps nor
    /// starts the screen saver lock. Must be disposed on the thread that created it.
    /// </summary>
    public static IDisposable KeepAwake()
    {
        if (!OperatingSystem.IsWindows()) return new Awake(false);
        return new Awake(SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired) != 0);
    }

    /// <summary>
    /// Ends the Remote Desktop connection but moves this session to the VPS console (tscon &lt;id&gt; /dest:console), so
    /// the desktop stays unlocked and MISA keeps receiving input. Needs administrator rights (UAC prompt if not elevated).
    /// </summary>
    public static void MoveToConsole()
    {
        var system = Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem
            ? Environment.SystemDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
        var start = new ProcessStartInfo(Path.Combine(system, "tscon.exe"), $"{SessionId} /dest:console")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Không chạy được tscon.exe.");
        if (process.WaitForExit(15000) && process.ExitCode != 0)
            throw new InvalidOperationException($"tscon.exe báo lỗi {process.ExitCode}: không chuyển được phiên sang console của VPS.");
    }

    private sealed class Awake(bool active) : IDisposable
    {
        private bool active = active;
        public void Dispose()
        {
            if (!active) return;
            active = false;
            SetThreadExecutionState(EsContinuous);
        }
    }

    private static int? ConnectState()
    {
        if (!WTSQuerySessionInformation(0, WtsCurrentSession, WtsConnectStateClass, out var buffer, out var bytes) || buffer == 0)
            return null;
        try { return bytes >= sizeof(int) ? Marshal.ReadInt32(buffer) : null; }
        finally { WTSFreeMemory(buffer); }
    }

    private static string Name(nint desktop)
    {
        var buffer = new char[256];
        return GetUserObjectInformation(desktop, UoiName, buffer, buffer.Length * sizeof(char), out _)
            ? new string(buffer).TrimEnd('\0') : "";
    }

    private const int WtsActive = 0;
    private const int WtsConnectStateClass = 8;
    private const int WtsCurrentSession = -1;
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;
    private const int SmRemoteSession = 0x1000;
    private const uint EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002, EsContinuous = 0x80000000;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass, out nint buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint memory);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    private static extern bool GetUserObjectInformation(nint handle, int index, [Out] char[] info, int length, out int needed);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
