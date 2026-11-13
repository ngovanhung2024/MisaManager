using System.Windows;
using System.Windows.Controls;
using MisaManager.Core;

namespace MisaManager.App;

/// <summary>Dialog to choose which customer/supplier code the tool uses for an MST that several codes share.</summary>
public static class PartyPreferencePrompt
{
    private const string Title = "Chọn mã khách hàng/nhà cung cấp trùng MST";

    /// <summary>Returns true when the choice was saved. <paramref name="focusTaxCode"/> puts that MST first.</summary>
    public static bool Edit(Window owner, PartyCatalogStore catalog, string? focusTaxCode = null)
    {
        var focus = PartyCatalogStore.TaxKey(focusTaxCode);
        var groups = catalog.SharedTaxGroups().OrderBy(group => PartyCatalogStore.TaxKey(group.TaxCode) == focus ? 0 : 1).ToArray();
        if (groups.Length == 0)
        {
            MessageBox.Show(owner, catalog.CustomerCount + catalog.SupplierCount == 0 ? "Chưa nạp danh sách khách hàng/nhà cung cấp."
                : "Danh sách khách hàng/nhà cung cấp không có MST nào thuộc nhiều mã.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var preferred = catalog.PreferredSnapshot();
        var choices = new List<(SharedTaxGroup Group, List<RadioButton> Options)>();
        var list = new StackPanel();
        foreach (var (group, index) in groups.Select((group, index) => (group, index)))
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            box.Children.Add(new TextBlock
            {
                Text = $"MST {group.TaxCode} · danh sách {PartyCatalogStore.Label(group.Role)}", FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            var current = preferred.FirstOrDefault(choice => choice.Role == group.Role
                && PartyCatalogStore.TaxKey(choice.TaxCode) == PartyCatalogStore.TaxKey(group.TaxCode))?.Code;
            var chosen = group.Entries.Any(entry => string.Equals(entry.Code, current, StringComparison.OrdinalIgnoreCase));
            var name = "tax" + index;
            var options = new List<RadioButton>
            {
                new() { GroupName = name, Content = "Chưa chọn: hóa đơn của MST này dừng trước khi nhập", IsChecked = !chosen,
                    Margin = new Thickness(12, 2, 0, 0) }
            };
            options.AddRange(group.Entries.DistinctBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase).Select(entry => new RadioButton
            {
                GroupName = name, Tag = entry.Code, Margin = new Thickness(12, 2, 0, 0),
                IsChecked = chosen && string.Equals(entry.Code, current, StringComparison.OrdinalIgnoreCase),
                Content = new TextBlock { Text = $"{entry.Code} – {entry.Name}", TextWrapping = TextWrapping.Wrap }
            }));
            options.ForEach(option => box.Children.Add(option));
            list.Children.Add(box);
            choices.Add((group, options));
        }
        var save = new Button { Content = "Lưu", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Hủy", IsCancel = true, Width = 80 };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "Hóa đơn có MST thuộc nhiều mã dùng mã được chọn: nhà cung cấp khi Mua hàng, khách hàng khi Bán hàng. Lựa chọn được "
                + "giữ khi nạp lại danh sách; cột 'Mã khách hàng' trong file Excel vẫn được ưu tiên hơn."
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
            Children = { save, cancel }
        };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new Window
        {
            Title = Title, Owner = owner, Content = panel, Width = 520,
            Height = Math.Min(640, 180 + groups.Sum(group => 30 + 24 * (group.Entries.Count + 1))),
            ResizeMode = ResizeMode.CanResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        save.Click += (_, _) =>
        {
            // MSTs no longer shared (a duplicate merged in MISA) keep their earlier choice.
            var listed = choices.Select(choice => (choice.Group.Role, PartyCatalogStore.TaxKey(choice.Group.TaxCode))).ToHashSet();
            var kept = preferred.Where(choice => !listed.Contains((choice.Role, PartyCatalogStore.TaxKey(choice.TaxCode))));
            var picked = choices.SelectMany(choice => choice.Options.Where(option => option.IsChecked == true && option.Tag is string)
                .Select(option => new PartyPreference(choice.Group.Role, choice.Group.TaxCode, (string)option.Tag)));
            try
            {
                catalog.SetPreferred(kept.Concat(picked));
                dialog.DialogResult = true;
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(dialog, "Không lưu được: " + error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        return dialog.ShowDialog() == true;
    }
}
