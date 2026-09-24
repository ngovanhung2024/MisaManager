using System.Text.Json;

namespace MisaAutomation.Core;

public sealed class LocatorRepository
{
    private readonly Dictionary<string, Locator> locators = new(StringComparer.Ordinal);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public Locator this[string key] => locators.TryGetValue(key, out var locator) ? locator
        : throw new AutomationException("UNKNOWN_LOCATOR", "Locator is not configured: " + key);
    public void Add(string key, Locator locator)
    {
        if (string.IsNullOrWhiteSpace(key) || (string.IsNullOrWhiteSpace(locator.AutomationId)
            && string.IsNullOrWhiteSpace(locator.Name) && string.IsNullOrWhiteSpace(locator.ClassName)))
            throw new AutomationException("INVALID_LOCATOR", "A locator needs a key and an identifying property.");
        if (!locators.TryAdd(key, locator))
            throw new AutomationException("DUPLICATE_LOCATOR", "Duplicate locator key: " + key);
    }
    public void Load(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").OrderBy(p => p))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in document.RootElement.EnumerateObject())
                Add(entry.Name, entry.Value.Deserialize<Locator>(Json)
                    ?? throw new AutomationException("INVALID_LOCATOR", path));
        }
        foreach (var key in locators.Keys) ValidateReferences(key, new HashSet<string>());
    }
    private void ValidateReferences(string key, HashSet<string> path)
    {
        if (!path.Add(key)) throw new AutomationException("LOCATOR_CYCLE", "Cyclic locator relationship: " + key);
        var locator = this[key];
        foreach (var reference in new[] { locator.Parent, locator.Anchor }.OfType<string>())
            ValidateReferences(reference, path);
        path.Remove(key);
    }
}
