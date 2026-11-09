using System.Windows;
using System.Windows.Controls;
using MisaManager.Core;

namespace MisaManager.App;

/// <summary>Dialog to choose which code the tool uses for each group of catalog codes sharing a name and unit.</summary>
public static class PreferredItemPrompt
{
    private const string Title = "Chọn mã dùng khi trùng tên";

    /// <summary>Returns true when the choice was saved.</summary>
    public static bool Edit(Window owner, ItemCatalogStore catalog)
    {
        var groups = CatalogAudit.DuplicateGroups(catalog.Snapshot());
        if (groups.Count == 0)
        {
            MessageBox.Show(owner, catalog.Count == 0 ? "Chưa nạp danh mục mã hàng." : "Danh mục không có mã trùng tên cùng ĐVT.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var preferred = catalog.PreferredCodes();
        var choices = new List<(IReadOnlyList<ItemCatalogEntry> Group, List<RadioButton> Options)>();
        var list = new StackPanel();
        foreach (var (group, index) in groups.Select((group, index) => (group, index)))
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            box.Children.Add(new TextBlock
            {
                Text = $"'{group[0].Name.Trim()}' (ĐVT {group[0].Unit.Trim()})", FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            var name = "group" + index;
            var chosen = group.Count(item => preferred.Contains(item.Code)) == 1;
            var smallest = group.Select(item => item.Code).Order(ItemCatalogStore.CodeOrder).First();
            var options = new List<RadioButton>
            {
                new() { GroupName = name, Content = $"Mặc định: mã số nhỏ nhất ({smallest})", IsChecked = !chosen,
                    Margin = new Thickness(12, 2, 0, 0) }
            };
            options.AddRange(group.Select(item => new RadioButton
            {
                GroupName = name, Tag = item.Code, Margin = new Thickness(12, 2, 0, 0), IsChecked = chosen && preferred.Contains(item.Code),
                Content = new TextBlock { Text = $"{item.Code} – {item.Name.Trim()}", TextWrapping = TextWrapping.Wrap }
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
            Text = "Dòng hóa đơn không ghi mã mà tên trùng nhiều mã cùng ĐVT dùng mã được chọn, mặc định là mã số nhỏ nhất. Lựa chọn "
                + "được giữ khi nạp lại danh mục; cột 'Mã hàng' trong file Excel hóa đơn vẫn được ưu tiên hơn."
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
            Title = Title, Owner = owner, Content = panel, Width = 520, Height = Math.Min(640, 180 + groups.Sum(group => 30 + 24 * (group.Count + 1))),
            ResizeMode = ResizeMode.CanResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        save.Click += (_, _) =>
        {
            // Codes outside the listed groups (e.g. a duplicate already retired in MISA) keep their earlier choice.
            var listed = choices.SelectMany(choice => choice.Group).Select(item => item.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var codes = preferred.Where(code => !listed.Contains(code))
                .Concat(choices.SelectMany(choice => choice.Options).Where(option => option.IsChecked == true && option.Tag is string)
                    .Select(option => (string)option.Tag));
            try
            {
                catalog.SetPreferred(codes);
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
