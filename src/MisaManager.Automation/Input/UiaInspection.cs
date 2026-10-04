using System.Globalization;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using MisaManager.Core;

namespace MisaManager.Automation;

public sealed record UiElementSummary(string AutomationId, string Name, string ControlType, string? Value,
    string ClassName, bool Enabled, bool Offscreen, bool Focused, Bounds Bounds, string Path);
public sealed record UiDialogSummary(long Handle, string Title, string AutomationId, string ClassName, bool Modal,
    IReadOnlyList<string> Texts, IReadOnlyList<string> Buttons);
public sealed record UiFindResult(long WindowHandle, string WindowTitle, IReadOnlyList<UiElementSummary> Elements,
    int Visited, bool Truncated, DateTimeOffset ObservedAt);
public sealed record UiFailureSnapshot(long WindowHandle, string WindowTitle, IReadOnlyList<UiDialogSummary> Dialogs,
    IReadOnlyList<UiElementSummary> FocusPath, IReadOnlyList<UiElementSummary> Targets, DateTimeOffset ObservedAt,
    string? Error = null);

// Compact, read-only inspection used by the failure report. Each element costs several
// cross-process UIA calls, so results stay small and bounded by default.
public sealed partial class UiaReadbackProvider
{
    private const int MaxText = 400;

    public UiFindResult Find(NativeFrame frame, string? text, string? scopeAutomationId, string? controlType,
        int maxResults, int maxVisited)
    {
        EnsureFrame(frame);
        ControlType? type = null;
        if (!string.IsNullOrWhiteSpace(controlType))
            type = Enum.TryParse<ControlType>(controlType, true, out var parsed) ? parsed
                : throw new AutomationException("CONTROL_TYPE_INVALID", "controlType không hợp lệ: " + controlType);
        var scope = ResolveScope(frame, scopeAutomationId);
        var needle = string.IsNullOrWhiteSpace(text) ? null : Fold(text.Trim());
        var results = new List<UiElementSummary>();
        var pending = new Queue<(AutomationElement Element, string Path)>();
        pending.Enqueue((scope, ""));
        var visited = 0;
        while (pending.Count > 0 && results.Count < maxResults && visited < maxVisited)
        {
            var (element, path) = pending.Dequeue();
            visited++;
            string id, name;
            ControlType elementType;
            try
            {
                id = element.Properties.AutomationId.ValueOrDefault ?? "";
                name = element.Properties.Name.ValueOrDefault ?? "";
                elementType = element.Properties.ControlType.ValueOrDefault;
            }
            catch (Exception) { continue; }
            if ((type is null || elementType == type)
                && (needle is null || Fold(id).Contains(needle, StringComparison.Ordinal) || Fold(name).Contains(needle, StringComparison.Ordinal))
                && Summarize(element, path) is { } match)
                results.Add(match);
            var childPath = Tail(path.Length == 0 ? Label(id, name, elementType) : path + " > " + Label(id, name, elementType));
            AutomationElement[] children;
            try { children = element.FindAllChildren(); }
            catch (Exception) { continue; }
            foreach (var child in children)
                if (child.Properties.ProcessId.ValueOrDefault == frame.Window.ProcessId)
                    pending.Enqueue((child, childPath));
        }
        return new(frame.Window.WindowHandle, frame.Window.Title, results, visited,
            pending.Count > 0 && results.Count < maxResults, DateTimeOffset.UtcNow);
    }

    public UiFailureSnapshot InspectFailure(NativeFrame frame, IReadOnlyList<string> targetAutomationIds)
    {
        EnsureFrame(frame);
        var dialogs = ReadDialogs(frame);
        var focus = ReadFocusPath(frame);
        var targets = new List<UiElementSummary>();
        foreach (var id in targetAutomationIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Take(6))
        {
            try
            {
                var found = root!.FindFirstDescendant(automation.ConditionFactory.ByAutomationId(id));
                if (found is not null && Summarize(found, "target") is { } summary) targets.Add(summary);
            }
            catch (Exception) { /* Target vanished; dialogs and focus remain useful. */ }
        }
        return new(frame.Window.WindowHandle, frame.Window.Title, dialogs, focus, targets, DateTimeOffset.UtcNow);
    }

    private AutomationElement ResolveScope(NativeFrame frame, string? scopeAutomationId)
    {
        if (string.IsNullOrWhiteSpace(scopeAutomationId)) return root!;
        var scopes = root!.FindAllDescendants(automation.ConditionFactory.ByAutomationId(scopeAutomationId))
            .Where(item => item.Properties.ProcessId.ValueOrDefault == frame.Window.ProcessId).ToArray();
        return scopes.Length switch
        {
            1 => scopes[0],
            0 => throw new AutomationException("SCOPE_NOT_FOUND", "Không thấy vùng có AutomationId " + scopeAutomationId),
            _ => throw new AutomationException("SCOPE_AMBIGUOUS", "AutomationId " + scopeAutomationId + " không duy nhất.")
        };
    }

    private List<UiDialogSummary> ReadDialogs(NativeFrame frame)
    {
        var pid = frame.Window.ProcessId;
        var windows = new List<AutomationElement>();
        // Owned WinForms dialogs appear under their owner; unowned ones under the desktop.
        try { windows.AddRange(automation.GetDesktop().FindAllChildren(automation.ConditionFactory.ByProcessId(pid))); }
        catch (Exception) { /* Desktop enumeration is best effort. */ }
        try { windows.AddRange(root!.FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Window))); }
        catch (Exception) { /* Root may be closing. */ }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UiDialogSummary>();
        foreach (var window in windows)
        {
            if (result.Count >= 8) break;
            try
            {
                if (window.Properties.ProcessId.ValueOrDefault != pid) continue;
                var handle = (long)window.Properties.NativeWindowHandle.ValueOrDefault;
                if (handle == frame.Window.WindowHandle.ToInt64()) continue;
                var key = handle != 0 ? "h" + handle.ToString(CultureInfo.InvariantCulture)
                    : string.Join(".", window.Properties.RuntimeId.ValueOrDefault ?? []);
                if (!seen.Add(key)) continue;
                var title = window.Properties.Name.ValueOrDefault ?? "";
                var id = window.Properties.AutomationId.ValueOrDefault ?? "";
                var className = window.Properties.ClassName.ValueOrDefault ?? "";
                var modal = window.Patterns.Window.IsSupported && window.Patterns.Window.Pattern.IsModal.ValueOrDefault;
                var messageLike = modal || className == "#32770" || LooksLikeMessage(id) || LooksLikeMessage(title);
                if (!messageLike)
                {
                    if (!window.Properties.IsOffscreen.ValueOrDefault)
                        result.Add(new(handle, Clip(title)!, id, className, false, [], []));
                    continue;
                }
                var texts = new List<string>();
                var buttons = new List<string>();
                foreach (var item in window.FindAllDescendants().Take(400))
                {
                    var itemType = item.Properties.ControlType.ValueOrDefault;
                    var itemName = item.Properties.Name.ValueOrDefault?.Trim();
                    if (itemType == ControlType.Button)
                    {
                        if (!string.IsNullOrWhiteSpace(itemName)) buttons.Add(Clip(itemName)!);
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(itemName)) texts.Add(Clip(itemName)!);
                    if (itemType is ControlType.Edit or ControlType.Document && item.Patterns.Value.IsSupported
                        && item.Patterns.Value.Pattern.Value.ValueOrDefault is { Length: > 0 } value)
                        texts.Add(Clip(value.Trim())!);
                }
                result.Add(new(handle, Clip(title)!, id, className, modal,
                    texts.Distinct(StringComparer.Ordinal).Take(30).ToArray(),
                    buttons.Distinct(StringComparer.Ordinal).Take(10).ToArray()));
            }
            catch (Exception) { /* A dialog closing during enumeration is not an error. */ }
        }
        return result;
    }

    private List<UiElementSummary> ReadFocusPath(NativeFrame frame)
    {
        var chain = new List<UiElementSummary>();
        try
        {
            var current = automation.FocusedElement();
            if (current is null || current.Properties.ProcessId.ValueOrDefault != frame.Window.ProcessId) return chain;
            var walker = automation.TreeWalkerFactory.GetControlViewWalker();
            for (var depth = 0; current is not null && depth < 6; depth++)
            {
                if (current.Properties.ProcessId.ValueOrDefault != frame.Window.ProcessId) break;
                if (Summarize(current, depth == 0 ? "focused" : "ancestor:" + depth.ToString(CultureInfo.InvariantCulture)) is { } item)
                    chain.Add(item);
                current = walker.GetParent(current);
            }
        }
        catch (Exception) { /* Focus may move while reading. */ }
        return chain;
    }

    private static UiElementSummary? Summarize(AutomationElement element, string path)
    {
        try
        {
            var bounds = element.BoundingRectangle;
            string? value = null;
            if (element.Patterns.Value.IsSupported) value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
            return new(element.Properties.AutomationId.ValueOrDefault ?? "", Clip(element.Properties.Name.ValueOrDefault ?? "")!,
                element.Properties.ControlType.ValueOrDefault.ToString(), Clip(value),
                element.Properties.ClassName.ValueOrDefault ?? "", element.Properties.IsEnabled.ValueOrDefault,
                element.Properties.IsOffscreen.ValueOrDefault, element.Properties.HasKeyboardFocus.ValueOrDefault,
                new(bounds.X, bounds.Y, bounds.Width, bounds.Height), path);
        }
        catch (Exception) { return null; }
    }

    private static bool LooksLikeMessage(string value)
    {
        var folded = Fold(value);
        return folded.Contains("message", StringComparison.Ordinal) || folded.Contains("thong bao", StringComparison.Ordinal)
            || folded.Contains("loi", StringComparison.Ordinal) || folded.Contains("canh bao", StringComparison.Ordinal)
            || folded.Contains("error", StringComparison.Ordinal) || folded.Contains("warning", StringComparison.Ordinal);
    }

    private static string Label(string id, string name, ControlType type)
        => id.Length > 0 ? id : name.Length > 0 ? (name.Length > 40 ? name[..40] : name) : type.ToString();

    private static string Tail(string path)
    {
        var parts = path.Split(" > ");
        return parts.Length <= 4 ? path : string.Join(" > ", parts[^4..]);
    }

    private static string? Clip(string? value) => value is null || value.Length <= MaxText ? value : value[..MaxText] + "…";

    // Case- and diacritic-insensitive so "ma hang" matches "Mã hàng".
    public static string Fold(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(c is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
