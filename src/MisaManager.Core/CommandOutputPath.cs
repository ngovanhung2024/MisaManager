namespace MisaManager.Core;

public static class CommandOutputPath
{
    public static string? Resolve(string? output)
    {
        if (output is null) return null;
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Output path must not be empty.");
        var full = Path.GetFullPath(output);
        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must be .json.");
        StateFiles.RejectReparse(full);
        var directory = Path.GetDirectoryName(full)!;
        if (File.Exists(Path.Combine(directory, "MisaManager.sln")))
            throw new ArgumentException("Do not write CLI output to the project root. Use out/scratch/<session>/<name>.json or omit --output for stdout.");
        return full;
    }
}
