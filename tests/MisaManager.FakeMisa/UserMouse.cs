using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MisaManager.FakeMisa;

/// <summary>
/// Deterministic model of a user moving the mouse while the tool clicks. SetCursorPos produces no
/// low-level event, so an injected press without an injected move to the same point just before it is
/// a click that depends on the live cursor; it is dropped and the cursor moved away, as if the user nudged it.
/// </summary>
public static class UserMouse
{
    private const int WhMouseLowLevel = 14, WmMouseMove = 0x0200, WmLeftDown = 0x0201, WmLeftUp = 0x0202;
    private static HookProc? proc;
    private static nint hook;
    private static (int X, int Y)? lastInjectedMove;
    private static bool dropUp;
    private static Func<bool> active = () => false;

    /// <summary>Set once a voucher is saved (for "after_save").</summary>
    public static bool Saved { get; set; }

    public static void Install(Scenario scenario)
    {
        if (scenario.UserMouseNudge is not ("always" or "after_save")) return;
        active = scenario.UserMouseNudge == "always" ? () => true : () => Saved;
        proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule!;
        hook = SetWindowsHookEx(WhMouseLowLevel, proc, GetModuleHandle(module.ModuleName), 0);
        FakeLog.Write($"user mouse nudge {scenario.UserMouseNudge} hook={hook}");
    }

    private static nint Callback(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<HookInfo>(data);
            var injected = (info.Flags & 1) != 0;
            switch ((int)message)
            {
                case WmMouseMove:
                    lastInjectedMove = injected ? (info.X, info.Y) : null;
                    break;
                case WmLeftDown when injected && active():
                    var atomic = lastInjectedMove is { } move && Math.Abs(move.X - info.X) <= 1 && Math.Abs(move.Y - info.Y) <= 1;
                    lastInjectedMove = null;
                    if (!atomic)
                    {
                        FakeLog.Write($"user nudged the mouse: click at ({info.X},{info.Y}) missed");
                        SetCursorPos(info.X + 400, info.Y + 400);
                        dropUp = true;
                        return 1;
                    }
                    break;
                case WmLeftUp when dropUp:
                    dropUp = false;
                    return 1;
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct HookInfo { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string name);
}
