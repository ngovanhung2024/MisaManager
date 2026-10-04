using System.Text;

namespace MisaManager.Automation;

public enum ScreenCheckStatus { Ok, Hidden, Missing, Ambiguous, OtherTab }

public sealed record ElementCheck(string Key, string Selector, ScreenCheckStatus Status, bool Required, string Detail);

public sealed record ScreenCheckResult(string Key, string Title, bool Open, IReadOnlyList<string> SelectedTabs,
    IReadOnlyList<ElementCheck> Elements, IReadOnlyList<string> VisibleAutomationIds)
{
    public IEnumerable<ElementCheck> Failures => Elements.Where(item => item.Required
        && item.Status is ScreenCheckStatus.Missing or ScreenCheckStatus.Ambiguous);
}

/// <summary>
/// Result of matching every profile selector against the MISA screens that are open right now.
/// Selectors are AutomationId/Name based, so the result does not depend on DPI, resolution or theme;
/// <see cref="ScreenCheckStatus.Hidden"/> usually means the MISA window is too small or a panel is collapsed.
/// </summary>
public sealed record ScreenCheckReport(DateTimeOffset CheckedAt, string WindowTitle, int Dpi, Core.Bounds ClientBounds,
    IReadOnlyList<ScreenCheckResult> Screens)
{
    public bool Ok => Screens.All(screen => !screen.Failures.Any());

    public string Summary
    {
        get
        {
            var open = Screens.Where(screen => screen.Open).ToArray();
            if (open.Length == 0) return "Không có màn hình MISA nào trong profile đang mở (hãy mở phân hệ Bán hàng).";
            var failures = open.SelectMany(screen => screen.Failures).Count();
            var checkedCount = open.Sum(screen => screen.Elements.Count(item => item.Status != ScreenCheckStatus.OtherTab));
            return failures == 0
                ? $"Giao diện MISA khớp profile: {checkedCount} control trên {open.Length} màn hình đang mở."
                : $"Có {failures} control không khớp profile trên màn hình đang mở. Xem chi tiết hoặc xuất báo cáo lỗi.";
        }
    }

    public string Details
    {
        get
        {
            var text = new StringBuilder()
                .AppendLine($"Cửa sổ: {WindowTitle}")
                .AppendLine($"DPI: {Dpi} · Vùng làm việc: {ClientBounds.Width:0}×{ClientBounds.Height:0}")
                .AppendLine();
            foreach (var screen in Screens)
            {
                text.AppendLine($"■ {screen.Title} [{screen.Key}]: {(screen.Open ? "đang mở" : "chưa mở – bỏ qua")}");
                if (!screen.Open) continue;
                if (screen.SelectedTabs.Count > 0) text.AppendLine("  Tab đang chọn: " + string.Join(", ", screen.SelectedTabs));
                foreach (var item in screen.Elements)
                    text.AppendLine($"  {Icon(item)} {item.Key} ({item.Selector}): {item.Detail}");
            }
            return text.ToString();
        }
    }

    private static string Icon(ElementCheck item) => item.Status switch
    {
        ScreenCheckStatus.Ok => "✓",
        ScreenCheckStatus.OtherTab => "·",
        ScreenCheckStatus.Hidden => "!",
        _ => item.Required ? "✗" : "?"
    };
}
