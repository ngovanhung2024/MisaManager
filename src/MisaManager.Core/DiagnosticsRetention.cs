namespace MisaManager.Core;

public sealed record DiagnosticsRetentionEntry(string Path, long Bytes, DateTime LastWriteUtc, bool Protected);
public sealed record DiagnosticsRetentionReport(long TotalBytes, int FileCount, IReadOnlyList<DiagnosticsRetentionEntry> Entries,
    bool OverQuota, string Message, int DeletedCount = 0, long DeletedBytes = 0);

public static class DiagnosticsRetention
{
    public const long DefaultQuotaBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan DefaultAge = TimeSpan.FromDays(30);

    public static DiagnosticsRetentionReport Inspect(string root, Func<string, bool>? protectedFile = null,
        long quotaBytes = DefaultQuotaBytes, TimeSpan? maximumAge = null)
    {
        if (!Directory.Exists(root)) return new(0, 0, [], false, "Diagnostics folder chưa tồn tại.");
        var cutoff = DateTime.UtcNow - (maximumAge ?? DefaultAge);
        var entries = new List<DiagnosticsRetentionEntry>();
        foreach (var path in StateFiles.Files(root))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                var keep = protectedFile?.Invoke(path) == true;
                entries.Add(new(path, info.Length, info.LastWriteTimeUtc, keep));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        var total = entries.Sum(entry => entry.Bytes);
        var old = entries.Count(entry => entry.LastWriteUtc < cutoff && !entry.Protected);
        var message = total > quotaBytes
            ? $"Diagnostics vượt quota {quotaBytes / (1024 * 1024)} MiB; đang giữ {entries.Count(entry => entry.Protected)} file active/unresolved. Có thể dọn {old} file cũ sau khi kiểm tra." 
            : $"Diagnostics {total / (1024 * 1024)} MiB, {entries.Count} file; chưa cần dọn.";
        return new(total, entries.Count, entries, total > quotaBytes, message);
    }

    public static DiagnosticsRetentionReport Prune(string root, Func<string, bool>? protectedFile = null,
        long quotaBytes = DefaultQuotaBytes, TimeSpan? maximumAge = null)
    {
        var report = Inspect(root, protectedFile, quotaBytes, maximumAge);
        if (report.Entries.Count == 0) return report;
        var cutoff = DateTime.UtcNow - (maximumAge ?? DefaultAge);
        var removable = report.Entries.Where(item => !item.Protected)
            .OrderBy(item => item.LastWriteUtc).ToList();
        var bytes = report.TotalBytes;
        var deleted = 0;
        long deletedBytes = 0;
        foreach (var item in removable)
        {
            if (bytes <= quotaBytes && item.LastWriteUtc >= cutoff) break;
            try
            {
                StateFiles.RejectReparse(item.Path);
                if (protectedFile?.Invoke(item.Path) == true) continue;
                File.Delete(item.Path); bytes -= item.Bytes; deleted++; deletedBytes += item.Bytes;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Diagnostics retention kept {0}: {1}", item.Path, error.Message); }
        }
        var final = Inspect(root, protectedFile, quotaBytes, maximumAge);
        return final with { DeletedCount = deleted, DeletedBytes = deletedBytes,
            Message = final.Message + $" Đã dọn {deleted} file/{deletedBytes / (1024 * 1024)} MiB." };
    }
}
