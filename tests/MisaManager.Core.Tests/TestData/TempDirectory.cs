namespace MisaManager.Core.Tests.TestData;

/// <summary>Unique scratch folder under the OS temp directory, removed on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "MisaManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    public string WriteText(string relative, string content)
    {
        var path = Combine(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
