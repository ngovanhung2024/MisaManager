namespace MisaManager.Core;

/// <summary>The last imported workbook as shown in the tool: its invoices, issues and ticks.</summary>
public sealed record ImportSessionSnapshot(string FilePath, string Sheet, ImportResult Result, IReadOnlyList<string> Checked);

/// <summary>
/// Keeps the imported invoice list across restarts and updates, so the workbook need not be imported again.
/// Lives outside generation folders (like the item catalog). Ticks are a separate small file because they change often.
/// </summary>
public sealed class ImportSessionStore(string rootPath)
{
    private const string SessionPath = "import-session.json";
    private const string CheckedPath = "import-checked.json";
    private readonly JsonStateStore store = new(rootPath);

    public string RootPath => store.RootPath;

    public async Task SaveAsync(string filePath, string sheet, ImportResult result, IEnumerable<string> ticked, CancellationToken token = default)
    {
        await store.WriteAsync("import-session", SessionPath, new ImportSessionSnapshot(filePath, sheet, result, []), token);
        await SaveCheckedAsync(ticked, token);
    }

    public Task SaveCheckedAsync(IEnumerable<string> ticked, CancellationToken token = default)
        => store.WriteAsync("import-checked", CheckedPath, ticked.Order(StringComparer.Ordinal).ToArray(), token);

    /// <summary>Null when nothing was saved or the saved session is unreadable (it is then simply not restored).</summary>
    public async Task<ImportSessionSnapshot?> LoadAsync(CancellationToken token = default)
    {
        try
        {
            if (await store.ReadAsync<ImportSessionSnapshot>("import-session", SessionPath, token) is not { } saved) return null;
            string[] ticked;
            try { ticked = await store.ReadAsync<string[]>("import-checked", CheckedPath, token) ?? []; }
            catch (InvalidDataException) { ticked = []; }
            var available = saved.Result.Invoices.Select(invoice => invoice.Fingerprint).ToHashSet(StringComparer.Ordinal);
            return saved with { Checked = [.. ticked.Where(available.Contains)] };
        }
        catch (InvalidDataException) { return null; }
    }

}
