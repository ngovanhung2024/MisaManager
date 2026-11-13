using System.Collections.Generic;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace MisaManager.FakeMisa;

/// <summary>
/// R20's "Chứng từ mua hàng" combo (report 0.2.189, HĐ 205): UIA ValuePattern gives the item key ("1" while
/// "2. Mua hàng trong nước không qua kho" is shown), and the items are only in the tree while the list is open.
/// </summary>
internal sealed class KeyedComboBox : ComboBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(KeyedComboBox owner) : ComboBoxAutomationPeer(owner), IValueProvider
    {
        public override object GetPattern(PatternInterface pattern)
            => pattern == PatternInterface.Value ? this : base.GetPattern(pattern);

        protected override List<AutomationPeer>? GetChildrenCore() => owner.IsDropDownOpen ? base.GetChildrenCore() : null;

        string IValueProvider.Value => owner.SelectedIndex.ToString(CultureInfo.InvariantCulture);
        bool IValueProvider.IsReadOnly => true;
        void IValueProvider.SetValue(string value) => throw new ElementNotEnabledException();
    }
}
