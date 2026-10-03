using System.Windows;
using System.Windows.Media;

namespace MisaManager.App;

public sealed record ComparisonSection(string Title, IReadOnlyList<ComparisonRow> Rows)
{
    public int Mismatches => Rows.Count(row => row.Kind == "mismatch");
    public bool HasMismatch => Mismatches > 0;
    public bool AllMatched => Rows.Count > 0 && Rows.All(row => row.Kind == "match");
    public string Badge => Mismatches > 0 ? $"{Mismatches} lệch" : AllMatched ? "✓ khớp" : Rows.All(row => row.Kind == "pending") ? "chưa đọc" : "";
}

public sealed record ComparisonModel(string Title, string Subtitle, ToolStateKind StatusKind, string StatusLabel,
    string Summary, string ObservedText, IReadOnlyList<ComparisonRow> Rows, bool CanReconcile);

public partial class ComparisonWindow : Window
{
    private ComparisonModel? model;
    private bool entered;

    public ComparisonWindow()
    {
        InitializeComponent();
        ContentRendered += (_, _) =>
        {
            if (entered) return;
            entered = true;
            Motion.FadeIn(Root, fromX: SlideFrom, fromY: 0, milliseconds: 220);
        };
    }

    /// <summary>Horizontal offset the window slides in from; set by the owner from the pairing side.</summary>
    public double SlideFrom { get; set; } = 14;

    public event EventHandler? ReconcileRequested;

    public void UpdateComparison(ComparisonModel next)
    {
        model = next;
        Render();
    }

    public void SetReconcileEnabled(bool enabled) => ReconcileButton.IsEnabled = enabled;

    private void Render()
    {
        if (model is not { } current) return;
        TitleLabel.Text = current.Title;
        SubtitleLabel.Text = current.Subtitle;
        var (fg, bg) = StatePalette.Of(current.StatusKind);
        StatusChip.Background = bg;
        StatusDot.Fill = fg;
        StatusLabel.Foreground = fg;
        StatusLabel.Text = current.StatusLabel;
        Motion.Pulse(StatusDot, current.StatusKind is ToolStateKind.Connecting or ToolStateKind.Entering
            or ToolStateKind.Reconciling or ToolStateKind.Saving or ToolStateKind.PauseRequested);
        ObservedLabel.Text = current.ObservedText;
        SummaryLabel.Text = current.Summary;
        SummaryLabel.Visibility = current.Summary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ReconcileButton.IsEnabled = current.CanReconcile;

        var matched = current.Rows.Count(row => row.Kind == "match");
        var mismatched = current.Rows.Count(row => row.Kind == "mismatch");
        var pending = current.Rows.Count(row => row.Kind == "pending");
        MatchCount.Text = matched.ToString();
        MismatchCount.Text = mismatched.ToString();
        PendingCount.Text = pending.ToString();

        var visible = OnlyMismatchToggle.IsChecked == true
            ? current.Rows.Where(row => row.Kind == "mismatch").ToArray() : current.Rows;
        var sections = visible.GroupBy(row => row.Section)
            .Select(group => new ComparisonSection(group.Key, [.. group])).ToArray();
        var scroll = RowsScroll.VerticalOffset;
        SectionList.ItemsSource = sections;
        RowsScroll.ScrollToVerticalOffset(scroll);
        EmptyLabel.Visibility = sections.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLabel.Text = current.Rows.Count == 0 ? "Chưa có dữ liệu đối chiếu. Bấm Đọc lại khi chứng từ MISA đang mở."
            : "Không có dòng lệch.";
        if (Motion.Enabled && IsLoaded) Motion.FadeIn(SectionList, fromY: 0, milliseconds: 140);
    }

    private void OnlyMismatch_Changed(object sender, RoutedEventArgs e) => Render();

    private void ReconcileButton_Click(object sender, RoutedEventArgs e)
        => ReconcileRequested?.Invoke(this, EventArgs.Empty);
}
