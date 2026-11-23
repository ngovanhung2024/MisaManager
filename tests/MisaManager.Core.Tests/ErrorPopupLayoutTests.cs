using System.Xml.Linq;

namespace MisaManager.Core.Tests;

public sealed class ErrorPopupLayoutTests
{
    [Fact]
    public void ErrorPopupKeepsActionsVisibleWhenDiagnosticTextIsLong()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var popup = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ErrorPopup");

        Assert.Equal("520", (string?)popup.Attribute("Height"));
        Assert.Equal("True", (string?)popup.Attribute("ClipToBounds"));

        var rows = popup.Descendants().Single(element => element.Name.LocalName == "Grid")
            .Descendants().Where(element => element.Name.LocalName == "RowDefinition").ToArray();
        Assert.Equal("*", (string?)rows[3].Attribute("Height"));

        var actions = popup.Descendants().Single(element => element.Name.LocalName == "StackPanel"
            && (string?)element.Attribute("DockPanel.Dock") == "Right");
        Assert.Contains(actions.Descendants().Where(element => element.Name.LocalName == "Button"),
            button => (string?)button.Attribute("Content") == "Copy lỗi");
        Assert.Contains(actions.Descendants().Where(element => element.Name.LocalName == "Button"),
            button => (string?)button.Attribute("Content") == "Xuất báo cáo");
    }
}
