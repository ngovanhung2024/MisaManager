using FlaUI.Core.Definitions;
using MisaAutomation.Core;

namespace MisaAutomation.UIA;

// FlaUI enums are not the native UIA 50000-series identifiers used by the port.
internal static class ControlKinds
{
    private static readonly ControlType[] NativeOrder =
    [
        ControlType.Button, ControlType.Calendar, ControlType.CheckBox, ControlType.ComboBox,
        ControlType.Edit, ControlType.Hyperlink, ControlType.Image, ControlType.ListItem,
        ControlType.List, ControlType.Menu, ControlType.MenuBar, ControlType.MenuItem,
        ControlType.ProgressBar, ControlType.RadioButton, ControlType.ScrollBar, ControlType.Slider,
        ControlType.Spinner, ControlType.StatusBar, ControlType.Tab, ControlType.TabItem,
        ControlType.Text, ControlType.ToolBar, ControlType.ToolTip, ControlType.Tree,
        ControlType.TreeItem, ControlType.Custom, ControlType.Group, ControlType.Thumb,
        ControlType.DataGrid, ControlType.DataItem, ControlType.Document, ControlType.SplitButton,
        ControlType.Window, ControlType.Pane, ControlType.Header, ControlType.HeaderItem,
        ControlType.Table, ControlType.TitleBar, ControlType.Separator, ControlType.SemanticZoom,
        ControlType.AppBar
    ];
    public static ControlType FromNative(int value) => value >= 50000 && value < 50000 + NativeOrder.Length
        ? NativeOrder[value - 50000] : throw new AutomationException("INVALID_CONTROL_TYPE", "Unknown native control type: " + value);
    public static int ToNative(ControlType value)
    {
        var index = Array.IndexOf(NativeOrder, value);
        return index < 0 ? 0 : 50000 + index;
    }
}
