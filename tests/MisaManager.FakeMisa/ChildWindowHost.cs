using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MisaManager.FakeMisa;

/// <summary>
/// Hosts WPF content in its own child HWND. R20 is WinForms, so its module panels are real windows;
/// the automation resolves a command's HWND by walking up to the bound scope and needs one there.
/// </summary>
public sealed class ChildWindowHost(FrameworkElement content, string windowName) : HwndHost
{
    private const int ChildStyle = 0x40000000 | 0x10000000 | 0x02000000; // WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN
    private HwndSource? source;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var parameters = new HwndSourceParameters(windowName)
        {
            ParentWindow = hwndParent.Handle,
            WindowStyle = ChildStyle,
            Width = Math.Max(1, (int)ActualWidth),
            Height = Math.Max(1, (int)ActualHeight)
        };
        source = new HwndSource(parameters) { RootVisual = content, SizeToContent = SizeToContent.Manual };
        return new HandleRef(this, source.Handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => source?.Dispose();

    // The child window's root visual does not follow the host's slot by itself.
    protected override Size ArrangeOverride(Size finalSize)
    {
        content.Width = finalSize.Width;
        content.Height = finalSize.Height;
        return base.ArrangeOverride(finalSize);
    }
}
