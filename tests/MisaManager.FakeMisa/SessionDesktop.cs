using System.Runtime.InteropServices;

namespace MisaManager.FakeMisa;

/// <summary>
/// Remote Desktop closed/minimised on the VPS (report 0.2.169, HĐ 81): the session's input desktop leaves MISA's
/// desktop, so Windows refuses SetForegroundWindow/SendInput for MISA's windows, then comes back on reconnect.
/// Modelled by switching input to a private desktop, which is what a lock/disconnect does to "Default".
/// </summary>
internal static class SessionDesktop
{
    public static void DisconnectFor(int milliseconds) => new Thread(() =>
    {
        var home = OpenDesktop("Default", 0, false, DesktopSwitch);
        var away = CreateDesktop("FakeMisaDisconnected", null, 0, 0, GenericAll, 0);
        if (home == 0 || away == 0 || !SwitchDesktop(away))
        {
            FakeLog.Write($"SESSION disconnect not simulated (error {Marshal.GetLastWin32Error()})");
            return;
        }
        FakeLog.Write($"SESSION disconnected for {milliseconds} ms");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SwitchDesktop(home);
        Thread.Sleep(milliseconds);
        SwitchDesktop(home);
        FakeLog.Write("SESSION reconnected");
        CloseDesktop(away);
    }) { IsBackground = true }.Start();

    private const uint DesktopSwitch = 0x0100, GenericAll = 0x10000000;
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenDesktopW", SetLastError = true)]
    private static extern nint OpenDesktop(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDesktopW", SetLastError = true)]
    private static extern nint CreateDesktop(string name, string? device, nint mode, uint flags, uint access, nint attributes);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SwitchDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
}
