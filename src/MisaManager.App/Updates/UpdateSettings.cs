using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MisaManager.Core;

namespace MisaManager.App;

/// <summary>
/// Per-user update settings in %LOCALAPPDATA%\MisaManager\update.json. The GitHub token (needed
/// only while the release repository is private) is encrypted with Windows DPAPI for this user;
/// MISAMANAGER_GITHUB_TOKEN overrides it.
/// </summary>
public sealed class UpdateSettings
{
    public const string TokenVariable = "MISAMANAGER_GITHUB_TOKEN";
    private sealed record Stored(string? Repository, bool CheckOnStartup, string? ProtectedToken);

    public string Repository { get; set; } = DefaultRepository;
    public bool CheckOnStartup { get; set; } = true;
    private string? token;

    /// <summary>owner/name baked in at build time (MSBuild property UpdateRepository).</summary>
    public static string DefaultRepository => typeof(UpdateSettings).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => item.Key == "UpdateRepository")?.Value is { Length: > 0 } value ? value : "ngovanhung2024/MisaManager";

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductIdentity.TechnicalName, "update.json");

    public string? Token => Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment : token;
    public bool HasStoredToken => !string.IsNullOrEmpty(token);
    public bool TokenFromEnvironment => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TokenVariable));

    public void SetToken(string? value) => token = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsValidRepository(string? value)
        => value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$");

    public static UpdateSettings Load()
    {
        var settings = new UpdateSettings();
        try
        {
            if (!File.Exists(FilePath)) return settings;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath), ContractJson.Options);
            if (stored is null) return settings;
            if (IsValidRepository(stored.Repository)) settings.Repository = stored.Repository!;
            settings.CheckOnStartup = stored.CheckOnStartup;
            if (!string.IsNullOrEmpty(stored.ProtectedToken)) settings.token = Unprotect(stored.ProtectedToken);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or FormatException or ExternalException)
        {
            System.Diagnostics.Trace.TraceWarning("Update settings unreadable: {0}", error.Message);
        }
        return settings;
    }

    public void Save()
    {
        if (!IsValidRepository(Repository)) throw new ArgumentException("Repository phải có dạng owner/tên-repo.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var stored = new Stored(Repository, CheckOnStartup, token is null ? null : Protect(token));
        StateFiles.WriteAtomic(FilePath, stored);
    }

    private static string Protect(string value) => Convert.ToBase64String(Dpapi(Encoding.UTF8.GetBytes(value), protect: true));
    private static string Unprotect(string value) => Encoding.UTF8.GetString(Dpapi(Convert.FromBase64String(value), protect: false));

    private static byte[] Dpapi(byte[] data, bool protect)
    {
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = protect ? CryptProtectData(ref input, null, 0, 0, 0, 1, out var output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new ExternalException("DPAPI failed", Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public nint Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
