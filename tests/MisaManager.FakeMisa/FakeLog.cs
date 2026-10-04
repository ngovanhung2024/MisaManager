namespace MisaManager.FakeMisa;

/// <summary>Event log of the fake ("fake-events.log" next to the state file) for diagnosing failed UI tests.</summary>
public static class FakeLog
{
    private static string? path;

    public static void Open(string? statePath)
        => path = statePath is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statePath))!, "fake-events.log");

    public static void Write(string message)
    {
        if (path is null) return;
        try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }
}
