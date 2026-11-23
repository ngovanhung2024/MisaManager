using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace MisaManager.FakeMisa;

/// <summary>A named container that shows up in UIA (plain WPF panels do not), like R20's module/scope panels.</summary>
public sealed class ScopePanel : Grid
{
    public ScopePanel(string automationId, string name = "")
    {
        AutomationProperties.SetAutomationId(this, automationId);
        if (name.Length > 0) AutomationProperties.SetName(this, name);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ScopePeer(this);

    private sealed class ScopePeer(ScopePanel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => "MISAPanel";
    }
}

internal static class Ui
{
    public static readonly Brush Chrome = new SolidColorBrush(Color.FromRgb(0xDC, 0xE3, 0xF3));
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xF7, 0xC9, 0x6B));

    /// <summary>
    /// R20 windows own a menu/toolbar manager that consumes a bare Alt. Without it Windows enters
    /// system-menu mode on Alt (sent by the automation's foreground activation) and eats the next key.
    /// </summary>
    public static void IgnoreBareAlt(this System.Windows.Window window)
        => window.SourceInitialized += (_, _) =>
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(window).Handle)?.AddHook(
                (nint _, int message, nint wParam, nint lParam, ref bool handled) =>
                {
                    const int WM_SYSCOMMAND = 0x0112, SC_KEYMENU = 0xF100;
                    if (message == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU && lParam == 0) handled = true;
                    return nint.Zero;
                });

    public static T Id<T>(this T element, string automationId) where T : DependencyObject
    {
        if (automationId.Length > 0) AutomationProperties.SetAutomationId(element, automationId);
        return element;
    }

    public static TextBox Text(string automationId = "", double width = double.NaN, string value = "", bool readOnly = false)
        => new TextBox { Text = value, Width = width, Height = 22, Margin = new Thickness(2), IsReadOnly = readOnly, VerticalContentAlignment = VerticalAlignment.Center }
            .Id(automationId);

    public static ComboBox Combo(string automationId, IEnumerable<string> options, double width = double.NaN, string value = "")
        => new ComboBox { IsEditable = true, ItemsSource = options.ToArray(), Text = value, Width = width, Height = 22, Margin = new Thickness(2) }
            .Id(automationId);

    public static Button Button(string caption, bool enabled = true, string automationId = "", double width = double.NaN)
        => new Button
        {
            Content = caption, IsEnabled = enabled, Margin = new Thickness(1), Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 40, Width = width, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent
        }.Id(automationId);

    public static Button CustomButton(string caption, bool enabled = true, string automationId = "", double width = double.NaN)
        => new CustomCommandButton
        {
            Content = caption, IsEnabled = enabled, Margin = new Thickness(1), Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 40, Width = width, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent
        }.Id(automationId);

    public static Button DynamicButton(string caption, bool enabled = true, string automationId = "", double width = double.NaN)
        => new DynamicCommandButton
        {
            Content = caption, IsEnabled = enabled, Margin = new Thickness(1), Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 40, Width = width, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent
        }.Id(automationId);

    private sealed class CustomCommandButton : Button
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(CustomCommandButton owner) : ButtonAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        }
    }

    private sealed class DynamicCommandButton : Button
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(DynamicCommandButton owner) : ButtonAutomationPeer(owner)
        {
            protected override string GetNameCore() => owner.Content?.ToString() ?? "";
        }
    }

    /// <summary>Two-column form: label cells (Text, no AutomationId) and the given editors.</summary>
    public static Grid Form(double labelWidth, params (string Label, UIElement Editor)[] rows)
    {
        var grid = new Grid { Margin = new Thickness(6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < rows.Length; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[index].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
            Grid.SetRow(label, index);
            Grid.SetRow(rows[index].Editor, index);
            Grid.SetColumn(rows[index].Editor, 1);
            grid.Children.Add(label);
            grid.Children.Add(rows[index].Editor);
        }
        return grid;
    }

    public static GroupBox Group(string header, UIElement content) => new() { Header = header, Content = content, Margin = new Thickness(4) };

    public static string Money(decimal value) => value.ToString("N0", FakeStore.Vi);
    public static string Quantity(decimal value) => value.ToString("N3", FakeStore.Vi);
    public static string Price(decimal value) => value.ToString("N2", FakeStore.Vi);

    public static bool TryNumber(string text, out decimal value)
        => decimal.TryParse(text.Trim(), System.Globalization.NumberStyles.Number, FakeStore.Vi, out value);

    /// <summary>"5", "5%", "5,0000" → "5%"; KCT/KKKNT stay textual; null when not a rate.</summary>
    public static string? TaxRate(string text)
    {
        var value = text.Trim();
        if (value.Equals("KCT", StringComparison.OrdinalIgnoreCase) || value.Equals("KKKNT", StringComparison.OrdinalIgnoreCase))
            return value.ToUpperInvariant();
        return TryNumber(value.TrimEnd('%'), out var rate) && rate is >= 0 and <= 100
            ? rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%" : null;
    }

    public static decimal RateValue(string rate)
        => decimal.TryParse(rate.TrimEnd('%'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
}
