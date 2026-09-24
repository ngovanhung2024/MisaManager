using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MisaAutomation.Core;

namespace MisaAutomation.Input;

public sealed class WindowsInputProvider : IInputProvider, IDisposable
{
    private readonly BlockingCollection<Action> clipboardQueue = new();
    private readonly Thread clipboardThread;
    public WindowsInputProvider()
    {
        clipboardThread = new Thread(() => { foreach (var action in clipboardQueue.GetConsumingEnumerable()) action(); })
            { IsBackground = true, Name = "MISA clipboard STA" };
        clipboardThread.SetApartmentState(ApartmentState.STA);
        clipboardThread.Start();
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput
    { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    private static void Guard(WindowInfo window)
    {
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var pid);
        if (foreground.ToInt64() != window.Handle || pid != window.ProcessId || !IsWindowEnabled(foreground))
            throw new AutomationException("FOCUS_LOST", "The selected MISA window must be foreground and enabled.");
    }
    private static NativeInput Key(ushort key, bool up = false) => new()
        { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };

    public void Press(WindowInfo window, int key, bool control)
    {
        int[] allowed = [0x0D, 0x09, 0x1B, 0x28, 0x26, 0x27, 0x25, 0x22, 0x21, 0x24, 0x23, 0x72, 0x78, 0x20, 0x41, 0x56, 0x2E];
        if (!allowed.Contains(key) || (control && key is not (0x41 or 0x56)) || (!control && key is 0x41 or 0x56))
            throw new AutomationException("KEY_NOT_ALLOWED", "Key is outside the sales input workflow.");
        Guard(window);
        var events = new List<NativeInput>();
        if (control) events.Add(Key(0x11));
        events.Add(Key((ushort)key)); events.Add(Key((ushort)key, true));
        if (control) events.Add(Key(0x11, true));
        if (SendInput((uint)events.Count, events.ToArray(), Marshal.SizeOf<NativeInput>()) != events.Count)
        {
            // Release only keys this operation may have left held. Never replay input.
            var releases = control ? new[] { Key((ushort)key, true), Key(0x11, true) } : new[] { Key((ushort)key, true) };
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<NativeInput>());
            throw new AutomationException("INPUT_UNCERTAIN", "Windows did not confirm the complete keyboard batch.", true);
        }
    }

    public void Paste(WindowInfo window, string text, Action ensureFocused)
    {
        if (text.Length > 32768 || text.Any(char.IsControl))
            throw new AutomationException("INVALID_TEXT", "Clipboard scalar input contains control characters or exceeds 32768 characters.");
        Guard(window);
        if (text.Length == 0)
        {
            ensureFocused();
            Press(window, 0x41, true);
            try { ensureFocused(); Press(window, 0x2E, false); }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", "Selection was sent; inspect MISA before retrying clear.", true, error); }
            return;
        }
        var prepared = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        clipboardQueue.Add(() =>
        {
            try
            {
                var data = new System.Windows.Forms.DataObject();
                data.SetText(text, System.Windows.Forms.TextDataFormat.UnicodeText);
                System.Windows.Forms.Clipboard.SetDataObject(data, true, 0, 0);
                prepared.SetResult(GetClipboardSequenceNumber());
            }
            catch (Exception error) { prepared.SetException(new AutomationException("CLIPBOARD_UNAVAILABLE", error.Message, false, error)); }
        });
        var sequence = prepared.Task.GetAwaiter().GetResult();
        ensureFocused();
        if (GetClipboardSequenceNumber() != sequence)
            throw new AutomationException("CLIPBOARD_CHANGED", "Clipboard changed before paste; no text was sent.");
        Press(window, 0x41, true);
        try
        {
            ensureFocused();
            if (GetClipboardSequenceNumber() != sequence)
                throw new AutomationException("CLIPBOARD_CHANGED", "Clipboard changed before paste.");
            Press(window, 0x56, true);
        }
        catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", "Selection was sent; inspect MISA before retrying paste.", true, error); }
        // Keep the pasted data available until MISA consumes it; never restore early.
    }
    public void Dispose()
    {
        clipboardQueue.CompleteAdding();
        if (!clipboardThread.Join(TimeSpan.FromSeconds(5)))
            throw new AutomationException("CLIPBOARD_BUSY", "Clipboard worker did not stop.");
        clipboardQueue.Dispose();
    }
}
