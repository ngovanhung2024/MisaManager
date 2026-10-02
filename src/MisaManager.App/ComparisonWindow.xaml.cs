using System.Collections.Generic;
using System.Windows;

namespace MisaManager.App;

public partial class ComparisonWindow : Window
{
    public ComparisonWindow()
    {
        InitializeComponent();
    }

    public void UpdateComparison(string title, string summary, IReadOnlyList<ComparisonRow> rows)
    {
        TitleLabel.Text = title;
        SummaryLabel.Text = summary;
        ComparisonList.ItemsSource = rows;
    }
}
