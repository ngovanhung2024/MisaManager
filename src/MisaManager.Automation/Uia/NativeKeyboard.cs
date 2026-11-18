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
        var movedToConsole = false;
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            // Without an input desktop (RDP closed/minimised, screen locked) no activation can succeed: report that
            // instead of FOCUS_FAILED, and send no Alt key into the session.
            DesktopSession.ThrowIfUnavailable();
            try { ActivateOnce(targetHandle, windowHandle, processId); return; }
            catch (AutomationException error) when (error.Code == "FOCUS_FAILED")
            {
                if (attempt >= 9)
                {
                    // One more round after the session was moved to the console (minimised RDP client).
                    if (!movedToConsole && DesktopSession.ThrowIfUnavailable(activationFailed: true))
                    {
                        movedToConsole = true;
                        attempt = -1;
                        continue;
                    }
                    throw;
                }
                if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
            }
        }
    }

    internal static bool IsForegroundRoot(nint handle)
        => handle != nint.Zero && (nint)PInvoke.GetAncestor((HWND)handle, GET_ANCESTOR_FLAGS.GA_ROOT) == (nint)PInvoke.GetForegroundWindow();

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
        var code = key switch { "Enter" => 0x0D, "Tab" => 0x09, "F3" => 0x72, "Escape" => 0x1B, "ArrowDown" => 0x28, "ArrowUp" => 0x26, "Delete" => 0x2E, "F9" => 0x78,
            _ => throw new AutomationException("KEY_UNSUPPORTED", "Chỉ hỗ trợ Enter, Tab, F3, F9, Escape, ArrowDown, ArrowUp hoặc Delete.") };
        var root = InputRoot(targetHandle, windowHandle, processId);
        var foreground = (nint)PInvoke.GetForegroundWindow();
        PInvoke.GetWindowThreadProcessId((HWND)foreground, out var pid);
        if (foreground != root || pid != processId || !PInvoke.IsWindowEnabled((HWND)foreground))
            throw new AutomationException("FOCUS_LOST", "Foreground không phải editor MISA đã chọn.");
        if (!HasTargetFocus(targetHandle))
            throw new AutomationException("FOCUS_LOST", "Win32 focus không nằm trong ô được chọn.");
        var inputs = new[] { Key((ushort)code), Key((ushort)code, true) };
        token.ThrowIfCancellationRequested();
        // A session closed/locked since activation would drop the key: report it before anything is sent.
        DesktopSession.ThrowIfUnavailable();
        if (SendInput(2, inputs, Marshal.SizeOf<NativeInput>()) != 2)
        {
            SendInput(1, [Key((ushort)code, true)], Marshal.SizeOf<NativeInput>());
            throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận đủ key input; không gửi lại.", true);
        }
    }

    internal static void ReplaceText(nint targetHandle, nint windowHandle, int processId, string text, CancellationToken token)
    {
        if (text.Length is 0 or > 32768 || text.Any(char.IsControl))
            throw new AutomationException("INVALID_TEXT", "Chữ cần gõ không hợp lệ.");
        var root = InputRoot(targetHandle, windowHandle, processId);
        var foreground = (nint)PInvoke.GetForegroundWindow();
        PInvoke.GetWindowThreadProcessId((HWND)foreground, out var pid);
        if (foreground != root || pid != processId || !PInvoke.IsWindowEnabled((HWND)foreground)
            || !HasTargetFocus(targetHandle))
            throw new AutomationException("FOCUS_LOST", "Focus không nằm trong editor MISA đã chọn; không gõ.");
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
            throw new AutomationException("FOCUS_LOST", "Phím Shift/Ctrl/Alt/Windows đang được giữ; không gõ.");
        token.ThrowIfCancellationRequested();
        DesktopSession.ThrowIfUnavailable();
        var inputs = new List<NativeInput> { Key(0x11), Key(0x41), Key(0x41, true), Key(0x11, true) };
        foreach (var character in text)
        {
            inputs.Add(new() { Type = 1, Data = new() { Keyboard = new() { Scan = character, Flags = 4 } } });
            inputs.Add(new() { Type = 1, Data = new() { Keyboard = new() { Scan = character, Flags = 6 } } });
        }
        if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeInput>()) != inputs.Count)
        {
            SendInput(2, [Key(0x41, true), Key(0x11, true)], Marshal.SizeOf<NativeInput>());
            throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận đủ phím gõ chữ; không gửi lại.", true);
        }
    }

    internal static void PressFocused(nint windowHandle, int processId, string key, CancellationToken token = default,
        nint scopeHandle = default)
    {
        var code = key switch { "Enter" => 0x0D, "Tab" => 0x09, "F3" => 0x72, "Escape" => 0x1B, "ArrowDown" => 0x28, "ArrowUp" => 0x26, "Delete" => 0x2E, "F9" => 0x78,
            _ => throw new AutomationException("KEY_UNSUPPORTED", "Chỉ hỗ trợ Enter, Tab, F3, F9, Escape, ArrowDown, ArrowUp hoặc Delete.") };
        var foreground = (nint)PInvoke.GetForegroundWindow();
        PInvoke.GetWindowThreadProcessId((HWND)foreground, out var pid);
        if (!IsWindowInScope(foreground, windowHandle) && !InSameRoot(foreground, scopeHandle) || pid != processId || !PInvoke.IsWindowEnabled((HWND)foreground))
            throw new AutomationException("FOCUS_LOST", "Foreground không phải editor MISA đã chọn.");
        token.ThrowIfCancellationRequested();
        DesktopSession.ThrowIfUnavailable();
        var inputs = new[] { Key((ushort)code), Key((ushort)code, true) };
        if (SendInput(2, inputs, Marshal.SizeOf<NativeInput>()) != 2)
        {
            SendInput(1, [Key((ushort)code, true)], Marshal.SizeOf<NativeInput>());
            throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận đủ key input; không gửi lại.", true);
        }
    }

    internal static void Click(int x, int y, nint windowHandle, int processId, nint targetHandle = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            Activate(targetHandle == nint.Zero ? windowHandle : targetHandle, windowHandle, processId);
            WaitForUserButtonsReleased();
            if (!PInvoke.SetCursorPos(x, y)) throw new AutomationException("FOCUS_FAILED", "Không đưa được con trỏ tới ô MISA.");
            // A topmost window (toast, this tool's own rail) over the point would receive the click instead of MISA.
            var hit = WindowFromPoint(new PointStruct { X = x, Y = y });
            uint hitProcess = 0;
            if (hit == nint.Zero || PInvoke.GetWindowThreadProcessId((HWND)hit, out hitProcess) == 0 || hitProcess != processId)
                throw new AutomationException("FOCUS_LOST", hit != nint.Zero && hitProcess == Environment.ProcessId
                    ? "Cửa sổ của MISA Manager (đối chiếu/tool) đang che nút MISA cần bấm; không click."
                    : "Có cửa sổ khác che ô MISA cần click; không click.");
            // Re-activate an unowned voucher if MISA's main window rose after focus; no click was sent yet.
            if (targetHandle != nint.Zero && PInvoke.GetAncestor((HWND)hit, GET_ANCESTOR_FLAGS.GA_ROOT)
                    != PInvoke.GetAncestor((HWND)targetHandle, GET_ANCESTOR_FLAGS.GA_ROOT))
            {
                if (attempt >= 2) throw new AutomationException("FOCUS_LOST", "Cửa sổ MISA khác đang che ô cần click; không click.");
                Thread.Sleep(40);
                continue;
            }
            SendClick(x, y);
            return;
        }
    }

    internal static void DoubleClick(int x, int y, nint windowHandle, int processId, nint targetHandle = default)
    {
        Activate(targetHandle == nint.Zero ? windowHandle : targetHandle, windowHandle, processId);
        WaitForUserButtonsReleased();
        if (!PInvoke.SetCursorPos(x, y)) throw new AutomationException("FOCUS_FAILED", "Không đưa được con trỏ tới dòng MISA.");
        var hit = WindowFromPoint(new PointStruct { X = x, Y = y });
        if (hit == nint.Zero || PInvoke.GetWindowThreadProcessId((HWND)hit, out var owner) == 0 || owner != processId)
            throw new AutomationException("FOCUS_LOST", "Có cửa sổ khác che dòng MISA cần mở.");
        if (targetHandle != nint.Zero && PInvoke.GetAncestor((HWND)hit, GET_ANCESTOR_FLAGS.GA_ROOT)
                != PInvoke.GetAncestor((HWND)targetHandle, GET_ANCESTOR_FLAGS.GA_ROOT))
            throw new AutomationException("FOCUS_LOST", "Cửa sổ MISA khác đang che dòng cần mở.");
        SendDoubleClick(x, y);
    }

    /// <summary>
    /// Clicks an item of a MISA popup list that is open now. Unlike <see cref="Click"/> nothing is activated first: the
    /// Alt key and foreground switch of activation close R20's value list, and the click then lands on the grid row
    /// below it (report 0.2.173, HĐ 167). The window under the point is checked right before the click is sent.
    /// </summary>
    internal static void ClickOpenList(int x, int y, int processId, Func<nint, bool> isList)
    {
        DesktopSession.ThrowIfUnavailable();
        var foreground = (nint)PInvoke.GetForegroundWindow();
        if (foreground == nint.Zero || PInvoke.GetWindowThreadProcessId((HWND)foreground, out var owner) == 0 || owner != processId)
            throw new AutomationException("GRID_DROPDOWN_MISSING", "MISA không còn ở trước nên danh sách đã đóng; không click.");
        WaitForUserButtonsReleased();
        if (!PInvoke.SetCursorPos(x, y)) throw new AutomationException("FOCUS_FAILED", "Không đưa được con trỏ tới danh sách MISA.");
        var hit = WindowAt(x, y);
        if (hit == nint.Zero || PInvoke.GetWindowThreadProcessId((HWND)hit, out var hitProcess) == 0 || hitProcess != processId || !isList(hit))
            throw new AutomationException("GRID_DROPDOWN_MISSING", "Danh sách MISA đã đóng trước khi click; không click xuống bảng.");
        SendClick(x, y);
    }

    /// <summary>Scrolls an already-open MISA popup without activating the window again.</summary>
    internal static void ScrollOpenListDown(int x, int y, int processId, Func<nint, bool> isList)
    {
        DesktopSession.ThrowIfUnavailable();
        var foreground = (nint)PInvoke.GetForegroundWindow();
        if (foreground == nint.Zero || PInvoke.GetWindowThreadProcessId((HWND)foreground, out var owner) == 0 || owner != processId)
            throw new AutomationException("GRID_DROPDOWN_MISSING", "MISA không còn ở trước nên danh sách đã đóng; không cuộn.");
        WaitForUserButtonsReleased();
        if (!PInvoke.SetCursorPos(x, y)) throw new AutomationException("FOCUS_FAILED", "Không đưa được con trỏ tới danh sách MISA.");
        var hit = WindowAt(x, y);
        if (hit == nint.Zero || PInvoke.GetWindowThreadProcessId((HWND)hit, out var hitProcess) == 0
            || hitProcess != processId || !isList(hit))
            throw new AutomationException("GRID_DROPDOWN_MISSING", "Danh sách MISA đã đóng trước khi cuộn.");
        var input = new NativeInput
        {
            Type = 0,
            Data = new()
            {
                Mouse = new MouseInput { Data = unchecked((uint)-WHEEL_DELTA), Flags = MouseWheel }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1)
            throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được cuộn danh sách MISA.", true);
    }

    private static void SendClick(int x, int y)
    {
        // Every event carries the absolute target, and SendInput queues the batch without
        // interleaving: a user moving the mouse meanwhile cannot redirect the press or release.
        var (dx, dy) = AbsolutePoint(x, y);
        const uint at = MouseMove | MouseAbsolute | MouseVirtualDesk;
        var inputs = new[]
        {
            Mouse(at, dx, dy),
            Mouse(at | MouseLeftDown, dx, dy),
            Mouse(at | MouseLeftUp, dx, dy)
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được click ô MISA.", true);
    }

    private static void SendDoubleClick(int x, int y)
    {
        var (dx, dy) = AbsolutePoint(x, y);
        const uint at = MouseMove | MouseAbsolute | MouseVirtualDesk;
        var inputs = new[]
        {
            Mouse(at, dx, dy), Mouse(at | MouseLeftDown, dx, dy), Mouse(at | MouseLeftUp, dx, dy),
            Mouse(at | MouseLeftDown, dx, dy), Mouse(at | MouseLeftUp, dx, dy)
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được mở dòng MISA.", true);
    }

    private const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004,
        MouseWheel = 0x0800;
    private const int WHEEL_DELTA = 120;
    private const uint MouseVirtualDesk = 0x4000, MouseAbsolute = 0x8000;

    private static (int X, int Y) AbsolutePoint(int x, int y)
    {
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = Math.Max(2, GetSystemMetrics(78)); var height = Math.Max(2, GetSystemMetrics(79));
        return ((int)Math.Round((x - left) * 65535d / (width - 1)), (int)Math.Round((y - top) * 65535d / (height - 1)));
    }

    // A physically held button would merge with the injected press (drag/selection instead of a click).
    private static void WaitForUserButtonsReleased()
    {
        for (var attempt = 0; (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0; attempt++)
        {
            if (attempt >= 40) throw new AutomationException("FOCUS_LOST", "Chuột đang được giữ; thả chuột để tool bấm tiếp.");
            Thread.Sleep(50);
        }
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

    // Infragistics may host its live editor outside the virtual cell's UIA subtree.
    internal static nint FocusedControlInCell(nint scopeHandle, int processId, System.Drawing.Rectangle bounds)
    {
        var thread = PInvoke.GetWindowThreadProcessId((HWND)scopeHandle, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || info.Focus == nint.Zero
            || PInvoke.GetWindowThreadProcessId((HWND)info.Focus, out var focusedProcess) == 0 || focusedProcess != processId
            || !InSameRoot(info.Focus, scopeHandle) || !PInvoke.IsWindowVisible((HWND)info.Focus)
            || !PInvoke.IsWindowEnabled((HWND)info.Focus) || !PInvoke.GetWindowRect((HWND)info.Focus, out var rect))
            return nint.Zero;
        var editorBounds = System.Drawing.Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
        bounds.Inflate(4, 4);
        return editorBounds.Width > 0 && editorBounds.Height > 0 && bounds.Contains(editorBounds) ? info.Focus : nint.Zero;
    }
    // Report 0.2.191, invoice 205: the voucher form can be a top-level window without owner; the key may go to that form
    // (the window holding the target), never to another one.
    internal static bool InSameRoot(nint foreground, nint scopeHandle)
        => scopeHandle != nint.Zero && foreground != nint.Zero
            && PInvoke.GetAncestor((HWND)foreground, GET_ANCESTOR_FLAGS.GA_ROOT) == PInvoke.GetAncestor((HWND)scopeHandle, GET_ANCESTOR_FLAGS.GA_ROOT);

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
    /// <summary>The native window that would receive a click at this screen point.</summary>
    internal static nint WindowAt(int x, int y) => WindowFromPoint(new PointStruct { X = x, Y = y });

    [StructLayout(LayoutKind.Sequential)] private struct PointStruct { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointStruct point);
    private static NativeInput Mouse(uint flags, int x, int y) => new()
    { Type = 0, Data = new() { Mouse = new() { X = x, Y = y, Flags = flags } } };
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
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


