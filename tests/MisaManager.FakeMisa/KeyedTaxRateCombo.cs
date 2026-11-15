using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MisaManager.FakeMisa;

/// <summary>
/// R20's "% thuế GTGT" value-list cell (reports 0.2.200/0.2.201, HĐ 18523/18534): outside editing, ValuePattern gives the
/// list's data value — the rate as "8,0000", a code for KCT/KKKNT — while the label shown is in ItemStatus as the
/// Infragistics "DisplayText" token. While edited it exposes its editor and an invokable "[Editor] dropdown button";
/// the option is picked from that list (typed KCT/KKKNT is not taken, see the voucher's commit handler).
/// </summary>
internal sealed class KeyedTaxRateCombo : ComboBox
{
    private bool editing;
    private readonly bool hidden;
    private readonly string keyFault;
    private readonly bool staleStatus;
    private readonly bool textAccessible;
    private string committedText = "";
    private string StatusText => staleStatus && IsDropDownOpen ? committedText : Text;
    public bool HidesList => hidden;

    public KeyedTaxRateCombo(bool opensOnClick = false, bool hidden = false, string keyFault = "", bool staleStatus = false,
        bool textAccessible = false)
    {
        this.hidden = hidden;
        this.keyFault = keyFault;
        this.staleStatus = staleStatus;
        this.textAccessible = textAccessible;
        DropDownOpened += (_, _) =>
        {
            committedText = Text;
            if (staleStatus) FakeLog.Write($"tax_rate committed status frozen at '{committedText}' while list open");
        };
        DropDownClosed += (_, _) =>
        {
            if (keyFault != "reject-selection") return;
            SelectedItem = committedText;
            Text = committedText;
            FakeLog.Write($"tax_rate selection rejected; committed '{Text}'");
        };
        GotKeyboardFocus += (_, _) => editing = true;
        LostKeyboardFocus += (_, _) => editing = false;
        // Tab/Enter commit the cell and leave edit mode, as R20 does.
        PreviewKeyDown += (_, e) => { if (e.Key is Key.Tab or Key.Enter) editing = false; };
        if (hidden)
        {
            // Editable WPF combos consume arrow keys before routed handlers. Intercept before WPF processes them.
            Loaded += (_, _) => InputManager.Current.PreProcessInput += BeforeListKey;
            Unloaded += (_, _) => InputManager.Current.PreProcessInput -= BeforeListKey;
            DropDownOpened += (_, _) => { if (Items.Contains(Text)) SelectedItem = Text; };
            // R20's open list (report 0.2.203): the arrows move the highlighted rate, which the cell shows (WPF's own
            // editable-combo keys do this); the rate is taken when the list closes (Enter), see FakeGrid.CreateCombo.
            SelectionChanged += (_, _) => { if (IsDropDownOpen) FakeLog.Write($"tax_rate list moved to '{SelectedItem}'"); };
            DropDownClosed += (_, _) => FakeLog.Write($"tax_rate list closed on '{Text}'");
        }
        if (!opensOnClick) return;
        // R20 drop-down-list cell (report 0.2.202, HĐ 18534): a click on the cell drops the list and keeps it dropped;
        // only the arrow toggles it.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            for (var at = e.OriginalSource as DependencyObject; at is not null && at != this;
                 at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
                if (at is ToggleButton or ComboBoxItem) return;
            if (IsDropDownOpen) e.Handled = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (IsDropDownOpen) return;
                IsDropDownOpen = true;
                FakeLog.Write("tax_rate list dropped by the cell click");
            });
        };
    }

    private void BeforeListKey(object sender, PreProcessInputEventArgs input)
    {
        if (!IsDropDownOpen || input.StagingItem.Input is not KeyEventArgs e
            || e.RoutedEvent != Keyboard.PreviewKeyDownEvent || e.Key is not (Key.Down or Key.Up or Key.Enter)) return;
        FakeLog.Write($"tax_rate list key {e.Key}");
        if (keyFault == "close" && e.Key is Key.Down or Key.Up)
        {
            input.Cancel();
            IsDropDownOpen = false;
        }
        if (keyFault == "ignore-arrows" && e.Key is Key.Down or Key.Up
            || keyFault == "ignore-enter" && e.Key == Key.Enter) input.Cancel();
    }

    private static string DataValue(string text) => text.Trim().ToUpperInvariant() switch
    {
        "KCT" => "-1",
        "KKKNT" => "-2",
        var rate => Ui.TaxRate(rate) is { } shown ? Ui.RateValue(shown).ToString("0.0000", FakeStore.Vi) : rate
    };

    private static ToggleButton? FindToggle(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ToggleButton toggle) return toggle;
            if (FindToggle(child) is { } nested) return nested;
        }
        return null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    protected override DependencyObject GetContainerForItemOverride() => hidden ? new HiddenItem(textAccessible) : base.GetContainerForItemOverride();

    // Drawn like an option but not a UIA list item, as R20's rate list (report 0.2.203).
    private sealed class HiddenItem(bool textAccessible) : ComboBoxItem
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new HiddenItemPeer(this, textAccessible);
    }

    private sealed class HiddenItemPeer(HiddenItem owner, bool textAccessible) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => "ValueListItem";
        protected override string GetNameCore() => textAccessible ? owner.Content?.ToString() ?? "" : "";
        protected override List<AutomationPeer>? GetChildrenCore() => textAccessible ? base.GetChildrenCore() : [];
    }

    private sealed class Peer(KeyedTaxRateCombo owner) : ComboBoxAutomationPeer(owner), IValueProvider
    {
        private R20UnitCombo.DropDownButtonPeer? button;

        public override object GetPattern(PatternInterface pattern)
            => pattern == PatternInterface.Value ? this : base.GetPattern(pattern);

        protected override List<AutomationPeer>? GetChildrenCore()
        {
            if (!owner.editing && !owner.IsDropDownOpen) return null;
            var children = base.GetChildrenCore() ?? [];
            if (owner.hidden) children.RemoveAll(peer => peer is not TextBoxAutomationPeer);
            if (button is null && FindToggle(owner) is { } toggle) button = new R20UnitCombo.DropDownButtonPeer(toggle, owner);
            if (button is not null) children.Add(button);
            return children;
        }

        protected override string GetItemStatusCore() => $"DisplayText^IG_TOKEN_SEPERATOR^{owner.StatusText}^IG_TOKEN_DELIMETER^";

        string IValueProvider.Value => DataValue(owner.StatusText);
        bool IValueProvider.IsReadOnly => false;
        void IValueProvider.SetValue(string value) => owner.Text = value;
    }
}
