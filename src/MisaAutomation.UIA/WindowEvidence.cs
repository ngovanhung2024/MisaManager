using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MisaAutomation.Core;

namespace MisaAutomation.UIA;

internal static class WindowEvidence
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    public static void Capture(WindowInfo window, string path)
    {
        WindowContext.Read(window.Handle, window.ProcessId);
        if (!GetWindowRect((nint)window.Handle, out var rect)) throw new IOException("Cannot read MISA bounds.");
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        if (width < 1 || height < 1 || (long)width * height > 30000000) throw new IOException("Invalid MISA capture dimensions.");
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        var dc = graphics.GetHdc();
        try
        {
            // Ask the selected HWND to paint itself; never capture a foreign foreground app.
            if (!PrintWindow((nint)window.Handle, dc, 2)) throw new IOException("MISA did not render error evidence.");
        }
        finally { graphics.ReleaseHdc(dc); }
        WindowContext.Read(window.Handle, window.ProcessId);
        bitmap.Save(path, ImageFormat.Png);
    }
}
