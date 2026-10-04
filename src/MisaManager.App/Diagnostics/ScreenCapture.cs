using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MisaManager.App;

/// <summary>Best-effort PNG capture of the whole virtual screen (all monitors, physical pixels).</summary>
internal static class ScreenCapture
{
    /// <summary>Runs the capture on a short-lived STA thread (WPF imaging objects expect one).</summary>
    public static Task<byte[]?> CaptureVirtualScreenAsync()
    {
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => completion.SetResult(CaptureVirtualScreen())) { IsBackground = true, Name = "ScreenCapture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static byte[]? CaptureVirtualScreen()
    {
        // The app manifest is DPI-unaware; switch this thread to per-monitor-v2 so the
        // metrics and the bitmap are in physical pixels and nothing is cropped or blurred.
        var previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            var x = GetSystemMetrics(76);
            var y = GetSystemMetrics(77);
            var width = GetSystemMetrics(78);
            var height = GetSystemMetrics(79);
            return width <= 0 || height <= 0 ? null : CaptureRegion(x, y, width, height);
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning("Screen capture failed: {0}", error.Message);
            return null;
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    private static byte[]? CaptureRegion(int x, int y, int width, int height)
    {
        var screen = GetDC(0);
        if (screen == 0) return null;
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020 | 0x40000000)) return null;
            SelectObject(memory, old);
            old = 0;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            if (old != 0) SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int operation);
}
