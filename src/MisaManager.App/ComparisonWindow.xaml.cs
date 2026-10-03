using System.Collections.Generic;
using System.Windows;

namespace MisaManager.App;

public partial class ComparisonWindow : Window
{
    public ComparisonWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? ReconcileRequested;

    public void UpdateComparison(string title, string summary, IReadOnlyList<ComparisonRow> rows)
    {
        TitleLabel.Text = title;
        SummaryLabel.Text = summary;
        ComparisonList.ItemsSource = rows;
    }

    public void SetReconcileEnabled(bool enabled) => ReconcileButton.IsEnabled = enabled;

    private void ReconcileButton_Click(object sender, RoutedEventArgs e)
        => ReconcileRequested?.Invoke(this, EventArgs.Empty);
}
