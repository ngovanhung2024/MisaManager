using System.Runtime.InteropServices;
using MisaManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MisaManager.Automation;

internal static class NativeKeyboard
{
    internal static void Activate(nint targetHandle, nint windowHandle, int processId, CancellationToken token = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { ActivateOnce(targetHandle, windowHandle, processId); return; }
            catch (AutomationException error) when (error.Code == "FOCUS_FAILED" && attempt < 9)
            {
                if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
            }
        }
    }

    private static void ActivateOnce(nint targetHandle, nint windowHandle, int processId)
    {
        var root = InputRoot(targetHandle, windowHandle, processId);
        if ((nint)PInvoke.GetForegroundWindow() == root) return;

        // The owned editor may need foreground activation separately from the
        // host. Attach only for activation and always verify the resulting HWND.
        EnsureMessageQueue();
        var foreground = (nint)PInvoke.GetForegroundWindow();
        var currentThread = PInvoke.GetCurrentThreadId();
        var targetThread = PInvoke.GetWindowThreadProcessId((HWND)root, out _);
        var foregroundThread = foreground == nint.Zero ? 0u : PInvoke.GetWindowThreadProcessId((HWND)foreground, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread
            && PInvoke.AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread
            && targetThread != foregroundThread
            && PInvoke.AttachThreadInput(currentThread, targetThread, true);
        var attachedCross = targetThread != 0 && foregroundThread != 0
            && targetThread != foregroundThread
            && PInvoke.AttachThreadInput(targetThread, foregroundThread, true);
        try
        {
            LockSetForegroundWindow(2); // LSFW_UNLOCK
            AllowSetForegroundWindow(0xFFFFFFFF);
            SendInput(1, [Key(0x12)], Marshal.SizeOf<NativeInput>());
            SendInput(1, [Key(0x12, true)], Marshal.SizeOf<NativeInput>());
            SetWindowPos((HWND)root, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos((HWND)root, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SwitchToThisWindow((HWND)root, true);
            BringWindowToTop(root);
            SetActiveWindow(root);
            PInvoke.SetForegroundWindow((HWND)root);
        }
        finally
        {
            if (attachedForeground) PInvoke.AttachThreadInput(currentThread, foregroundThread, false);
            if (attachedTarget) PInvoke.AttachThreadInput(currentThread, targetThread, false);
            if (attachedCross) PInvoke.AttachThreadInput(targetThread, foregroundThread, false);
        }
        if ((nint)PInvoke.GetForegroundWindow() != root)
        {
            LockSetForegroundWindow(2);
            AllowSetForegroundWindow(0xFFFFFFFF);
            SendInput(1, [Key(0x12)], Marshal.SizeOf<NativeInput>());
            SendInput(1, [Key(0x12, true)], Marshal.SizeOf<NativeInput>());
            SetWindowPos((HWND)root, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos((HWND)root, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SwitchToThisWindow((HWND)root, true);
            PInvoke.SetForegroundWindow((HWND)root);
        }
        if ((nint)PInvoke.GetForegroundWindow() != root)
            throw new AutomationException("FOCUS_FAILED", "Foreground MISA activation failed.");
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(HWND window, bool altTab);

    [DllImport("user32.dll")]
    private static extern bool LockSetForegroundWindow(uint lockCode);

    private static readonly HWND HWND_TOPMOST = new(-1);
    private static readonly HWND HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(HWND window, HWND insertAfter, int x, int y, int width, int height, uint flags);
    internal static void Press(nint targetHandle, nint windowHandle, int processId, string key, CancellationToken token = default)
    {
        var code = key switch { "Enter" => 0x0D, "Tab" => 0x09, "F3" => 0x72, "Escape" => 0x1B, "ArrowDown" => 0x28, "Delete" => 0x2E, "F9" => 0x78,
            _ => throw new AutomationException("KEY_UNSUPPORTED", "Chỉ hỗ trợ Enter, Tab, F3, F9, Escape, ArrowDown hoặc Delete.") };
        var root = InputRoot(targetHandle, windowHandle, processId);
        var foreground = (nint)PInvoke.GetForegroundWindow();
        PInvoke.GetWindowThreadProcessId((HWND)foreground, out var pid);
        if (foreground != root || pid != processId || !PInvoke.IsWindowEnabled((HWND)foreground))
            throw new AutomationException("FOCUS_LOST", "Foreground không phải editor MISA đã chọn.");
        if (!HasTargetFocus(targetHandle))
            throw new AutomationException("FOCUS_LOST", "Win32 focus không nằm trong ô được chọn.");
        var inputs = new[] { Key((ushort)code), Key((ushort)code, true) };
        token.ThrowIfCancellationRequested();
        if (SendInput(2, inputs, Marshal.SizeOf<NativeInput>()) != 2)
        {
            SendInput(1, [Key((ushort)code, true)], Marshal.SizeOf<NativeInput>());
            throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận đủ key input; không gửi lại.", true);
        }
    }

    internal static void PressFocused(nint windowHandle, int processId, string key, CancellationToken token = default)
    {
        var code = key switch { "Enter" => 0x0D, "Tab" => 0x09, "F3" => 0x72, "Escape" => 0x1B, "ArrowDown" => 0x28, "Delete" => 0x2E, "F9" => 0x78,
            _ => throw new AutomationException("KEY_UNSUPPORTED", "Chỉ hỗ trợ Enter, Tab, F3, F9, Escape, ArrowDown hoặc Delete.") };
        var foreground = (nint)PInvoke.GetForegroundWindow();
        PInvoke.GetWindowThreadProcessId((HWND)foreground, out var pid);
        if (!IsWindowInScope(foreground, windowHandle) || pid != processId || !PInvoke.IsWindowEnabled((HWND)foreground))
            throw new AutomationException("FOCUS_LOST", "Foreground không phải editor MISA đã chọn.");
        token.ThrowIfCancellationRequested();
        var inputs = new[] { Key((ushort)code), Key((ushort)code, true) };
        if (SendInput(2, inputs, Marshal.SizeOf<NativeInput>()) != 2)
        {
            SendInput(1, [Key((ushort)code, true)], Marshal.SizeOf<NativeInput>());
            throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận đủ key input; không gửi lại.", true);
        }
    }

    internal static void Click(int x, int y, nint windowHandle, int processId, nint targetHandle = default)
    {
        Activate(targetHandle == nint.Zero ? windowHandle : targetHandle, windowHandle, processId);
        if (!PInvoke.SetCursorPos(x, y)) throw new AutomationException("FOCUS_FAILED", "Không đưa được con trỏ tới ô MISA.");
        // A topmost window (toast, this tool's own rail) over the point would receive the click instead of MISA.
        var hit = WindowFromPoint(new PointStruct { X = x, Y = y });
        uint hitProcess = 0;
        if (hit == nint.Zero || PInvoke.GetWindowThreadProcessId((HWND)hit, out hitProcess) == 0 || hitProcess != processId)
            throw new AutomationException("FOCUS_LOST", hit != nint.Zero && hitProcess == Environment.ProcessId
                ? "Cửa sổ của MISA Manager (đối chiếu/tool) đang che nút MISA cần bấm; không click."
                : "Có cửa sổ khác che ô MISA cần click; không click.");
        var inputs = new[]
        {
            Mouse(0x0002),
            Mouse(0x0004)
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được click ô MISA.", true);
    }

    private static nint InputRoot(nint targetHandle, nint windowHandle, int processId)
    {
        var root = (nint)PInvoke.GetAncestor((HWND)targetHandle, GET_ANCESTOR_FLAGS.GA_ROOT);
        // R20 can expose a top-level editor without a usable ancestor, while
        // the host is disabled by an owned modal editor. Keep this validated
        // same-process HWND and let activation perform the final check.
        if (root == nint.Zero) root = targetHandle;
        PInvoke.GetWindowThreadProcessId((HWND)targetHandle, out var targetProcess);
        PInvoke.GetWindowThreadProcessId((HWND)windowHandle, out var windowProcess);
        if (root == nint.Zero || targetProcess != processId || windowProcess != processId
            || PInvoke.IsIconic((HWND)root))
            throw new AutomationException("KEY_TARGET_INVALID", "Target không thuộc cửa sổ MISA đang hoạt động.");
        // R20 may expose the voucher as a child or an owned top-level window.
        // Process identity is stable while R20 changes HWND ownership across editor states.
        return root;
    }
    internal static bool HasTargetFocus(nint targetHandle)
    {
        var thread = PInvoke.GetWindowThreadProcessId((HWND)targetHandle, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info)
            && (info.Focus == targetHandle || PInvoke.IsChild((HWND)targetHandle, (HWND)info.Focus));
    }
    private static bool IsWindowInScope(nint foreground, nint windowHandle)
    {
        if (foreground == windowHandle) return true;
        if ((nint)PInvoke.GetAncestor((HWND)foreground, GET_ANCESTOR_FLAGS.GA_ROOT) == windowHandle) return true;
        var owner = foreground;
        var seen = new HashSet<nint>();
        while (owner != nint.Zero && seen.Add(owner))
        {
            if (owner == windowHandle) return true;
            owner = (nint)PInvoke.GetWindow((HWND)owner, GET_WINDOW_CMD.GW_OWNER);
        }
        return false;
    }
    private static NativeInput Key(ushort code, bool up = false) => new()
    { Type = 1, Data = new() { Keyboard = new() { Key = code, Flags = up ? 2u : 0u } } };
    [StructLayout(LayoutKind.Sequential)] private struct PointStruct { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointStruct point);
    private static NativeInput Mouse(uint flags) => new()
    { Type = 0, Data = new() { Mouse = new() { Flags = flags } } };
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] input, int size);

    private static void EnsureMessageQueue()
        => PeekMessage(out _, nint.Zero, 0, 0, 0);

    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }
}


