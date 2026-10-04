using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace MisaManager.Integration.Tests;

/// <summary>Raw-view UIA tree of the fake, written next to the screenshot when a UI test fails.</summary>
public static class UiaDump
{
    public static string Dump(nint window, int maxDepth = 9, int maxNodes = 600)
    {
        var text = new StringBuilder();
        try
        {
            using var automation = new UIA3Automation();
            var walker = automation.TreeWalkerFactory.GetRawViewWalker();
            var count = 0;
            void Visit(AutomationElement element, int depth)
            {
                if (depth > maxDepth || ++count > maxNodes) return;
                string Read(Func<string> read) { try { return read(); } catch (Exception error) { return "!" + error.GetType().Name; } }
                text.Append(' ', depth * 2)
                    .Append(Read(() => element.Properties.ControlType.ValueOrDefault.ToString())).Append(" [")
                    .Append(Read(() => element.Properties.AutomationId.ValueOrDefault ?? "")).Append("] '")
                    .Append(Read(() => element.Properties.Name.ValueOrDefault ?? "")).Append("' hwnd=")
                    .Append(Read(() => element.Properties.NativeWindowHandle.ValueOrDefault.ToString()))
                    .Append(" off=").Append(Read(() => element.Properties.IsOffscreen.ValueOrDefault.ToString()))
                    .Append(" en=").Append(Read(() => element.Properties.IsEnabled.ValueOrDefault.ToString()))
                    .Append(" rect=").Append(Read(() => element.Properties.BoundingRectangle.ValueOrDefault.ToString()))
                    .Append(" class=").Append(Read(() => element.Properties.ClassName.ValueOrDefault ?? ""))
                    .AppendLine();
                for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
                    Visit(child, depth + 1);
            }
            Visit(automation.FromHandle(window), 0);
        }
        catch (Exception error) { text.AppendLine("UIA dump failed: " + error); }
        return text.ToString();
    }
}
