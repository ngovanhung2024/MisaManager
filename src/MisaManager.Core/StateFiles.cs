using System.Text.Json;
using System.Text.RegularExpressions;

namespace MisaManager.Core;

public static partial class StateFiles
{
    [GeneratedRegex(@"^[0-9]{8}-[0-9]{6}-[a-f0-9]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex GenerationPattern();

    public static string GenerationPath(string root, string id)
    {
        if (!GenerationPattern().IsMatch(id)) throw new InvalidDataException("Invalid generation identity.");
        return UnderRoot(root, id);
    }

    public static string UnderRoot(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Replace('\\', '/').Split('/').Any(p => p is "" or "." or ".." || p.Contains(':')))
            throw new InvalidDataException("State path must remain inside its owned root.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        RejectReparse(full);
        return full;
    }

    public static void RejectReparse(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse point is not a state location: " + current);
    }

    public static IEnumerable<string> Files(string root)
    {
        RejectReparse(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            RejectReparse(entry);
            if (Directory.Exists(entry)) { foreach (var file in Files(entry)) yield return file; }
            else yield return entry;
        }
    }

    public static void WriteAtomic<T>(string path, T value)
    {
        RejectReparse(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value, ContractJson.Options); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
