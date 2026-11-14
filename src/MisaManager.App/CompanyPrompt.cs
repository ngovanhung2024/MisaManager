using System.Windows;
using System.Windows.Controls;
using MisaManager.Core;

namespace MisaManager.App;

/// <summary>Dialog for a company's name, MST and its own purchase rule (fuel only: Kho, TK kho, TK chi phí).</summary>
public static class CompanyPrompt
{
    /// <summary>The edited company (empty id for a new one), or null when cancelled.</summary>
    public static CompanyProfile? Edit(Window owner, CompanyProfile company, string title)
    {
        TextBox Field(string text) => new() { Text = text, Margin = new Thickness(0, 2, 0, 8) };
        var name = Field(company.Name);
        var tax = Field(company.TaxCode);
        var fuel = new CheckBox
        {
            Content = "Mua hàng chỉ nhập xăng dầu (dầu diesel nhập kho, xăng/nhớt không qua kho; hóa đơn khác để nhập tay)",
            IsChecked = company.Fuel is not null, Margin = new Thickness(0, 4, 0, 6)
        };
        var rule = company.Fuel ?? new FuelPurchaseRule("CTY", "152", "154");
        var stockAccount = Field(rule.StockAccount);
        var expenseAccount = Field(rule.ExpenseAccount);
        var fuelFields = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
        // Kho is left to MISA's default; the saved rule keeps its old value only for compatibility.
        foreach (var (label, box) in new (string, TextBox)[] { ("TK kho (dầu diesel)", stockAccount),
                     ("TK chi phí (xăng/nhớt)", expenseAccount) })
        {
            fuelFields.Children.Add(new TextBlock { Text = label });
            fuelFields.Children.Add(box);
        }
        fuelFields.IsEnabled = fuel.IsChecked == true;
        fuel.Checked += (_, _) => fuelFields.IsEnabled = true;
        fuel.Unchecked += (_, _) => fuelFields.IsEnabled = false;
        var save = new Button { Content = "Lưu", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Hủy", IsCancel = true, Width = 80 };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Tên công ty" });
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "MST (người mua trên hóa đơn mua vào, người bán trên hóa đơn bán ra)" });
        panel.Children.Add(tax);
        panel.Children.Add(new TextBlock
        {
            Text = "Có MST thì hóa đơn của công ty khác dừng trước khi nhập.", TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, -4, 0, 8)
        });
        panel.Children.Add(fuel);
        panel.Children.Add(fuelFields);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
            Children = { save, cancel }
        });
        var dialog = new Window
        {
            Title = title, Owner = owner, Content = panel, Width = 460, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        CompanyProfile? result = null;
        save.Click += (_, _) =>
        {
            var problem = name.Text.Trim().Length == 0 ? "Cần tên công ty."
                : fuel.IsChecked == true && PartyCatalogStore.TaxKey(tax.Text).Length == 0 ? "Quy tắc chỉ nhập xăng dầu cần MST của công ty."
                : fuel.IsChecked == true && new[] { stockAccount, expenseAccount }.Any(box => box.Text.Trim().Length == 0)
                    ? "Cần đủ TK kho và TK chi phí." : null;
            if (problem is not null)
            {
                MessageBox.Show(dialog, problem, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            result = company with
            {
                Name = name.Text.Trim(), TaxCode = tax.Text.Trim(),
                Fuel = fuel.IsChecked == true ? new(rule.Stock, stockAccount.Text.Trim(), expenseAccount.Text.Trim()) : null
            };
            dialog.DialogResult = true;
        };
        return dialog.ShowDialog() == true ? result : null;
    }
}
