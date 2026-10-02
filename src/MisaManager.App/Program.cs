using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.IO;
using MisaManager.Automation;
using MisaManager.Core;
using MisaManager.Control;
using MisaManager.Import;

namespace MisaManager.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        args = WaitForPreviousInstance(args);
        if (args.Length > 0) return RunCommandAsync(args).GetAwaiter().GetResult();
        using var instance = new Mutex(true, "Local\\MisaManager.App", out var created);
        if (!created) return 10;
        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        finally { instance.ReleaseMutex(); }
    }

    private static string[] WaitForPreviousInstance(string[] args)
    {
        var index = Array.IndexOf(args, "--wait-for-pid");
        if (index < 0) return args;
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var pid))
        {
            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(30_000);
            }
            catch (ArgumentException) { }
        }
        return args.Where((_, position) => position != index && position != index + 1).ToArray();
    }

    private static async Task<int> RunCommandAsync(string[] args)
    {
        object result;
        var code = 0;
        string? output = null;
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index < 0 ? null : index + 1 < args.Length ? args[index + 1]
                : throw new ArgumentException("Missing argument for " + name);
        }
        try
        {
            output = CommandOutputPath.Resolve(Option("--output"));
            if (args[0] == "--health-check")
            {
                await ControlServer.CheckListenerHealthAsync();
                var profile = WorkflowProfile.Load();
                using var owner = new NativeDispatcher();
                var native = await owner.RunAsync(() =>
                {
                    using var provider = new UiaReadbackProvider();
                    return new { backend = "UIA3/Win32", initialized = true, windows = new MisaWindowDiscovery().FindCandidates() };
                });
                result = new { status = "healthy", product = ProductIdentity.DisplayName,
                    version = typeof(Program).Assembly.GetName().Version?.ToString(),
                    sourceRevision = ProductIdentity.SourceRevision,
                    coreAssemblySha256 = ProductIdentity.RuntimeAssemblySha256,
                    protocolVersion = ProductIdentity.ControlProtocolVersion, schemaVersion = ProductIdentity.StateSchemaVersion,
                    workbookSchemaVersion = ProductIdentity.WorkbookSchemaVersion,
                    locatorProfileVersion = ProductIdentity.LocatorProfileVersion,
                    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    inputSent = false, controlListener = "healthy", native,
                    profile = new { profile.Id, profile.Revision, profile.Status } };
            }
            else if (args[0] == "--check-workbook")
            {
                var path = Option("--path") ?? throw new ArgumentException("--path is required.");
                var reader = new WorkbookReader();
                var workbook = await reader.ReadAsync(path);
                ImportResult? imported = null;
                WorkbookMappingSchema? mapping = null;
                object? importError = null;
                try
                {
                    var importer = new InvoiceWorkbookImporter(reader);
                    var map = ParseMapping(args);
                    var legacyCompany = Option("--legacy-company");
                    var confirmation = legacyCompany is null && !args.Contains("--sum-line-totals") ? null : new ImportConfirmation(
                        args.Contains("--sum-line-totals"), legacyCompany is not null, legacyCompany ?? "", workbook.Sha256,
                        "CLI user confirmation: " + (legacyCompany is null ? "" : "legacy cash-sale defaults; ")
                            + (args.Contains("--sum-line-totals") ? "sum invoice totals from line amounts." : "source invoice totals.")
                            + (args.Contains("--derive-unit-price") ? " MISA derives unit price from source quantity/amount." : ""),
                        args.Contains("--derive-unit-price"));
                    imported = importer.Import(workbook, new(Option("--sheet"), Option("--locale"), ColumnMap: map, Confirmation: confirmation));
                    mapping = importer.DescribeMapping(workbook, imported.Source.Worksheet);
                    if (args.Contains("--save-import-profile"))
                    {
                        using var instance = new Mutex(true, "Local\\MisaManager.App", out var created);
                        if (!created) throw new InvalidOperationException("Đóng manager trước khi lưu cấu hình từ CLI.");
                        try
                        {
                            var generations = new GenerationStore();
                            var active = generations.EnsureActive(ProductIdentity.RuntimeVersion);
                            var profile = new WorkbookProfileStore(new JsonStateStore(StateFiles.GenerationPath(generations.RootPath, active.GenerationId))).SaveAsync(workbook,
                                imported.Source.Worksheet, mapping, map, confirmation: confirmation, locale: Option("--locale")).GetAwaiter().GetResult();
                            imported = importer.Import(workbook, new(Option("--sheet"), Option("--locale"), ColumnMap: map,
                                ImportRevision: profile.Revision, Confirmation: confirmation));
                        }
                        finally { instance.ReleaseMutex(); }
                    }
                }
                catch (WorkbookImportException error) { importError = new { error.Code, error.Message }; }
                code = imported is { IsBlocked: false } ? 0 : 2;
                var selectedInvoice = Option("--invoice-number") is { } number
                    ? imported?.Invoices.SingleOrDefault(invoice => invoice.Identity.Number == number)
                    : null;
                result = new { status = code == 0 ? "valid" : "blocked", inputSent = false,
                    workbook.FilePath, workbook.Sha256, durationMs = workbook.Duration.TotalMilliseconds,
                    sheets = workbook.Sheets.Select(s => new { s.Name, s.Visibility, rowCount = s.Rows.Length,
                        preview = s.Rows.Take(8).Select(r => new { r.Number, cells = r.Cells.Values.OrderBy(c => c.Column)
                            .Where(c => !string.IsNullOrEmpty(c.ParsedValue)).Select(c => new { c.Address, c.ParsedValue, c.CellType, c.InheritedFrom }) }) }),
                    importError, mapping, invoiceCount = imported?.Invoices.Count ?? 0,
                    issues = imported?.Issues.Take(200), totalIssues = imported?.Issues.Count ?? 0,
                    issueCounts = imported?.Issues.GroupBy(issue => new { issue.Code, issue.Severity })
                        .Select(group => new { group.Key.Code, group.Key.Severity, count = group.Count() }),
                    selectedInvoice,
                    invoices = imported?.Invoices.Select(i => new { i.InvoiceId, i.Identity, buyer = i.Buyer.Name,
                        i.GrandTotal, lineCount = i.Lines.Length, i.IsBlocked }) };
            }
            else throw new ArgumentException("Supported commands: --health-check or --check-workbook --path <xlsx> [--sheet <name>] [--locale vi-VN|en-US|invariant] [--legacy-company <name> --sum-line-totals] [--map field=column ...] [--output <json>].");
        }
        catch (Exception error)
        {
            code = 1;
            result = new { status = "error", inputSent = false, error = error.ToString() };
        }
        var json = JsonSerializer.Serialize(result, ContractJson.Options);
        if (output is null) Console.WriteLine(json);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, json, new System.Text.UTF8Encoding(false));
        }
        return code;
    }

    private static ImmutableDictionary<string, int>? ParseMapping(string[] args)
    {
        var entries = args.SkipWhile(arg => arg != "--map").Skip(1).TakeWhile(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (entries.Length == 0) return null;
        var builder = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])) throw new ArgumentException("--map cần field=column.");
            var value = int.TryParse(parts[1], out var column) && column > 0 ? column : throw new ArgumentException("Cột map phải là số dương.");
            if (!builder.TryAdd(parts[0], value)) throw new ArgumentException("Trường map bị trùng: " + parts[0]);
        }
        return builder.ToImmutable();
    }
}
