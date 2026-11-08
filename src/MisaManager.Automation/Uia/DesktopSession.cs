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
        + "Đóng Remote Desktop (nút X) thì tool tự giữ MISA chạy khi bật ••• → VPS → \"Tự giữ MISA chạy khi đóng Remote Desktop\". "
        + "Để thu nhỏ Remote Desktop mà tool vẫn chạy: chạy lệnh ••• → VPS → \"Copy lệnh cho máy tính của bạn\" một lần trên máy bạn dùng để kết nối.";

    /// <summary>
    /// Registry fix for the PC the user connects from (not the VPS): mstsc and other clients built on its ActiveX
    /// control keep rendering and passing input to a minimised session (UiPath/SmartBear/Power Automate guidance).
    /// </summary>
    public const string ClientRegistryCommand =
        "reg add \"HKCU\\Software\\Microsoft\\Terminal Server Client\" /v RemoteDesktop_SuppressWhenMinimized /t REG_DWORD /d 2 /f\r\n"
        + "reg add \"HKCU\\Software\\Wow6432Node\\Microsoft\\Terminal Server Client\" /v RemoteDesktop_SuppressWhenMinimized /t REG_DWORD /d 2 /f\r\n";

    /// <summary>
    /// When Remote Desktop is closed (the session is left without a client), move the session to the VPS console so
    /// MISA keeps a desktop that takes input. A connected session is never moved: tscon would end the user's connection.
    /// </summary>
    public static bool AutoConsole { get; set; }

    /// <summary>Moves this session to the console; true when it was attempted successfully. Replaced by integration tests.</summary>
    public static Func<bool> ConsoleMover { get; set; } = TryMoveToConsole;

    /// <summary>True while no client is connected to this session (Remote Desktop closed). Replaced by integration tests.</summary>
    public static Func<bool> DisconnectedProbe { get; set; } = () => OperatingSystem.IsWindows() && ConnectState() is { } state && state != WtsActive;

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
                + "Windows trên VPS bỏ qua phím/chuột khi đó. Tool không ngắt kết nối của bạn." + Guidance;
        if (reason is null) return false;
        // Only a closed Remote Desktop is moved; a minimised or locked one still has the user's connection.
        if (AutoConsole && DisconnectedProbe() && TryAutoConsole()) return true;
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

    private static readonly Lock GuardGate = new();
    private static Thread? guard;
    private static DevMode? connectedDisplay;

    /// <summary>
    /// Armed once when the app starts, whether or not a run is going: the moment Remote Desktop is closed, the session is
    /// moved to the VPS console (one tscon per disconnect), so MISA keeps its desktop before anything needs it. Its
    /// screen size while connected is remembered and given back on the console.
    /// </summary>
    public static void StartGuard()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (GuardGate)
        {
            if (guard is not null) return;
            guard = new Thread(GuardLoop) { IsBackground = true, Name = "VPS console guard" };
            guard.Start();
        }
    }

    private static void GuardLoop()
    {
        var handled = false;
        while (true)
        {
            Thread.Sleep(300);
            try
            {
                if (!DisconnectedProbe())
                {
                    handled = false;
                    if (Blocker() is null && CurrentDisplayMode() is { } mode) lock (GuardGate) connectedDisplay = mode;
                    continue;
                }
                if (handled || !AutoConsole) continue;
                handled = true;
                Interlocked.Exchange(ref lastConsoleMove, Stopwatch.GetTimestamp());
                MoveToConsoleKeepingDisplay();
            }
            catch (Exception error) { Trace.TraceWarning("VPS console guard: " + error.Message); }
        }
    }

    private static readonly Lock MoveGate = new();

    private static bool MoveToConsoleKeepingDisplay()
    {
        lock (MoveGate)
        {
            // The guard and a blocked step can both ask; the second finds the session already on the console.
            if (!DisconnectedProbe()) return Blocker() is null;
            DevMode? before;
            lock (GuardGate) before = connectedDisplay;
            before ??= CurrentDisplayMode();
            bool moved;
            try { moved = ConsoleMover(); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                Trace.TraceWarning("Không chuyển được phiên sang console: " + error.Message);
                return false;
            }
            if (!moved) return false;
            Trace.TraceInformation("Remote Desktop đã đóng: chuyển phiên sang console của VPS để MISA tiếp tục nhận phím/chuột.");
            // tscon returns before the session's desktop is attached to the console.
            for (var poll = 0; poll < 100 && (Blocker() is not null || IsRemote); poll++) Thread.Sleep(100);
            // The console usually starts at another resolution than the RDP client. Give MISA its screen size back so its
            // layout (grid columns in view) and the frames captured before the move stay valid.
            if (before is { } mode) RestoreDisplayMode(mode);
            WaitForSteadyDesktop();
            return Blocker() is null;
        }
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
