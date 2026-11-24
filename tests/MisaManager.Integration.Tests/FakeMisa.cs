using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using MisaManager.Automation;

namespace MisaManager.Integration.Tests;

public sealed record FakeLine(string Code, string Name, string Unit, string DebitAccount, string RevenueAccount,
    decimal Quantity, decimal UnitPrice, decimal Amount, string TaxRate, decimal TaxAmount, string Stock = "", string CreditAccount = "", string CostItem = "");

public sealed record FakeVoucher(string VoucherNumber, string InvoiceNumber, string Series, string CustomerCode,
    string CustomerName, string CustomerTaxCode, string PostedDate, string VoucherDate, string IssuedDate, string Payment,
    IReadOnlyList<FakeLine> Lines, decimal Subtotal, decimal TaxTotal, decimal GrandTotal, string VoucherType = "");

public sealed record FakeItem(string Code, string Name, string Unit, string TaxRate, decimal Price = 0);

public sealed record FakeCustomer(string Code, string Name, string Address, string TaxCode, bool Individual = false, string IdNumber = "");

public sealed record FakeState(IReadOnlyList<FakeVoucher> Vouchers, IReadOnlyList<FakeItem> CreatedItems,
    IReadOnlyList<FakeCustomer>? CreatedCustomers = null);

/// <summary>Scenario switches understood by MISA.SME.exe (see tests/MisaManager.FakeMisa/Scenario.cs).</summary>
public sealed record FakeScenario(bool SaveClosesEditor = true, IReadOnlyList<FakeVoucher>? ExistingVouchers = null,
    bool TaxRoundingPrompt = false, decimal TaxOffset = 0, IReadOnlyList<string>? TakenItemCodes = null, bool ShowLoginWindow = false,
    bool CustomerTaxUnlisted = false, IReadOnlyList<FakeItem>? AdditionalItems = null,
    bool IncompleteItemLookup = false, string? CreatedItemNameOverride = null, bool ItemLookupUsesVoucherColumns = false,
    decimal TaxRoundingDifference = 1, string UserMouseNudge = "", bool UserSwitchesTabs = false,
    bool CloseSwallowedOnceAfterSave = false, bool TaxPromptAsLabel = false, int SaveBusyMilliseconds = 0,
    bool SaveKeepsCatEnabled = false, bool NewCustomerAddressReadOnly = false, int TaxPromptBusyMilliseconds = 0,
    bool Purchase = false, IReadOnlyList<FakeCustomer>? AdditionalSuppliers = null, int PurchaseDefaultsDelayMilliseconds = 0,
    int LineErrorRow = -1, int RowRedrawAfterAmountMilliseconds = 0,
    bool ScrollSidewaysAfterAmount = false, decimal UnitPriceDrift = 0, bool TaxPromptPerRate = false,
    int QuantityIgnoredRow = -1, bool TaxpayerStatusPrompt = false, int GridUnavailableAfterTaxTabMilliseconds = 0,
    bool GridCellsRecreatedOnReadAfterTaxTab = false, bool GridFooterOutside = false, int GridFooterCountOffset = 0,
    bool GridShadowBandOnTaxTab = false, bool GridShadowOverlayOnTaxTab = false,
    bool GridCelllessWhileEditorInactive = false, bool MainActivatedOnFirstTaxAmountRead = false,
    int SessionDisconnectedMilliseconds = 0, bool GhostRowAfterItemCode = false, int BusyAfterQuantityClickMilliseconds = 0,
    bool R20UnitList = false, int SavePostingMilliseconds = 0, bool QuantityIgnoredInFirstVoucherOnly = false,
    int SaveProofDelayMilliseconds = 0, bool OutOfMemoryOnSave = false, string PurchaseItemStock = "",
    string PurchaseItemStockAccount = "", string ExpenseItemAccount = "632", bool EditorUnowned = false,
    string VoucherTypeRemembered = "", int QuantityDecimals = -1, int VoucherTypeRetitleMilliseconds = 0,
    bool TaxRateValueIsKey = false, bool TaxRateListOpensOnClick = false, int DirtyPromptDelayMilliseconds = 0,
    bool TaxRateListHiddenFromUia = false, string TaxRateListKeyFault = "", bool TaxRateStatusStaleWhileOpen = false,
    bool TaxRateListTextAccessible = false, bool TaxRateTextIgnored = false, bool TaxRateRequiresKeyboardText = false,
    bool TaxRateEditorOutsideCellTree = false, bool MainCoversVoucherOnCustomerFocus = false,
    string InitialInvoiceFilter = "", bool IgnoreInvoiceFilterCommit = false,
    bool PurchaseListCellsUseCustomProvider = false, bool PurchaseEditUsesCustomProvider = false,
    bool PurchaseEditInitiallyDisabled = false, bool PurchaseRowOpenIgnored = false,
    bool CostItemListDrawnAsCustomText = false, bool CostItemListHiddenFromUia = false,
    bool PurchaseEditRemainsDisabledAfterSave = false);


/// <summary>One fake MISA process per test, with its own scenario and state file.</summary>
public sealed class FakeMisa : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Process process;
    private readonly bool disconnectsSession;

    private FakeMisa(Process process, string root, WindowIdentity window, bool disconnectsSession)
    {
        this.process = process;
        this.disconnectsSession = disconnectsSession;
        Root = root;
        Window = window;
    }

    public string Root { get; }
    public WindowIdentity Window { get; }
    public string StatePath => Path.Combine(Root, "fake-state.json");

    public static string Executable
    {
        get
        {
            if (Environment.GetEnvironmentVariable("MISA_FAKE_EXE") is { Length: > 0 } configured) return configured;
            // out/bin/MisaManager.Integration.Tests/<Configuration>/<tfm>/ → out/bin/MisaManager.FakeMisa/<Configuration>/<tfm>/
            var self = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var tfm = self.Name;
            var configuration = self.Parent!.Name;
            var bin = self.Parent.Parent!.Parent!.FullName;
            return Path.Combine(bin, "MisaManager.FakeMisa", configuration, tfm, "MISA.SME.exe");
        }
    }

    public static async Task<FakeMisa> StartAsync(FakeScenario? scenario = null, CancellationToken token = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "misa-fake-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        var scenarioPath = Path.Combine(root, "scenario.json");
        await File.WriteAllTextAsync(scenarioPath, JsonSerializer.Serialize(scenario ?? new FakeScenario(), Json), token);
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Executable)! };
        foreach (var argument in new[] { "--scenario", scenarioPath, "--state", Path.Combine(root, "fake-state.json") })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start " + Executable);
        try
        {
            var discovery = new MisaWindowDiscovery();
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException($"Fake MISA exited with {process.ExitCode}.");
                var window = discovery.FindCandidates().FirstOrDefault(candidate => candidate.ProcessId == process.Id
                    && candidate.Title.StartsWith("MISA SME 2023 R20 Enterprise - ", StringComparison.Ordinal));
                if (window is not null)
                {
                    // Let WPF finish its first layout before the automation reads bounds.
                    await Task.Delay(1000, token);
                    return new FakeMisa(process, root, discovery.Bind(window.WindowHandle, process.Id),
                        scenario?.SessionDisconnectedMilliseconds > 0);
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Fake MISA main window did not appear.");
                await Task.Delay(250, token);
            }
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    public string WindowIdentityText => $"pid={Window.ProcessId};start={Window.ProcessStartUtc:O};hwnd={Window.WindowHandle};exe={Window.ExecutablePath}";

    /// <summary>Closes the open voucher as a user would: Đóng, then No (do not save) on the "data changed" prompt.</summary>
    public async Task CloseVoucherAsUserAsync(CancellationToken token)
    {
        using var automation = new UIA3Automation();
        var conditions = automation.ConditionFactory;
        AutomationElement[] Buttons() => automation.GetDesktop().FindAllChildren(conditions.ByProcessId(Window.ProcessId))
            .SelectMany(window => window.FindAllDescendants(conditions.ByControlType(ControlType.Button))).ToArray();
        var voucherButtons = automation.GetDesktop().FindAllChildren(conditions.ByProcessId(Window.ProcessId))
            .Select(window => window.FindAllDescendants(conditions.ByControlType(ControlType.Button)))
            .First(buttons => buttons.Any(button => button.Name == "Cất"));
        voucherButtons.First(button => button.Name == "Đóng").Patterns.Invoke.Pattern.Invoke();
        // MessageBox buttons keep their Win32 ids as AutomationId: 7 = IDNO.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100, token);
            if (Buttons().FirstOrDefault(button => button.AutomationId == "7") is not { } no) continue;
            no.Patterns.Invoke.Pattern.Invoke();
            break;
        }
        for (var attempt = 0; attempt < 50 && Buttons().Any(button => button.Name == "Cất"); attempt++) await Task.Delay(100, token);
    }

    public FakeState State()
        => File.Exists(StatePath) ? JsonSerializer.Deserialize<FakeState>(File.ReadAllText(StatePath), Json)! : new FakeState([], []);

    /// <summary>Full-screen PNG into the test output folder; used when a UI test fails.</summary>
    public static string? Screenshot(string name)
    {
        try
        {
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "screenshots");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".png");
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or ArgumentException
            or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        process.WaitForExit(5000);
        process.Dispose();
        // A fake killed while "disconnected" must not leave this desktop without input.
        if (disconnectsSession) RestoreInputDesktop();
    }

    private static void RestoreInputDesktop()
    {
        var home = OpenDesktop("Default", 0, false, 0x0100);
        if (home == 0) return;
        SwitchDesktop(home);
        CloseDesktop(home);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "OpenDesktopW")]
    private static extern nint OpenDesktop(string name, uint flags, bool inherit, uint access);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SwitchDesktop(nint desktop);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
}
