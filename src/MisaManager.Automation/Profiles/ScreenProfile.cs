using System.Text.Json;
using MisaManager.Core;

namespace MisaManager.Automation;

/// <summary>
/// One UI element of a MISA screen. AutomationId wins over Name; ControlType narrows either. Label (form caption left
/// of the control) is used only without an AutomationId.
/// </summary>
public sealed record ScreenElement(string AutomationId = "", string Name = "", string ControlType = "",
    string? Tab = null, bool Required = true, string? Description = null, string Label = "")
{
    public NativeSelector Selector => new(AutomationId, Name, ControlType, Label);
    public override string ToString() => AutomationId.Length > 0 ? AutomationId
        : Name.Length == 0 && Label.Length > 0 ? $"nhãn '{Label}' ({(ControlType.Length > 0 ? ControlType : "Edit")})" : $"{Name} ({ControlType})";
}

public sealed record ScreenGrid(string AutomationId, IReadOnlyDictionary<string, string> Columns);

/// <summary>
/// A MISA screen (module list, voucher editor, dialog) described only by data, so new
/// screens/tabs can be automated or checked without touching the selector code.
/// </summary>
public sealed record ScreenProfile(string Key, string Title, ScreenElement? Scope,
    IReadOnlyDictionary<string, string> Tabs, IReadOnlyDictionary<string, ScreenElement> Elements,
    IReadOnlyDictionary<string, ScreenGrid> Grids, IReadOnlyDictionary<string, string> Settings)
{
    public ScreenElement Element(string key) => Elements.TryGetValue(key, out var element) ? element
        : throw new InvalidDataException($"Profile màn hình '{Key}' thiếu element '{key}'.");

    public ScreenGrid Grid(string key) => Grids.TryGetValue(key, out var grid) ? grid
        : throw new InvalidDataException($"Profile màn hình '{Key}' thiếu grid '{key}'.");

    public string Tab(string key) => Tabs.TryGetValue(key, out var caption) ? caption
        : throw new InvalidDataException($"Profile màn hình '{Key}' thiếu tab '{key}'.");

    public string Setting(string key, string fallback) => Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
}

/// <summary>JSON shape of profiles/*.json (schema 2).</summary>
public sealed record ProfileDocument(int SchemaVersion, string Id, string Revision, string VersionTitle, string Status,
    string Culture, IReadOnlyDictionary<string, ScreenDocument> Screens);

public sealed record ScreenDocument(string? Title = null, ScreenElement? Scope = null,
    IReadOnlyDictionary<string, string>? Tabs = null, IReadOnlyDictionary<string, ScreenElement>? Elements = null,
    IReadOnlyDictionary<string, ScreenGrid>? Grids = null, IReadOnlyDictionary<string, string>? Settings = null);

public static class ProfileLoader
{
    public const int SchemaVersion = 2;
    public const string DefaultFileName = "misa-sme2023-r20.json";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "profiles", DefaultFileName);

    public static (ProfileDocument Document, IReadOnlyDictionary<string, ScreenProfile> Screens) Read(string path)
    {
        var document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(path), ContractJson.Options)
            ?? throw new InvalidDataException("Profile MISA trống.");
        if (document.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"Profile MISA phải dùng schemaVersion {SchemaVersion}.");
        if (new[] { document.Id, document.Revision, document.VersionTitle, document.Status, document.Culture }.Any(string.IsNullOrWhiteSpace)
            || document.Screens is null || document.Screens.Count == 0)
            throw new InvalidDataException("Profile MISA thiếu id/revision/versionTitle/status/culture hoặc screens.");
        var screens = new Dictionary<string, ScreenProfile>(StringComparer.Ordinal);
        foreach (var (key, screen) in document.Screens)
        {
            if (screen is null) throw new InvalidDataException($"Màn hình '{key}' rỗng.");
            var elements = screen.Elements ?? new Dictionary<string, ScreenElement>();
            foreach (var (name, element) in elements.Append(new("(scope)", screen.Scope!)).Where(pair => pair.Value is not null))
                Validate(key, name, element);
            var tabs = screen.Tabs ?? new Dictionary<string, string>();
            foreach (var (name, element) in elements)
                if (element.Tab is { } tab && !tabs.ContainsKey(tab))
                    throw new InvalidDataException($"Element '{key}.{name}' tham chiếu tab '{tab}' không khai báo.");
            var grids = screen.Grids ?? new Dictionary<string, ScreenGrid>();
            foreach (var (name, grid) in grids)
            {
                if (grid is null || string.IsNullOrWhiteSpace(grid.AutomationId) || grid.Columns is null || grid.Columns.Count == 0)
                    throw new InvalidDataException($"Grid '{key}.{name}' thiếu automationId hoặc columns.");
                if (grid.Columns.Values.Any(string.IsNullOrWhiteSpace)
                    || grid.Columns.Values.Distinct(StringComparer.Ordinal).Count() != grid.Columns.Count)
                    throw new InvalidDataException($"Grid '{key}.{name}' có tiêu đề cột trống hoặc trùng nhau.");
            }
            screens[key] = new(key, screen.Title ?? key, screen.Scope, tabs, elements, grids,
                screen.Settings ?? new Dictionary<string, string>());
        }
        return (document, screens);
    }

    private static void Validate(string screen, string name, ScreenElement element)
    {
        if (string.IsNullOrWhiteSpace(element.AutomationId) && string.IsNullOrWhiteSpace(element.Name) && string.IsNullOrWhiteSpace(element.Label))
            throw new InvalidDataException($"Element '{screen}.{name}' cần automationId, name hoặc label.");
        if (element.ControlType.Length > 0 && !Enum.TryParse<FlaUI.Core.Definitions.ControlType>(element.ControlType, out _))
            throw new InvalidDataException($"Element '{screen}.{name}' có controlType '{element.ControlType}' không hợp lệ.");
    }
}
