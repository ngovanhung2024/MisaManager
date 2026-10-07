using System.Text.Json;

namespace MisaManager.FakeMisa;

/// <summary>Reads control IDs from the same profile the automation uses, so both sides stay in sync.</summary>
public sealed class FakeProfile
{
    private readonly JsonElement screens;

    private FakeProfile(JsonDocument document)
    {
        var root = document.RootElement;
        VersionTitle = root.GetProperty("versionTitle").GetString()!;
        screens = root.GetProperty("screens").Clone();
    }

    public string VersionTitle { get; }

    public static FakeProfile Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new FakeProfile(document);
    }

    public string ScopeId(string screen) => screens.GetProperty(screen).GetProperty("scope").GetProperty("automationId").GetString()!;

    public string ScopeName(string screen) => screens.GetProperty(screen).GetProperty("scope").GetProperty("name").GetString()!;

    /// <summary>AutomationId of an element, or "" when the profile selects it by caption.</summary>
    public string Id(string screen, string element)
        => screens.GetProperty(screen).GetProperty("elements").GetProperty(element).TryGetProperty("automationId", out var id)
            ? id.GetString() ?? "" : "";

    public string Caption(string screen, string element)
        => screens.GetProperty(screen).GetProperty("elements").GetProperty(element).TryGetProperty("name", out var name)
            ? name.GetString() ?? "" : "";

    public string Tab(string screen, string tab) => screens.GetProperty(screen).GetProperty("tabs").GetProperty(tab).GetString()!;

    public string GridId(string screen, string grid) => screens.GetProperty(screen).GetProperty("grids").GetProperty(grid).GetProperty("automationId").GetString()!;

    public string Column(string screen, string grid, string key)
        => screens.GetProperty(screen).GetProperty("grids").GetProperty(grid).GetProperty("columns").GetProperty(key).GetString()!;

    public string Setting(string screen, string key, string fallback)
        => screens.GetProperty(screen).TryGetProperty("settings", out var settings) && settings.TryGetProperty(key, out var value)
            ? value.GetString() ?? fallback : fallback;
}
