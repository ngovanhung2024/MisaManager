using FlaUI.Core.AutomationElements;
using MisaAutomation.Core;

namespace MisaAutomation.UIA;

internal sealed class ElementCache
{
    internal sealed record Entry(AutomationElement Element, long Window, DateTimeOffset Created);
    private readonly Dictionary<string, Entry> tokens = new();
    private readonly Dictionary<string, string> locators = new();
    private readonly Dictionary<string, string> runtimeIds = new();
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public string Store(AutomationElement element, long window, string? key = null)
    {
        var runtime = element.Properties.RuntimeId.ValueOrDefault;
        var runtimeId = runtime is { Length: > 0 } ? window + ":" + string.Join(".", runtime) : null;
        if (runtimeId != null && runtimeIds.TryGetValue(runtimeId, out var existing) && tokens.TryGetValue(existing, out var previous)
            && DateTimeOffset.UtcNow - previous.Created <= TimeSpan.FromMinutes(2))
        {
            tokens[existing] = previous with { Element = element };
            if (key != null) locators[key] = existing;
            return existing;
        }
        if (tokens.Count >= 8192)
        {
            foreach (var expired in tokens.Where(p => DateTimeOffset.UtcNow - p.Value.Created > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToArray())
                tokens.Remove(expired);
            foreach (var expired in runtimeIds.Where(p => !tokens.ContainsKey(p.Value)).Select(p => p.Key).ToArray())
                runtimeIds.Remove(expired);
            foreach (var expired in locators.Where(p => !tokens.ContainsKey(p.Value)).Select(p => p.Key).ToArray())
                locators.Remove(expired);
            if (tokens.Count >= 8192)
                throw new AutomationException("CACHE_LIMIT", "Narrow the inspection query or invalidate the form cache.");
        }
        var token = Guid.NewGuid().ToString("N");
        tokens.Add(token, new Entry(element, window, DateTimeOffset.UtcNow));
        if (runtimeId != null) runtimeIds[runtimeId] = token;
        if (key != null) locators[key] = token;
        return token;
    }
    public string? Find(string key, long window)
    {
        if (locators.TryGetValue(key, out var token))
        {
            try { Get(token, window); Hits++; return token; }
            catch (AutomationException error) when (error.Code == "STALE_ELEMENT") { locators.Remove(key); }
        }
        Misses++;
        return null;
    }
    public AutomationElement Get(string token, long window)
    {
        if (!tokens.TryGetValue(token, out var entry) || entry.Window != window
            || DateTimeOffset.UtcNow - entry.Created > TimeSpan.FromMinutes(2))
            throw new AutomationException("STALE_ELEMENT", "Element reference expired; resolve it in the current window.");
        try
        {
            if (entry.Element.IsAvailable) return entry.Element;
        }
        catch (System.Runtime.InteropServices.COMException) { }
        tokens.Remove(token);
        throw new AutomationException("STALE_ELEMENT", "The MISA control is no longer available.");
    }
    public void Clear() { tokens.Clear(); locators.Clear(); runtimeIds.Clear(); }
}
