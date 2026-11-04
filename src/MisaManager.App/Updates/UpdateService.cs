using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MisaManager.Core;

namespace MisaManager.App;

public sealed record UpdateInfo(Version Version, string Tag, string Title, string Notes, string HtmlUrl,
    long AssetId, string AssetName, long AssetSize, string Sha256);

/// <summary>
/// Checks GitHub Releases of <see cref="UpdateSettings.Repository"/> for a newer MSI, downloads and
/// hash-verifies it, then hands over to msiexec (per-user MajorUpgrade, no admin rights).
/// </summary>
public sealed class UpdateService(UpdateSettings settings, Func<bool> hasUnresolvedRun) : IDisposable
{
    private readonly HttpClient http = CreateClient();
    public UpdateSettings Settings { get; } = settings;

    public static Version CurrentVersion
    {
        get
        {
            var version = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(ProductIdentity.TechnicalName, CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private HttpRequestMessage Request(string url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(accept);
        // HttpClient drops Authorization when the asset download redirects to another host.
        if (Settings.Token is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>Returns the newest release when it is newer than this build, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        using var request = Request($"https://api.github.com/repos/{Settings.Repository}/releases/latest", "application/vnd.github+json");
        using var response = await http.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UpdateException(Settings.Token is null
                ? $"Không thấy bản phát hành nào của {Settings.Repository}. Nếu repo đang private, hãy nhập GitHub token trong \"Cài đặt cập nhật\"."
                : $"Không thấy bản phát hành nào của {Settings.Repository} (hoặc token không có quyền đọc repo).");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UpdateException("GitHub từ chối truy cập (token sai/hết hạn hoặc vượt giới hạn truy vấn).");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var release = document.RootElement;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseTag(tag, out var version) || version <= CurrentVersion) return null;
        JsonElement? msi = null;
        JsonElement? sums = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) msi ??= asset;
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = asset;
        }
        if (msi is not { } installer) throw new UpdateException($"Bản {tag} chưa có file cài đặt .msi.");
        var assetName = installer.GetProperty("name").GetString()!;
        var sha = installer.TryGetProperty("digest", out var digest) && digest.GetString() is { } value
            && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..].ToLowerInvariant() : null;
        if (sums is { } sumsAsset)
            sha = await ReadPublishedHashAsync(sumsAsset.GetProperty("id").GetInt64(), assetName, token) ?? sha;
        if (sha is null || sha.Length != 64) throw new UpdateException($"Bản {tag} thiếu mã SHA-256 để kiểm tra file cài đặt.");
        return new UpdateInfo(version, tag, release.TryGetProperty("name", out var title) ? title.GetString() ?? tag : tag,
            release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            release.GetProperty("html_url").GetString() ?? "", installer.GetProperty("id").GetInt64(), assetName,
            installer.GetProperty("size").GetInt64(), sha);
    }

    private async Task<string?> ReadPublishedHashAsync(long assetId, string assetName, CancellationToken token)
    {
        using var request = Request($"https://api.github.com/repos/{Settings.Repository}/releases/assets/{assetId}", "application/octet-stream");
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return null;
        foreach (var line in (await response.Content.ReadAsStringAsync(token)).Split('\n'))
        {
            var parts = line.Trim().Split((char[])[' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Equals(assetName, StringComparison.OrdinalIgnoreCase)) return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Downloads the MSI next to the state folder and verifies its SHA-256.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.TechnicalName, "updates");
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.EnumerateFiles(directory, "*.msi")) TryDelete(old);
        var path = Path.Combine(directory, $"MisaManager-{update.Version}.msi");
        var partial = path + ".partial";
        using (var request = Request($"https://api.github.com/repos/{Settings.Repository}/releases/assets/{update.AssetId}", "application/octet-stream"))
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.AssetSize;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                received += read;
                if (total > 0) progress?.Report((double)received / total);
            }
        }
        string actual;
        await using (var stream = File.OpenRead(partial))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
        if (actual != update.Sha256)
        {
            TryDelete(partial);
            throw new UpdateException("File cài đặt tải về không khớp SHA-256 đã công bố; đã hủy cập nhật.");
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>Publisher of an Authenticode-signed file, or null when unsigned.</summary>
    public static string? Publisher(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Folder the per-user MSI installs the app into (Package.wxs: LocalAppData\Programs\MisaManager\app).</summary>
    public static string InstalledExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "MisaManager", "app", "MisaManager.App.exe");

    private static string UpdatesDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductIdentity.TechnicalName, "updates");

    private static string ResultFile => Path.Combine(UpdatesDirectory, "last-install.txt");

    /// <summary>Refuses while a checkpoint is still attached (the caller releases it first); otherwise starts msiexec after this process exits and restarts the app.</summary>
    public void InstallAfterExit(string msiPath, Version version)
    {
        if (hasUnresolvedRun())
            throw new UpdateException("Không bỏ được checkpoint của phiên Thêm vào MISA cũ nên chưa cập nhật. Thử lại sau.");
        var directory = Path.GetDirectoryName(msiPath)!;
        var log = Path.Combine(directory, "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
        var script = Path.Combine(directory, "install-update.cmd");
        var app = Environment.ProcessPath ?? throw new UpdateException("Không xác định được đường dẫn app để mở lại.");
        var installed = InstalledExecutable;
        Directory.CreateDirectory(UpdatesDirectory);
        // Line 1-2 written now, msiexec exit code appended by the script; read back on the next start.
        File.WriteAllText(ResultFile, version.ToString(3) + "\r\n" + log + "\r\n");
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            $"set PID={Environment.ProcessId}",
            ":wait",
            // ping is the delay: timeout.exe exits at once when the hidden console has no stdin.
            "tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)",
            $"msiexec /i \"{msiPath}\" /passive /norestart /l*v \"{log}\"",
            $">>\"{ResultFile}\" echo %ERRORLEVEL%",
            // Relaunch the MSI-installed copy: the running exe may be a stale copy outside the install folder.
            $"if exist \"{installed}\" (start \"\" \"{installed}\") else (start \"\" \"{app}\")",
            ""));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory });
    }

    /// <summary>Result of the last in-app install, consumed once; null when it succeeded or none is pending.</summary>
    public static string? TakeFailedInstallMessage()
    {
        string[] lines;
        try
        {
            if (!File.Exists(ResultFile)) return null;
            lines = File.ReadAllLines(ResultFile);
            File.Delete(ResultFile);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        if (lines.Length < 2 || !TryParseTag(lines[0], out var target)) return null;
        var log = lines[1].Trim();
        var running = Environment.ProcessPath ?? "";
        var code = lines.Length > 2 && int.TryParse(lines[2].Trim(), CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null;
        return InstallFailureMessage(target, CurrentVersion, code, log, running, InstalledExecutable);
    }

    internal static string? InstallFailureMessage(Version target, Version current, int? exitCode, string log, string running, string installed)
    {
        if (current >= target) return null;
        if (exitCode is not (0 or 3010))
        {
            var reason = exitCode switch
            {
                null => "trình cài đặt không chạy xong (app cũ có thể chưa đóng hẳn)",
                1602 => "đã bấm hủy trong cửa sổ cài đặt",
                1618 => "đang có một bộ cài khác chạy trên máy",
                1625 or 1643 => "chính sách Windows chặn cài đặt",
                1638 => "đã có bản khác cài trên máy",
                _ => "Windows Installer báo lỗi",
            };
            return $"Cài bản {target.ToString(3)} không thành công: {reason}"
                + (exitCode is { } code ? $" (mã lỗi {code})" : "") + $".\n\nNhật ký cài đặt: {log}\n\nĐóng mọi cửa sổ MISA Excel Manager rồi thử cập nhật lại.";
        }
        if (running.Length > 0 && !string.Equals(Path.GetFullPath(running), Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase))
            return $"Đã cài bản {target.ToString(3)} vào:\n{installed}\n\nnhưng app đang chạy là bản {current.ToString(3)} ở chỗ khác:\n{running}\n\n"
                + "Hãy đóng app này và mở bằng biểu tượng \"MISA Excel Manager\" trên Desktop hoặc menu Start; xóa shortcut/bản cũ để không mở nhầm.";
        return $"Đã chạy cài bản {target.ToString(3)} nhưng app vẫn là bản {current.ToString(3)}. Khởi động lại máy rồi mở lại app.\n\nNhật ký cài đặt: {log}";
    }

    internal static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0) return false;
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => http.Dispose();
}

public sealed class UpdateException(string message) : Exception(message);
