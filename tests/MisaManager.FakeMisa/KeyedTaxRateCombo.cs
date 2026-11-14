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

    public KeyedTaxRateCombo()
    {
        GotKeyboardFocus += (_, _) => editing = true;
        LostKeyboardFocus += (_, _) => editing = false;
        // Tab/Enter commit the cell and leave edit mode, as R20 does.
        PreviewKeyDown += (_, e) => { if (e.Key is Key.Tab or Key.Enter) editing = false; };
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

    private sealed class Peer(KeyedTaxRateCombo owner) : ComboBoxAutomationPeer(owner), IValueProvider
    {
        private R20UnitCombo.DropDownButtonPeer? button;

        public override object GetPattern(PatternInterface pattern)
            => pattern == PatternInterface.Value ? this : base.GetPattern(pattern);

        protected override List<AutomationPeer>? GetChildrenCore()
        {
            if (!owner.editing && !owner.IsDropDownOpen) return null;
            var children = base.GetChildrenCore() ?? [];
            if (button is null && FindToggle(owner) is { } toggle) button = new R20UnitCombo.DropDownButtonPeer(toggle, owner);
            if (button is not null) children.Add(button);
            return children;
        }

        protected override string GetItemStatusCore() => $"DisplayText^IG_TOKEN_SEPERATOR^{owner.Text}^IG_TOKEN_DELIMETER^";

        string IValueProvider.Value => DataValue(owner.Text);
        bool IValueProvider.IsReadOnly => false;
        void IValueProvider.SetValue(string value) => owner.Text = value;
    }
}
