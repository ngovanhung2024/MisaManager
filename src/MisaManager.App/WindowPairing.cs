using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace MisaManager.App;

/// <summary>
/// Keeps the comparison window glued to the opposite side of the tool window. Works in device pixels
/// through Win32 so mixed-DPI monitors cannot make the pair drift, and never activates either window.
/// </summary>
internal sealed class WindowPairing : IDisposable
{
    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200;
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000;
    private const uint MonitorDefaultToNearest = 2;
    /// <summary>The popup is wider than the tool so the Excel and MISA columns have room to sit side by side.</summary>
    private const double PopupWidthFactor = 1.2;

    private readonly Window tool;
    private readonly Window popup;
    private bool suppress;
    private bool popupOnRight = true;
    private bool hiddenByPairing;
    private Rect32 expectedTool, expectedPopup;

    public WindowPairing(Window tool, Window popup)
    {
        this.tool = tool;
        this.popup = popup;
        var handle = new WindowInteropHelper(popup).EnsureHandle();
        // Clicking the popup must never take the foreground away from MISA.
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate);
        tool.LocationChanged += ToolChanged;
        tool.SizeChanged += ToolSizeChanged;
        tool.StateChanged += ToolChanged;
        popup.LocationChanged += PopupMoved;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }

    public bool PopupOnRight => popupOnRight;
    public bool CanShowPopup => tool.WindowState == WindowState.Normal && tool.IsVisible;

    public void Dispose()
    {
        tool.LocationChanged -= ToolChanged;
        tool.SizeChanged -= ToolSizeChanged;
        tool.StateChanged -= ToolChanged;
        popup.LocationChanged -= PopupMoved;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
    }

    private void ToolChanged(object? sender, EventArgs e) => Place();
    private void ToolSizeChanged(object sender, SizeChangedEventArgs e) => Place();
    private void DisplayChanged(object? sender, EventArgs e) => tool.Dispatcher.BeginInvoke(Place);

    /// <summary>Recomputes the popup rectangle from the tool window.</summary>
    public void Place()
    {
        if (suppress) return;
        var toolHandle = new WindowInteropHelper(tool).Handle;
        var popupHandle = new WindowInteropHelper(popup).Handle;
        if (toolHandle == 0 || popupHandle == 0 || !GetWindowRect(toolHandle, out var t)) return;
        if (tool.WindowState != WindowState.Normal || !tool.IsVisible)
        {
            if (popup.IsVisible) { hiddenByPairing = true; popup.Visibility = Visibility.Hidden; }
            return;
        }
        if (hiddenByPairing) { hiddenByPairing = false; popup.Visibility = Visibility.Visible; }
        if (t.Width <= 0 || t.Height <= 0) return;

        var work = WorkArea(toolHandle);
        if (work.Width <= 0 || work.Height <= 0) return;
        var width = Math.Min((int)Math.Round(t.Width * PopupWidthFactor), work.Width);
        var height = Math.Min(t.Height, work.Height);
        popupOnRight = t.Left + t.Width / 2 < work.Left + work.Width / 2;
        // Fall back to the other side when the preferred one cannot hold the whole popup.
        if (popupOnRight && t.Right + width > work.Right && t.Left - width >= work.Left) popupOnRight = false;
        else if (!popupOnRight && t.Left - width < work.Left && t.Right + width <= work.Right) popupOnRight = true;
        var left = popupOnRight ? t.Right : t.Left - width;
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        var top = Math.Clamp(t.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
        expectedPopup = new Rect32(left, top, left + width, top + height);
        Apply(popupHandle, expectedPopup, resize: true);
    }

    private void PopupMoved(object? sender, EventArgs e)
    {
        if (suppress || tool.WindowState != WindowState.Normal || !popup.IsVisible) return;
        var toolHandle = new WindowInteropHelper(tool).Handle;
        var popupHandle = new WindowInteropHelper(popup).Handle;
        if (toolHandle == 0 || popupHandle == 0 || !GetWindowRect(popupHandle, out var p)
            || !GetWindowRect(toolHandle, out var t) || p == expectedPopup) return;
        popupOnRight = p.Left + p.Width / 2 >= t.Left + t.Width / 2;
        var work = WorkArea(popupHandle);
        if (work.Width <= 0 || work.Height <= 0) return;
        // Keep the whole pair (tool + wider popup) inside the work area.
        var pairLeft = popupOnRight ? p.Left - t.Width : p.Left;
        pairLeft = Math.Clamp(pairLeft, work.Left, Math.Max(work.Left, work.Right - t.Width - p.Width));
        var left = popupOnRight ? pairLeft : pairLeft + p.Width;
        var top = Math.Clamp(p.Top, work.Top, Math.Max(work.Top, work.Bottom - t.Height));
        expectedTool = new Rect32(left, top, left + t.Width, top + t.Height);
        if (expectedTool != t) Apply(toolHandle, expectedTool, resize: false);
    }

    /// <summary>Pulls a window back inside its monitor's work area (size and position).</summary>
    public static void KeepOnScreen(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || window.WindowState != WindowState.Normal || !GetWindowRect(handle, out var r)) return;
        var work = WorkArea(handle);
        if (work.Width <= 0 || work.Height <= 0) return;
        var width = Math.Min(r.Width, work.Width);
        var height = Math.Min(r.Height, work.Height);
        var left = Math.Clamp(r.Left, work.Left, work.Right - width);
        var top = Math.Clamp(r.Top, work.Top, work.Bottom - height);
        var target = new Rect32(left, top, left + width, top + height);
        if (target != r) SetWindowPos(handle, 0, target.Left, target.Top, target.Width, target.Height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private void Apply(nint handle, Rect32 target, bool resize)
    {
        suppress = true;
        try
        {
            SetWindowPos(handle, 0, target.Left, target.Top, target.Width, target.Height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | (resize ? 0 : SwpNoSize));
        }
        finally { suppress = false; }
    }

    private static Rect32 WorkArea(nint window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), ref info) ? info.Work : default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect32(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect32 rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
}
