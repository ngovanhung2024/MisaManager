using System.Text;

namespace MisaAutomation.Core;

public sealed record DialogState(WindowInfo Window, string Code, string Message, DateTimeOffset ObservedAt);

public sealed class MisaDialogMonitor(IAutomationProvider provider, IAutomationLog log)
{
    private readonly Dictionary<long, DialogState> current = new();
    public IReadOnlyCollection<DialogState> Current => current.Values;
    public void Observe()
    {
        var windows = provider.Windows().Where(w => w.Kind == "dialog").ToArray();
        foreach (var removed in current.Keys.Except(windows.Select(w => w.Handle)).ToArray()) current.Remove(removed);
        foreach (var window in windows)
        {
            var message = window.Title + "\n" + provider.WindowText(window.Handle);
            var dialog = new DialogState(window, Classify(message), message, DateTimeOffset.UtcNow);
            if (!current.TryGetValue(window.Handle, out var previous) || previous.Message != message)
                log.Write("dialog", dialog);
            current[window.Handle] = dialog;
        }
    }
    public void ThrowIfBlocking()
    {
        Observe();
        if (current.Values.FirstOrDefault() is { } dialog)
            throw new AutomationException(dialog.Code, dialog.Message);
    }
    public static string Classify(string message)
    {
        var text = string.Concat(message.ToLowerInvariant().Replace('\u0111', 'd').Normalize(NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark));
        if (text.Contains("da ton tai") || text.Contains("trung so chung tu")) return "DUPLICATE_VOUCHER";
        if (text.Contains("bat buoc") || text.Contains("chua nhap") || text.Contains("khong duoc de trong")) return "REQUIRED_FIELD_MISSING";
        if (text.Contains("ngay") && (text.Contains("khong hop le") || text.Contains("khong dung"))) return "INVALID_DATE";
        if (text.Contains("mat ket noi") || text.Contains("connection lost")) return "CONNECTION_LOST";
        if (text.Contains("co so du lieu") || text.Contains("database")) return "DATABASE_UNAVAILABLE";
        if (text.Contains("khong co quyen") || text.Contains("access denied")) return "PERMISSION_DENIED";
        return "UNKNOWN_DIALOG";
    }
}
