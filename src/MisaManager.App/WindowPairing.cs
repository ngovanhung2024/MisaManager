using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace MisaManager.App;

/// <summary>
/// Places the comparison window at the opposite edge of the tool's monitor (tool on the right → comparison on the
/// left), with the tool's width and height, so MISA's voucher stays uncovered between them. Works in device pixels
/// through Win32 so mixed-DPI monitors cannot make it drift, and never activates either window.
/// </summary>
internal sealed class WindowPairing : IDisposable
{
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200;
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000;
    private const uint MonitorDefaultToNearest = 2;

    private readonly Window tool;
    private readonly Window popup;
    private bool popupOnRight;
    private bool hiddenByPairing;

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
        tool.IsVisibleChanged += ToolVisibleChanged;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }

    public bool PopupOnRight => popupOnRight;
    public bool CanShowPopup => ToolShown();

    public void Dispose()
    {
        tool.LocationChanged -= ToolChanged;
        tool.SizeChanged -= ToolSizeChanged;
        tool.StateChanged -= ToolChanged;
        tool.IsVisibleChanged -= ToolVisibleChanged;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
    }

    private void ToolChanged(object? sender, EventArgs e) => Place();
    private void ToolSizeChanged(object sender, SizeChangedEventArgs e) => Place();
    private void ToolVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Place();
    private void DisplayChanged(object? sender, EventArgs e) => tool.Dispatcher.BeginInvoke(Place);

    // WPF raises LocationChanged for a minimize before WindowState changes; IsIconic sees the real state,
    // so the popup is never sized from the parked (-32000) minimized rectangle.
    private bool ToolShown()
    {
        var handle = new WindowInteropHelper(tool).Handle;
        return tool.IsVisible && tool.WindowState == WindowState.Normal && handle != 0 && !IsIconic(handle);
    }

    /// <summary>Recomputes the popup rectangle from the tool window.</summary>
    public void Place()
    {
        var toolHandle = new WindowInteropHelper(tool).Handle;
        var popupHandle = new WindowInteropHelper(popup).Handle;
        if (toolHandle == 0 || popupHandle == 0) return;
        if (!ToolShown())
        {
            if (popup.IsVisible) { hiddenByPairing = true; popup.Visibility = Visibility.Hidden; }
            return;
        }
        if (!GetWindowRect(toolHandle, out var t) || t.Width <= 0 || t.Height <= 0) return;
        var work = WorkArea(toolHandle);
        if (work.Width <= 0 || work.Height <= 0) return;
        if (hiddenByPairing) { hiddenByPairing = false; popup.Visibility = Visibility.Visible; }
        var width = Math.Min(t.Width, work.Width);
        var height = Math.Min(t.Height, work.Height);
        popupOnRight = t.Left + t.Width / 2 < work.Left + work.Width / 2;
        var left = popupOnRight ? work.Right - width : work.Left;
        var top = Math.Clamp(t.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
        var target = new Rect32(left, top, left + width, top + height);
        if (GetWindowRect(popupHandle, out var current) && current == target) return;
        SetWindowPos(popupHandle, 0, target.Left, target.Top, target.Width, target.Height, SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>Pulls a window back inside its monitor's work area (size and position).</summary>
    public static void KeepOnScreen(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || window.WindowState != WindowState.Normal || IsIconic(handle) || !GetWindowRect(handle, out var r)) return;
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
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
}
