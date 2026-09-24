using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.Json;
using MisaAutomation.Core;
using MisaAutomation.UIA;

namespace MisaAutomation.Inspector;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        args = LoadPendingDump(args);
        if (args.Contains("--dump", StringComparer.OrdinalIgnoreCase))
            return Dump(args);
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new InspectorForm());
        return 0;
    }

    private static string[] LoadPendingDump(string[] args)
    {
        if (args.Length != 0) return args;
        var request = Path.Combine("C:\\misa", "misa_uia_dump.request");
        if (!File.Exists(request)) return args;
        // The request marker is created for one fixed live session immediately
        // before launch, avoiding command-line loss across the UAC broker.
        return ["--dump", "--pid", "10968", "--hwnd", "132374",
            "--output", "C:\\misa\\misa_uia_controls.json",
            "--error-output", "C:\\misa\\misa_uia_controls.error.txt",
            "--maximum-nodes", "20000"];
    }

    private static int Dump(string[] args)
    {
        try
        {
            var pid = int.Parse(Required(args, "--pid"), System.Globalization.CultureInfo.InvariantCulture);
            var handle = long.Parse(Required(args, "--hwnd"), System.Globalization.CultureInfo.InvariantCulture);
            var output = Path.GetFullPath(Required(args, "--output"));
            var maximumNodes = int.TryParse(Optional(args, "--maximum-nodes"), out var limit) ? limit : 10000;
            var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MisaInvoiceTool", "inspector");
            Directory.CreateDirectory(logFolder);
            using var log = new JsonAutomationLog(Path.Combine(logFolder,
                $"headless-{Environment.ProcessId}.jsonl"));
            var repository = new LocatorRepository();
            // Headless inspection only reads the tree. Event subscriptions can
            // block on custom MISA providers and are unnecessary for a dump.
            using var provider = new UiaProvider(repository, log, subscribeEvents: false,
                checkPermission: false);
            var connected = provider.Connect(handle, pid);
            var windows = provider.Windows().Where(window => !string.IsNullOrWhiteSpace(window.Title))
                .OrderBy(window => window.Handle == connected.Handle ? 0 : 1).ThenBy(window => window.Handle).ToArray();
            var dumps = new List<object>();
            foreach (var window in windows)
            {
                try
                {
                    provider.BindWindow(window.Handle);
                    var nodes = provider.DumpTree(maximumNodes);
                    dumps.Add(new { window, nodes, nodeCount = nodes.Count, error = (string?)null });
                }
                catch (Exception error)
                {
                    dumps.Add(new { window, nodes = Array.Empty<UiaTreeNode>(), nodeCount = 0,
                        error = error.ToString() });
                }
            }
            using var process = Process.GetProcessById(pid);
            string? executable = null;
            try { executable = process.MainModule?.FileName; }
            catch (Win32Exception) { /* UIPI may deny module path at medium integrity. */ }
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            var elevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            var payload = new
            {
                capturedAt = DateTimeOffset.Now,
                backend = "FlaUI.UIA3",
                inspectionMode = elevated ? "elevated_headless_read_only" : "medium_headless_read_only",
                inspector = new
                {
                    processId = Environment.ProcessId,
                    elevated,
                    is64Bit = Environment.Is64BitProcess
                },
                process = new
                {
                    processId = pid,
                    name = process.ProcessName,
                    executable,
                    is64BitOperatingSystem = Environment.Is64BitOperatingSystem
                },
                connected,
                windows = dumps
            };
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var temporary = output + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload, options));
            File.Move(temporary, output, true);
            return 0;
        }
        catch (Exception error)
        {
            var errorPath = Optional(args, "--error-output");
            if (!string.IsNullOrWhiteSpace(errorPath))
                File.WriteAllText(Path.GetFullPath(errorPath), error.ToString());
            return 1;
        }
    }

    private static string Required(string[] args, string name)
        => Optional(args, name) ?? throw new ArgumentException("Missing " + name + ".");

    private static string? Optional(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, name,
            StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
