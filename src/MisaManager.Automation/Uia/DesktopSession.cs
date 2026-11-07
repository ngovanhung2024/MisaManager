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
        + "Để ẩn VPS mà tool vẫn chạy: bật ••• → VPS → \"Tự giữ MISA chạy khi thu nhỏ/đóng Remote Desktop\", "
        + "hoặc chạy lệnh ••• → VPS → \"Copy lệnh cho máy tính của bạn\" trên máy bạn dùng để kết nối.";

    /// <summary>
    /// Registry fix for the PC the user connects from (not the VPS): mstsc and other clients built on its ActiveX
    /// control keep rendering and passing input to a minimised session (UiPath/SmartBear/Power Automate guidance).
    /// </summary>
    public const string ClientRegistryCommand =
        "reg add \"HKCU\\Software\\Microsoft\\Terminal Server Client\" /v RemoteDesktop_SuppressWhenMinimized /t REG_DWORD /d 2 /f\r\n"
        + "reg add \"HKCU\\Software\\Wow6432Node\\Microsoft\\Terminal Server Client\" /v RemoteDesktop_SuppressWhenMinimized /t REG_DWORD /d 2 /f\r\n";

    /// <summary>
    /// When input cannot reach MISA in a Remote Desktop session (window minimised or closed), move the session to the
    /// VPS console instead of waiting for the user to come back. The RDP client is disconnected; reconnecting is normal.
    /// </summary>
    public static bool AutoConsole { get; set; }

    /// <summary>Moves this session to the console; true when it was attempted successfully. Replaced by integration tests.</summary>
    public static Func<bool> ConsoleMover { get; set; } = TryMoveToConsole;

    private static readonly TimeSpan ConsoleRetryInterval = TimeSpan.FromSeconds(20);
    private static long lastConsoleMove;

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
    /// Activation just failed on every attempt. In a Remote Desktop session this is the minimised client: it suppresses
    /// the session's output and Windows ignores SendInput and refuses foreground changes while the session still reports
    /// Active, so no direct probe exists. Waiting is safe: no input has been sent.
    /// </param>
    /// <returns>True when the session was moved to the console and now takes input: activation may be tried again.</returns>
    internal static bool ThrowIfUnavailable(bool activationFailed = false)
    {
        var reason = Blocker();
        if (reason is null && activationFailed && IsRemote)
            reason = "Không đưa được MISA lên trước trong phiên Remote Desktop: cửa sổ Remote Desktop trên máy bạn đang thu nhỏ/ẩn, "
                + "Windows trên VPS bỏ qua phím/chuột khi đó." + Guidance;
        if (reason is null) return false;
        if (AutoConsole && TryAutoConsole()) return true;
        throw new AutomationException(UnavailableCode, reason);
    }

    private static bool TryAutoConsole()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref lastConsoleMove);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < ConsoleRetryInterval) return false;
        Interlocked.Exchange(ref lastConsoleMove, now);
        return MoveToConsoleKeepingDisplay();
    }

    /// <summary>
    /// Run start and every invoice start on a VPS, before any input reaches MISA: a Remote Desktop session is moved to
    /// the VPS console right away, while nothing is half-typed. Minimising or closing Remote Desktop afterwards changes
    /// nothing for the run, so no desktop switch can happen in the middle of a step. The late move in
    /// <see cref="ThrowIfUnavailable"/> only remains for a user who reconnects and hides Remote Desktop mid-invoice.
    /// </summary>
    /// <returns>A status line when the session had to change; null when it was already fit for unattended work.</returns>
    public static string? PrepareUnattended()
    {
        if (!OperatingSystem.IsWindows() || !AutoConsole) return null;
        lock (PrepareGate)
        {
            if (!IsRemote) return null;
            Interlocked.Exchange(ref lastConsoleMove, Stopwatch.GetTimestamp());
            return MoveToConsoleKeepingDisplay()
                ? "Đã chuyển phiên Windows sang màn hình console của VPS trước khi nhập: thu nhỏ hoặc đóng Remote Desktop "
                    + "không còn làm gián đoạn tool. Kết nối lại khi cần xem; hóa đơn kế tiếp tool lại tự chuyển về console."
                : "Không chuyển được phiên sang console của VPS (cần chạy tool bằng quyền administrator). "
                    + "Đừng thu nhỏ/đóng Remote Desktop khi tool đang chạy.";
        }
    }

    private static readonly Lock PrepareGate = new();

    private static bool MoveToConsoleKeepingDisplay()
    {
        var before = CurrentDisplayMode();
        bool moved;
        try { moved = ConsoleMover(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Trace.TraceWarning("Không chuyển được phiên sang console: " + error.Message);
            return false;
        }
        if (!moved) return false;
        Trace.TraceInformation("Đã chuyển phiên Remote Desktop sang console của VPS để MISA tiếp tục nhận phím/chuột.");
        // tscon returns before the session's desktop is attached to the console.
        for (var poll = 0; poll < 100 && (Blocker() is not null || IsRemote); poll++) Thread.Sleep(100);
        // The console usually starts at another resolution than the RDP client. Give MISA its screen size back so its
        // layout (grid columns in view) and the frames captured before the move stay valid.
        if (before is { } mode) RestoreDisplayMode(mode);
        WaitForSteadyDesktop();
        return Blocker() is null;
    }

    // Screen size and input desktop unchanged for a moment: MISA has finished re-laying out after the switch.
    private static void WaitForSteadyDesktop()
    {
        if (!OperatingSystem.IsWindows()) return;
        var deadline = Environment.TickCount64 + 15000;
        var last = DesktopState();
        var since = Environment.TickCount64;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(200);
            var current = DesktopState();
            if (current != last) (last, since) = (current, Environment.TickCount64);
            else if (Environment.TickCount64 - since >= 1500) return;
        }
    }

    private static (int Width, int Height, bool Input) DesktopState()
        => (GetSystemMetrics(SmCxScreen), GetSystemMetrics(SmCyScreen), Blocker() is null);

    private static DevMode? CurrentDisplayMode()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettings(null, EnumCurrentSettings, ref mode) ? mode : null;
    }

    private static void RestoreDisplayMode(DevMode before)
    {
        if (CurrentDisplayMode() is not { } current
            || current.PelsWidth == before.PelsWidth && current.PelsHeight == before.PelsHeight) return;
        var wanted = current;
        wanted.PelsWidth = before.PelsWidth;
        wanted.PelsHeight = before.PelsHeight;
        wanted.Fields = DmPelsWidth | DmPelsHeight;
        // Not persisted (flags 0): a later Remote Desktop connection applies its own size as usual.
        var result = ChangeDisplaySettingsEx(null, ref wanted, 0, 0, 0);
        if (result != 0)
            Trace.TraceWarning($"Console VPS không nhận độ phân giải {before.PelsWidth}x{before.PelsHeight} (mã {result}).");
    }

    /// <summary>
    /// Silent tscon for <see cref="AutoConsole"/>: only from an elevated process (the app requires administrator) and only
    /// when this session is not already the console session.
    /// </summary>
    public static bool TryMoveToConsole()
    {
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess) return false;
        if (WTSGetActiveConsoleSessionId() == (uint)SessionId) return false;
        return RunTscon(elevate: false);
    }

    private static int SessionId { get { using var self = Process.GetCurrentProcess(); return self.SessionId; } }

    /// <summary>True when this process runs in a Remote Desktop session (not on the VPS console).</summary>
    public static bool IsRemote => RemoteProbe();

    /// <summary>Remote Desktop probe behind <see cref="IsRemote"/>. Replaced by integration tests.</summary>
    public static Func<bool> RemoteProbe { get; set; } = () => OperatingSystem.IsWindows() && GetSystemMetrics(SmRemoteSession) != 0;

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
        if (!RunTscon(elevate: !Environment.IsPrivilegedProcess))
            throw new InvalidOperationException("tscon.exe báo lỗi: không chuyển được phiên sang console của VPS.");
    }

    private static bool RunTscon(bool elevate)
    {
        var system = Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem
            ? Environment.SystemDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
        var start = new ProcessStartInfo(Path.Combine(system, "tscon.exe"), $"{SessionId} /dest:console")
        {
            UseShellExecute = elevate,
            Verb = elevate ? "runas" : "",
            CreateNoWindow = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Không chạy được tscon.exe.");
        return process.WaitForExit(15000) && process.ExitCode == 0;
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
    private const int SmCxScreen = 0, SmCyScreen = 1;
    private const int EnumCurrentSettings = -1;
    private const uint DmPelsWidth = 0x00080000, DmPelsHeight = 0x00100000;
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
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DevMode devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(string? device, ref DevMode devMode, nint window, uint flags, nint parameter);

    // DEVMODEW (display part of the unions), 220 bytes.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
}
