using System.IO;
using System.Text.Json;
using MisaManager.Automation;
using MisaManager.Core;
using MisaManager.Import;

namespace MisaManager.App;

/// <summary>Composition root: owns state, the native UIA owner and the invoice workflow for the UI.</summary>
public sealed partial class AppServices : IAsyncDisposable
{
    public WorkbookReader WorkbookReader { get; } = new();
    /// <summary>Lá Việt, Lộc Thủy…: the selected company's catalogs, invoice lists and rules are the ones loaded below.</summary>
    public CompanyProfileStore Companies { get; }
    public CompanyProfile Company => Companies.Active;
    // Folder of the selected company; switching company restarts the app so every store reloads.
    private readonly string companyFolder;
    public ItemCatalogStore ItemCatalog { get; }
    public PartyCatalogStore PartyCatalog { get; }
    public PostedInvoiceStore PostedInvoices { get; }
    /// <summary>Purchase invoices already in MISA ("Mua hàng hóa, dịch vụ" export); kept apart from the sales register.</summary>
    public PostedInvoiceStore PostedPurchases { get; }
    public ItemGroupStore ItemGroups { get; }
    public OpeningStockStore OpeningStock { get; }
    public JsonStateStore State { get; }
    public MisaWindowDiscovery Windows { get; } = new();
    private readonly NativeDispatcher nativeOwner = new();
    public NativeObservationService Native { get; }
    public NativeInputService Input { get; }
    public GenerationStore Generations { get; }
    public MigrationRegistry MigrationRegistry { get; } = new(Array.Empty<IStateMigration>());
    public StateMigrationCoordinator Migrations { get; }
    public WorkflowProfile WorkflowProfile { get; } = WorkflowProfile.Load();
    public WorkflowProfile PurchaseProfile { get; } = WorkflowProfile.Load(kind: VoucherKind.Purchase);
    private readonly NativeWorkflowGateway workflowGateway;
    public WorkflowActivity? Activity => workflowGateway.Activity;
    public WorkflowRunner Workflow { get; }
    public InvoiceDraftStore Drafts { get; }
    public WorkbookProfileStore Profiles { get; }
    public UpdateService Updates { get; }
    // Outside generation folders so an update or restart keeps the imported invoice list.
    public ImportSessionStore ImportSessions { get; }
    public ImportSessionStore PurchaseImportSessions { get; }

    public PostedInvoiceStore PostedFor(InvoiceDocument invoice) => invoice.IsPurchase() ? PostedPurchases : PostedInvoices;
    public bool IsPosted(InvoiceDocument invoice) => PostedFor(invoice).Contains(invoice);
    public WorkflowProfile ProfileFor(InvoiceDocument? invoice) => invoice?.IsPurchase() == true ? PurchaseProfile : WorkflowProfile;

    private static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductIdentity.TechnicalName);
    private string ModePath => Path.Combine(companyFolder, "mode.txt");

    /// <summary>Bán hàng or Mua hàng, as last chosen in the tool.</summary>
    public VoucherKind Mode
    {
        get
        {
            try { return File.Exists(ModePath) && File.ReadAllText(ModePath).Trim() == nameof(VoucherKind.Purchase) ? VoucherKind.Purchase : VoucherKind.Sales; }
            catch (IOException) { return VoucherKind.Sales; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ModePath)!);
                File.WriteAllText(ModePath, value.ToString());
            }
            catch (IOException) { }
        }
    }
    public string LogRoot { get; }

    private static string CreationSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductIdentity.TechnicalName, "auto-create.txt");

    /// <summary>Menu toggles "Tự thêm mã hàng mới" / "Tự thêm khách hàng mới"; both on unless switched off (remembered).</summary>
    public bool AllowItemCreation
    {
        get => workflowGateway.AllowItemCreation;
        set { workflowGateway.AllowItemCreation = value; SaveCreationSettings(); }
    }
    public bool AllowPartyCreation
    {
        get => workflowGateway.AllowPartyCreation;
        set { workflowGateway.AllowPartyCreation = value; SaveCreationSettings(); }
    }

    /// <summary>Menu toggle "Tự giữ MISA chạy khi đóng Remote Desktop"; on unless switched off (remembered).</summary>
    public bool AutoConsole
    {
        get => DesktopSession.AutoConsole;
        set { DesktopSession.AutoConsole = value; SaveCreationSettings(); }
    }

    private void LoadCreationSettings()
    {
        DesktopSession.AutoConsole = true;
        try
        {
            if (!File.Exists(CreationSettingsPath)) return;
            var off = File.ReadAllLines(CreationSettingsPath).Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
            workflowGateway.AllowItemCreation = !off.Contains("items=off");
            workflowGateway.AllowPartyCreation = !off.Contains("parties=off");
            DesktopSession.AutoConsole = !off.Contains("console=off");
        }
        catch (IOException) { }
    }

    private void SaveCreationSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CreationSettingsPath)!);
            File.WriteAllLines(CreationSettingsPath, [workflowGateway.AllowItemCreation ? "items=on" : "items=off",
                workflowGateway.AllowPartyCreation ? "parties=on" : "parties=off",
                DesktopSession.AutoConsole ? "console=on" : "console=off"]);
        }
        catch (IOException) { }
    }
    private readonly SemaphoreSlim workflowGate = new(1, 1);
    private Task initialization;
    private readonly object initializationGate = new();
    // A faulted startup is retried on next use (Workflow.InitializeAsync is re-entrant) instead of
    // leaving the app permanently "disconnected" until it is reopened.
    public Task Initialization
    {
        get
        {
            lock (initializationGate)
                return initialization.IsFaulted ? initialization = Workflow.InitializeAsync() : initialization;
        }
    }
    private readonly RunEventStore eventStore;

    public AppServices()
    {
        Companies = new CompanyProfileStore(DataRoot);
        companyFolder = Companies.ActiveFolder;
        ItemCatalog = LoadItemCatalog(companyFolder);
        PartyCatalog = LoadPartyCatalog(companyFolder);
        PostedInvoices = LoadPostedInvoices(companyFolder, "posted-invoices.json");
        PostedPurchases = LoadPostedInvoices(companyFolder, "posted-purchases.json");
        ItemGroups = LoadItemGroups(companyFolder);
        OpeningStock = LoadOpeningStock(companyFolder);
        ImportSessions = new(Path.Combine(companyFolder, "import"));
        PurchaseImportSessions = new(Path.Combine(companyFolder, "import-purchase"));
        Generations = new GenerationStore();
        var appVersion = typeof(AppServices).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var active = Generations.EnsureActive(appVersion);
        State = new JsonStateStore(Path.GetDirectoryName(Path.Combine(Generations.RootPath, active.GenerationId, "state.json"))!);
        Migrations = new StateMigrationCoordinator(MigrationRegistry, Generations);
        Native = new(nativeOwner);
        Input = new(nativeOwner);
        LogRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.TechnicalName, "logs");
        eventStore = new RunEventStore(LogRoot);
        workflowGateway = new NativeWorkflowGateway(Windows, Input, WorkflowProfile, PurchaseProfile);
        workflowGateway.FailureObserver = CaptureFailureUiAsync;
        workflowGateway.ItemCatalog = ItemCatalog;
        workflowGateway.PartyCatalog = PartyCatalog;
        workflowGateway.ItemGroups = ItemGroups;
        workflowGateway.Companies = Companies;
        LoadCreationSettings();
        Workflow = new WorkflowRunner(workflowGateway, State, eventStore);
        workflowGateway.StopSignal = Workflow.StopSignal;
        Drafts = new(State);
        Profiles = new(State);
        Updates = new(UpdateSettings.Load(), () => Workflow.Current is { } current && !WorkflowRunner.IsTerminal(current.Snapshot.Status));
        initialization = InitializeRuntimeAsync();
    }

    // Kept outside generation folders so rebuilds and "Xóa lịch sử" never drop created item codes.
    private static ItemCatalogStore LoadItemCatalog(string folder)
    {
        var path = Path.Combine(folder, "item-catalog.json");
        try { return new ItemCatalogStore(path); }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
            return new ItemCatalogStore(path);
        }
        catch (IOException)
        {
            // Briefly locked (antivirus/backup): retry once before giving up.
            Thread.Sleep(500);
            return new ItemCatalogStore(path);
        }
    }

    private static PartyCatalogStore LoadPartyCatalog(string folder)
    {
        var path = Path.Combine(folder, "party-catalog.json");
        try { return new PartyCatalogStore(path); }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
            return new PartyCatalogStore(path);
        }
    }

    private static PostedInvoiceStore LoadPostedInvoices(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        try { return new PostedInvoiceStore(path); }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
            return new PostedInvoiceStore(path);
        }
    }

    private static ItemGroupStore LoadItemGroups(string folder)
    {
        var path = Path.Combine(folder, "item-groups.json");
        try { return new ItemGroupStore(path); }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
            return new ItemGroupStore(path);
        }
    }

    private static OpeningStockStore LoadOpeningStock(string folder)
    {
        var path = Path.Combine(folder, "opening-stock.json");
        try { return new OpeningStockStore(path); }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
            return new OpeningStockStore(path);
        }
    }

    private async Task InitializeRuntimeAsync()
    {
        Generations.Retain(maximumGenerations: 2, minimumAge: TimeSpan.FromDays(30),
            hasUnresolvedRun: Generations.HasUnresolvedRun);
        await Workflow.InitializeAsync();
        await Task.Run(() => eventStore.Prune());
    }

    private async Task EnsureInitializedAsync(CancellationToken token)
    {
        try { await Initialization.WaitAsync(token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Workflow.InitializeAsync is re-entrant, so a faulted startup is retried per call.
            await Workflow.InitializeAsync(token);
        }
    }

    /// <summary>Enters one invoice end to end: fill, reconcile, then Cất when it matches or Bỏ when it does not.</summary>
    public async Task<OperationResult> RunInvoiceAsync(InvoiceDocument invoice, string windowIdentity, CancellationToken token = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(windowIdentity))
            return new(requestId, OperationStatuses.Blocked, "Chưa xác định được phiên MISA đang mở.");
        await workflowGate.WaitAsync(token);
        try
        {
            await ItemCatalogWorkbook.EnsureImportedProvenanceAsync(WorkbookReader, ItemCatalog, token);
            await EnsureInitializedAsync(token);
            var result = await Workflow.RunLiveAsync(invoice, "", windowIdentity, token, liveValidation: true,
                WorkflowProfile.Revision, allowKgToTon: true, requestId, requestId, WorkflowExecutionMode.LiveProduction, discardOnFailure: true);
            var status = result.Outcome is "saved" or "discarded" or "existing" or WorkflowRunner.ErrorDiscardedOutcome
                ? OperationStatuses.Completed : OperationStatuses.Blocked;
            var message = result.Outcome switch
            {
                "paused" => "Đã tạm dừng theo yêu cầu sau thao tác vừa hoàn tất; chứng từ chưa Cất.",
                "saved" => "Đối chiếu khớp và đã Cất hóa đơn.",
                "discarded" => "Đối chiếu không khớp, đã Bỏ chứng từ và lưu hồ sơ kiểm chứng.",
                "existing" => "Hóa đơn đã tồn tại trong MISA: đã chọn Không, đóng chứng từ và chuyển sang hóa đơn tiếp theo.",
                WorkflowRunner.ErrorDiscardedOutcome => "Nhập hóa đơn bị lỗi: " + result.Error + " Đã Bỏ chứng từ (không Cất); lỗi lưu ở tab Hiệu năng.",
                _ => result.MisaClean
                    ? "Nhập hóa đơn bị lỗi; đã dừng (chưa mở chứng từ nào). Bấm \"Copy lỗi\" để gửi cho AI."
                    : "Nhập hóa đơn bị lỗi; đã dừng, chứng từ vẫn mở trong MISA (không Bỏ, không nhập lại). Kiểm tra rồi đóng chứng từ; bấm \"Copy lỗi\" để gửi cho AI."
            };
            var data = FailureData(result.Context, result.Verification, result.Error);
            if ((status != OperationStatuses.Completed || result.Outcome == WorkflowRunner.ErrorDiscardedOutcome) && result.Outcome != "paused")
                await SaveFailureDataAsync(result.Context.Snapshot.RunId, data);
            return new(requestId, status, message, result.Context.Snapshot.InputMayHaveBeenSent, data);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (WorkflowRunner.RefusedBeforeStart(error)
            && (Workflow.Current is not { } current || WorkflowRunner.IsTerminal(current.Snapshot.Status)))
        {
            return new(requestId, OperationStatuses.Blocked, "Chưa nhập hóa đơn này vào MISA: " + error.Message, false,
                error.ToString(), RefusedBeforeStart: true);
        }
        catch (Exception error)
        {
            return new(requestId, OperationStatuses.Blocked, "Không thể bắt đầu nhập hóa đơn: " + error.Message, false, error.ToString());
        }
        finally { workflowGate.Release(); }
    }

    /// <summary>Cancels the step in flight. Not serialized: it must be able to interrupt a running invoice.</summary>
    public async Task<OperationResult> PauseAsync(CancellationToken token = default)
    {
        try
        {
            await Workflow.PauseAsync(token);
            return new("ui", OperationStatuses.Completed, "Đã tạm dừng; chứng từ MISA không bị đóng, Cất hoặc Bỏ.", Data: Workflow.Current?.Snapshot);
        }
        catch (Exception error) { return new("ui", OperationStatuses.Blocked, error.Message); }
    }

    public async Task<OperationResult> ResumeAsync(CancellationToken token = default)
    {
        await workflowGate.WaitAsync(token);
        try
        {
            await EnsureInitializedAsync(token);
            var resumed = await Workflow.ResumeAsync(token);
            return new("ui", OperationStatuses.Completed, "Đã tiếp tục từ checkpoint đã lưu.", Data: resumed.Snapshot);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return new("ui", OperationStatuses.Blocked, error.Message); }
        finally { workflowGate.Release(); }
    }

    /// <summary>Bỏ the voucher a Dừng ngay left half-entered, so its invoice is entered again from scratch.</summary>
    public async Task<OperationResult> DiscardStoppedVoucherAsync(CancellationToken token = default)
    {
        await workflowGate.WaitAsync(token);
        try
        {
            await EnsureInitializedAsync(token);
            var result = await Workflow.DiscardStoppedVoucherAsync(token);
            return result?.Snapshot.Status is null or WorkflowStatus.Discarded
                ? new("ui", OperationStatuses.Completed, "Đã Bỏ chứng từ dở của lần Dừng ngay trước.", Data: result?.Snapshot)
                : new("ui", OperationStatuses.Blocked, "Không Bỏ được chứng từ dở của lần Dừng ngay trước: "
                    + result.Snapshot.LastMessage + " Đóng (Bỏ) chứng từ đó trong MISA rồi chạy lại.", Data: result.Snapshot);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new("ui", OperationStatuses.Blocked, "Không Bỏ được chứng từ dở của lần Dừng ngay trước: " + error.Message);
        }
        finally { workflowGate.Release(); }
    }

    public async Task<OperationResult> AbandonStoppedSessionAsync(CancellationToken token = default)
    {
        await workflowGate.WaitAsync(token);
        try
        {
            await EnsureInitializedAsync(token);
            var abandoned = await Workflow.AbandonStoppedSessionAsync(token);
            return new("ui", OperationStatuses.Completed,
                abandoned?.SessionDetached == true
                    ? "Đã bỏ checkpoint cũ; bắt đầu phiên Thêm vào MISA mới."
                    : "Không có checkpoint tạm dừng cần bỏ.", Data: abandoned?.Snapshot);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new("ui", OperationStatuses.Blocked, error.Message);
        }
        finally { workflowGate.Release(); }
    }

    public async Task<OperationResult> ReconcileAsync(string expectedRevision, CancellationToken token = default)
    {
        await workflowGate.WaitAsync(token);
        try
        {
            await EnsureInitializedAsync(token);
            if (Workflow.Current is not { } current || current.Invoice.Fingerprint != expectedRevision)
                return new("ui", OperationStatuses.Blocked, "Hóa đơn đang chọn không gắn với chứng từ MISA hiện tại.");
            var comparison = await Workflow.ReconcileAsync(token);
            return new("ui", comparison.Matched ? OperationStatuses.Completed : OperationStatuses.Blocked, comparison.Matched
                ? "Đã đối chiếu khách hàng, ngày, từng dòng và tổng MISA với nguồn." : "Dữ liệu MISA có sai khác với nguồn.", Data: comparison);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return new("ui", OperationStatuses.Blocked, error.Message); }
        finally { workflowGate.Release(); }
    }

    /// <summary>
    /// Read-only comparison of the voucher open in MISA with the candidate invoice carrying its number; shares the single
    /// UIA input owner.
    /// </summary>
    public async Task<OpenVoucherComparison> CompareOpenVoucherAsync(IReadOnlyList<InvoiceDocument> candidates, WindowIdentity window,
        CancellationToken token = default)
    {
        await workflowGate.WaitAsync(token);
        try { return await workflowGateway.CompareOpenVoucherAsync(candidates, window, token); }
        finally { workflowGate.Release(); }
    }

    /// <summary>Checks every selector of the screens currently visible in MISA against the profile.</summary>
    public async Task<ScreenCheckReport> CheckScreensAsync(WindowIdentity window, CancellationToken token = default)
        => await Input.CheckScreensAsync(window, WorkflowProfile.Screens, token);

    // Each step is independent and best-effort so one failure (or a stuck UIA call) cannot skip the rest or hang exit.
    public async ValueTask DisposeAsync()
    {
        async Task Step(string name, Func<Task> action)
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Shutdown {0}: {1}", name, error.Message); }
        }
        await Step("native", () => Native.DisposeAsync().AsTask());
        await Step("input", () => Input.DisposeAsync().AsTask());
        await Step("owner", () => Task.Run(nativeOwner.Dispose));
        Updates.Dispose();
    }
}
