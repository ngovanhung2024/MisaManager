using System.Collections.Generic;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

namespace MisaManager.FakeMisa;

/// <summary>
/// R20's "% thuế GTGT" value-list cell as modelled after report 0.2.200 (HĐ 18523): outside editing, ValuePattern gives
/// the list's data value — the rate as "8,0000", but a code for KCT/KKKNT ("-1"/"-2") — while the label shown is in
/// ItemStatus as Infragistics' "DisplayText" token. The inner editor is exposed only while the cell is being edited.
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

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(KeyedTaxRateCombo owner) : ComboBoxAutomationPeer(owner), IValueProvider
    {
        public override object GetPattern(PatternInterface pattern)
            => pattern == PatternInterface.Value ? this : base.GetPattern(pattern);

        protected override List<AutomationPeer>? GetChildrenCore() => owner.editing || owner.IsDropDownOpen ? base.GetChildrenCore() : null;

        protected override string GetItemStatusCore() => $"DisplayText^IG_TOKEN_SEPERATOR^{owner.Text}^IG_TOKEN_DELIMETER^";

        string IValueProvider.Value => DataValue(owner.Text);
        bool IValueProvider.IsReadOnly => false;
        void IValueProvider.SetValue(string value) => owner.Text = value;
    }
}
