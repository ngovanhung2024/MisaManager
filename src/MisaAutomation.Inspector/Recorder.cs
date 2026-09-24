using System.Runtime.InteropServices;

namespace MisaAutomation.Inspector;

internal sealed class Recorder(Control dispatcher) : IDisposable
{
    public const int HotkeyId = 0x4D52;
    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    { public Point Point; public uint Mouse, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int type, HookProc hook, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    private HookProc? callback;
    private nint hook, window;
    public bool Active => hook != 0;
    public event Action<Point>? Captured;
    public bool RegisterHotkey(nint hwnd) { window = hwnd; return RegisterHotKey(hwnd, HotkeyId, 0x4006, 0x52); }
    public void Toggle()
    {
        if (Active) { UnhookWindowsHookEx(hook); hook = 0; return; }
        callback = (code, message, data) =>
        {
            if (code >= 0 && message == 0x0201)
            {
                var mouse = Marshal.PtrToStructure<MouseData>(data);
                if ((mouse.Flags & 1) == 0 && !dispatcher.IsDisposed)
                    dispatcher.BeginInvoke(() => Captured?.Invoke(mouse.Point));
            }
            return CallNextHookEx(hook, code, message, data);
        };
        hook = SetWindowsHookEx(14, callback, 0, 0);
        if (hook == 0) throw new InvalidOperationException("Windows did not install the recorder mouse hook.");
    }
    public void Dispose()
    {
        if (hook != 0) UnhookWindowsHookEx(hook);
        if (window != 0) UnregisterHotKey(window, HotkeyId);
        hook = window = 0;
    }
}
