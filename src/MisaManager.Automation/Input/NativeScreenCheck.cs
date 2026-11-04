using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed partial class NativeInputService
{
    private const int MaxReportedIds = 300;

    /// <summary>Read-only: matches every selector of the profile screens against the live UIA tree. Sends no input.</summary>
    public Task<ScreenCheckReport> CheckScreensAsync(WindowIdentity window, IReadOnlyDictionary<string, ScreenProfile> screens,
        CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery(false);
            var current = RefreshWindow(window);
            var main = automation.FromHandle(current.WindowHandle);
            var results = new List<ScreenCheckResult>();
            foreach (var screen in screens.Values)
            {
                token.ThrowIfCancellationRequested();
                results.Add(CheckScreen(main, current.ProcessId, screen));
            }
            return new ScreenCheckReport(DateTimeOffset.UtcNow, MisaWindowDiscovery.ProductTitle(current.Title), current.Dpi,
                current.ClientBounds, results);
        }, token);

    private ScreenCheckResult CheckScreen(AutomationElement main, int processId, ScreenProfile screen)
    {
        var scope = main;
        if (screen.Scope is { } scopeSelector)
        {
            var scopes = Snapshots(main, Live(SelectorCondition(scopeSelector.Selector), processId, enabled: false))
                .Where(item => Matches(item, scopeSelector.Selector, requireVisible: false)).ToArray();
            var visible = scopes.Where(item => !item.Offscreen && item.HasArea).ToArray();
            if (visible.Length == 0) return new(screen.Key, screen.Title, false, [], [], []);
            if (visible.Length > 1)
                return new(screen.Key, screen.Title, true, [], [new("(scope)", scopeSelector.ToString(), ScreenCheckStatus.Ambiguous, true,
                    $"Có {visible.Length} cửa sổ/vùng cùng ID; đóng bớt rồi kiểm tra lại.")], []);
            scope = visible[0].Element;
        }

        var all = Snapshots(scope, automation.ConditionFactory.ByProcessId(processId));
        var tabs = all.Where(item => item.ControlType == ControlType.TabItem && screen.Tabs.Values.Contains(item.Name, StringComparer.Ordinal))
            .Where(item => IsSelected(item.Element)).Select(item => item.Name).Distinct(StringComparer.Ordinal).ToArray();
        var checks = new List<ElementCheck>();
        foreach (var (key, element) in screen.Elements)
        {
            if (element.Selector.ByLabel)
            {
                var labelled = LabelledMatches(all, element.Selector);
                checks.Add(labelled.Length switch
                {
                    1 => new(key, element.ToString(), ScreenCheckStatus.Ok, element.Required, "tìm thấy"),
                    > 1 => new(key, element.ToString(), ScreenCheckStatus.Ambiguous, element.Required, $"{labelled.Length} control/nhãn cùng selector đang hiển thị"),
                    _ => new(key, element.ToString(), ScreenCheckStatus.Missing, element.Required,
                        "KHÔNG tìm thấy nhãn hoặc ô bên phải nhãn – cần cập nhật profile")
                });
                continue;
            }
            var matches = all.Where(item => Matches(item, element.Selector, requireVisible: false)).ToArray();
            // R20's module tab strip sits above the module panel (the scope), so look in the whole window
            // for elements the scope does not contain.
            if (matches.Length == 0 && !ReferenceEquals(scope, main))
                matches = Snapshots(main, Live(SelectorCondition(element.Selector), processId, enabled: false, onscreen: false))
                    .Where(item => Matches(item, element.Selector, requireVisible: false)).ToArray();
            var visible = matches.Where(item => !item.Offscreen && item.HasArea).ToArray();
            var onOtherTab = element.Tab is { } tabKey && screen.Tabs.TryGetValue(tabKey, out var caption) && !tabs.Contains(caption, StringComparer.Ordinal);
            checks.Add(visible.Length switch
            {
                1 => new(key, element.ToString(), ScreenCheckStatus.Ok, element.Required, visible[0].Enabled ? "tìm thấy" : "tìm thấy (đang khóa)"),
                > 1 => new(key, element.ToString(), ScreenCheckStatus.Ambiguous, element.Required, $"{visible.Length} control cùng selector đang hiển thị"),
                _ when onOtherTab => new(key, element.ToString(), ScreenCheckStatus.OtherTab, element.Required,
                    $"nằm ở tab '{screen.Tabs[element.Tab!]}' chưa mở – chưa kiểm tra"),
                _ when matches.Length > 0 => new(key, element.ToString(), ScreenCheckStatus.Hidden, element.Required,
                    "có trong cây giao diện nhưng đang ẩn/ngoài màn hình (phóng to cửa sổ MISA)"),
                _ => new(key, element.ToString(), ScreenCheckStatus.Missing, element.Required,
                    "KHÔNG tìm thấy – MISA có thể đã đổi ID, cần cập nhật profile")
            });
        }
        foreach (var (key, grid) in screen.Grids)
            checks.Add(CheckGrid(all, key, grid));

        // A scope-less screen (dialog) counts as open only when one of its AutomationId elements is shown;
        // caption-only matches such as "Cất" also exist on the voucher editor.
        var open = screen.Scope is not null || checks.Any(item => item.Status is ScreenCheckStatus.Ok
            && screen.Elements.TryGetValue(item.Key, out var element) && element.AutomationId.Length > 0);
        var failed = checks.Any(item => item.Status is ScreenCheckStatus.Missing or ScreenCheckStatus.Ambiguous);
        // The module tab strip is outside the scope; list its tabs so the report carries their real IDs.
        var outside = failed && !ReferenceEquals(scope, main)
            ? Snapshots(main, new AndCondition(automation.ConditionFactory.ByProcessId(processId), automation.ConditionFactory.ByControlType(ControlType.TabItem)))
            : [];
        IReadOnlyList<string> ids = failed
            ? all.Concat(outside).Where(item => (item.AutomationId.Length > 0 || item.ControlType == ControlType.TabItem) && !item.Offscreen)
                .Select(item => $"{item.AutomationId} | {item.ControlType} | {item.Name}").Distinct().Take(MaxReportedIds).ToArray()
            : [];
        return new(screen.Key, screen.Title, open, tabs, open ? checks : [], open ? ids : []);
    }

    private ElementCheck CheckGrid(ElementSnapshot[] all, string key, ScreenGrid grid)
    {
        var name = "grid:" + key;
        var grids = all.Where(item => string.Equals(item.AutomationId, grid.AutomationId, StringComparison.Ordinal)).ToArray();
        var visible = grids.Where(item => !item.Offscreen && item.HasArea).ToArray();
        if (visible.Length == 0)
            return new(name, grid.AutomationId, grids.Length > 0 ? ScreenCheckStatus.Hidden : ScreenCheckStatus.Missing, true,
                grids.Length > 0 ? "grid đang ẩn (có thể ở tab khác)" : "KHÔNG tìm thấy grid");
        if (visible.Length > 1) return new(name, grid.AutomationId, ScreenCheckStatus.Ambiguous, true, $"{visible.Length} grid cùng ID đang hiển thị");
        var headers = Snapshots(visible[0].Element, new OrCondition(
                automation.ConditionFactory.ByControlType(ControlType.HeaderItem), automation.ConditionFactory.ByControlType(ControlType.Header)))
            .Select(item => item.Name.Trim()).Where(text => text.Length > 0).ToHashSet(StringComparer.Ordinal);
        // The same grid shows different columns per tab (Hàng tiền / Thuế): only report columns of the visible layout.
        var missing = grid.Columns.Where(pair => !headers.Contains(pair.Value)).Select(pair => $"{pair.Key}='{pair.Value}'").ToArray();
        var present = grid.Columns.Count - missing.Length;
        // Header exposure differs between grid builds; an unreadable header row is reported but never blocks a run.
        if (present == 0)
            return new(name, grid.AutomationId, ScreenCheckStatus.Missing, false, "không đọc được tiêu đề cột nào khớp profile");
        return new(name, grid.AutomationId, ScreenCheckStatus.Ok, true, missing.Length == 0
            ? $"đủ {present} cột"
            : $"{present}/{grid.Columns.Count} cột hiển thị; chưa thấy (có thể ở tab khác): {string.Join(", ", missing)}");
    }

    private static bool IsSelected(AutomationElement element)
    {
        try { return element.Patterns.SelectionItem.IsSupported && element.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault; }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
        { return false; }
    }
}
