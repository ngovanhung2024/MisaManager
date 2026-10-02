using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MisaManager.Automation;
using MisaManager.Control;
using MisaManager.Core;
using MisaManager.Import;

namespace MisaManager.App;

public sealed partial class AppServices : IAsyncDisposable
{
    public WorkbookReader WorkbookReader { get; } = new();
    public ItemCatalogStore ItemCatalog { get; } = LoadItemCatalog();
    public JsonStateStore State { get; }
    public MisaWindowDiscovery Windows { get; } = new();
    private readonly NativeDispatcher nativeOwner = new();
    public NativeObservationService Native { get; }
    public NativeInputService Input { get; }
    public GenerationStore Generations { get; }
    public MigrationRegistry MigrationRegistry { get; } = new(Array.Empty<IStateMigration>());
    public StateMigrationCoordinator Migrations { get; }
    public MisaR20Profile MisaProfile { get; } = new();
    public WorkflowProfile WorkflowProfile { get; } = WorkflowProfile.Load();
    private readonly NativeWorkflowGateway workflowGateway;
    public WorkflowActivity? Activity => workflowGateway.Activity;
    public ControlServer Control { get; }
    public WorkflowRunner Workflow { get; }
    public RepairService Repairs { get; }
    public InvoiceDraftStore Drafts { get; }
    public WorkbookProfileStore Profiles { get; }
    private readonly ConcurrentDictionary<string, OperationStatusSnapshot> operations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim directGate = new(1, 1);
    public Task Initialization { get; }
    private readonly RunEventStore eventStore;

    public AppServices()
    {
        Generations = new GenerationStore();
        var appVersion = typeof(AppServices).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var active = Generations.EnsureActive(appVersion);
        State = new JsonStateStore(Path.GetDirectoryName(Path.Combine(Generations.RootPath, active.GenerationId, "state.json"))!);
        Migrations = new StateMigrationCoordinator(MigrationRegistry, Generations);
        Native = new(nativeOwner);
        Input = new(nativeOwner);
        eventStore = new RunEventStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.TechnicalName, "logs"));
        workflowGateway = new NativeWorkflowGateway(Windows, Input, WorkflowProfile);
        workflowGateway.FailureObserver = CaptureFailureUiAsync;
        workflowGateway.ItemCatalog = ItemCatalog;
        Workflow = new WorkflowRunner(workflowGateway, State, eventStore);
        Repairs = new(State);
        Drafts = new(State);
        Profiles = new(State);
        Initialization = InitializeRuntimeAsync();
        Control = new ControlServer(new ControlDispatcher(DispatchAsync));
        Control.Start();
    }

    // Kept outside generation folders so rebuilds and "Xóa lịch sử" never drop created item codes.
    private static ItemCatalogStore LoadItemCatalog()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.TechnicalName, "item-catalog.json");
        try { return new ItemCatalogStore(path); }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
            return new ItemCatalogStore(path);
        }
    }

    private async Task InitializeRuntimeAsync()
    {
        var appVersion = typeof(AppServices).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        Generations.Retain(maximumGenerations: 2, minimumAge: TimeSpan.FromDays(30),
            hasUnresolvedRun: Generations.HasUnresolvedRun);
        await Workflow.InitializeAsync();
        await Task.Run(() => eventStore.Prune());
    }

    private async Task<OperationResult> DispatchAsync(ControlRequest request, CancellationToken token)
    {
        ContractValidation.Validate(request);
        request = request with { Action = ControlActions.Canonical(request.Action) };
        token.ThrowIfCancellationRequested();
        if (request.Action is not ("capabilities" or "diagnostics" or "runtime_status" or "live_test_state" or "failure_context" or "observe" or "inspect" or "export_tab" or "native_read" or "get_operation_status" or "verification_history"))
        {
            try { await Initialization.WaitAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { return new(request.RequestId, "blocked", "Không khôi phục được state: " + error.Message); }
        }
        if (request.Action is "advance" or "resume" or "save" or "discard" or "reconcile" or "recover")
        {
            var current = Workflow.Current;
            if (current is null || request.ExpectedRevision != current.Invoice.Fingerprint)
                return new(request.RequestId, "blocked", "Expected revision không khớp hóa đơn trong workflow.");
        }
        if (request.Action is "get_operation_status" or "capabilities")
            return await DispatchCoreAsync(request, token);
        // Agent reads are not operations: no registry entry, no serial gate.
        if (ControlActions.IsAgentAction(request.Action))
            return await DispatchAgentAsync(request, token);
        var operationId = ReadOperationId(request);
        using var correlation = OperationContext.Begin(request.RequestId, operationId);
        var started = new OperationStatusSnapshot(operationId, request.RequestId, request.SessionId,
            request.Action, "running", DateTimeOffset.UtcNow, null, "Operation đang chạy.", false);
        if (!operations.TryAdd(operationId, started))
            return new(request.RequestId, "blocked", $"Operation '{operationId}' đang tồn tại; không chạy lặp.",
                Data: operations[operationId]);

        var coreStarted = false;
        try
        {
            var serial = request.Action is "start_workflow" or "startWorkflow" or "run_workflow" or "live_run" or "advance" or "resume" or "save" or "discard" or "focus" or "reconcile" or "recover" or "correct_invoice" or "begin_repair" or "complete_repair" or "acknowledge_restart" or "rebind_after_restart";
            if (serial) await directGate.WaitAsync(token);
            OperationResult result;
            try
            {
                token.ThrowIfCancellationRequested();
                if (request.Deadline <= DateTimeOffset.UtcNow)
                    result = new(request.RequestId, "expired", "Lệnh đã hết hạn trước khi thực thi.");
                else
                {
                    coreStarted = true;
                    result = await DispatchCoreAsync(request, token);
                }
            }
            finally { if (serial) directGate.Release(); }
            CompleteOperation(operationId, result.Status, result.Message, result.InputMayHaveBeenSent);
            return result with { Data = result.Data ?? operations[operationId] };
        }
        catch (OperationCanceledException)
        {
            var uncertain = coreStarted && ControlActions.MayMutate(request.Action);
            var mayInput = coreStarted && ControlActions.MaySendInput(request.Action);
            var status = uncertain ? "uncertain" : request.Deadline <= DateTimeOffset.UtcNow ? "expired" : "cancelled";
            var message = uncertain ? "Cancellation interrupted a mutation; inspect its state before retrying."
                : status == "expired" ? "Operation đã hết deadline trước khi hoàn tất." : "Operation đã bị hủy.";
            CompleteOperation(operationId, status, message, mayInput);
            return new(request.RequestId, status, message, mayInput, operations[operationId]);
        }
        catch (AutomationException error)
        {
            var status = error.InputMayHaveBeenSent ? "uncertain" : "blocked";
            CompleteOperation(operationId, status, error.Message, error.InputMayHaveBeenSent);
            return new(request.RequestId, status, error.Message, error.InputMayHaveBeenSent, operations[operationId]);
        }
        catch (Exception error)
        {
            var status = coreStarted && ControlActions.MayMutate(request.Action) ? "uncertain" : "error";
            var mayInput = coreStarted && ControlActions.MaySendInput(request.Action);
            CompleteOperation(operationId, status, error.Message, mayInput);
            return new(request.RequestId, status, error.Message, mayInput, operations[operationId]);
        }
    }

    private async Task<OperationResult> DispatchCoreAsync(ControlRequest request, CancellationToken token)
    {
        switch (request.Action)
        {
            case "capabilities":
                return new(request.RequestId, "completed", "MisaManager control connected.", Data: new CapabilitySnapshot(
                    typeof(AppServices).Assembly.GetName().Version?.ToString() ?? "0.0.0", ProductIdentity.ControlProtocolVersion,
                    ProductIdentity.LocatorProfileVersion, WorkflowProfile.Status, [.. ControlActions.AgentActions, "observe", "inspect", "export_tab", "diagnostics", "live_test_state", "failure_context", "focus", "native_read", "native_set", "native_invoke", "native_key", "click", "type", "pressKey", "scroll", "selectTab", "startWorkflow", "runWorkflow", "liveRun", "getOperationStatus", "start_workflow", "run_workflow", "live_run", "verification_history", "advance", "pause", "resume", "cancel", "save", "discard", "reconcile", "workflow_status", "get_operation_status", "generation_status", "correct_invoice", "recover", "begin_repair", "complete_repair", "acknowledge_restart", "rebind_after_restart", "reload"],
                    BlockedReason: MisaProfile.Capability.BlockedReason,
                    SourceRevision: ProductIdentity.SourceRevision, StateSchemaVersion: ProductIdentity.StateSchemaVersion,
                    ProfileRevision: WorkflowProfile.Revision, CoreAssemblySha256: ProductIdentity.RuntimeAssemblySha256));
            case "diagnostics":
                var diagnosticsRoot = eventStore.RootPath;
                return new(request.RequestId, "completed", "Diagnostics collected.", Data: new
                {
                    native = await Native.DiagnosticsAsync(token),
                    profile = new { MisaProfile.Id, MisaProfile.Revision, MisaProfile.Status, capability = MisaProfile.Capability },
                    workflow = Workflow.Current?.Snapshot,
                    runtime = new RuntimeStateIdentity(ProductIdentity.RuntimeVersion, ProductIdentity.SourceRevision, WorkflowProfile.Revision),
                    retention = DiagnosticsRetention.Inspect(diagnosticsRoot, eventStore.IsProtectedFile)
                });
            case "live_test_state":
                return await ReadLiveStateAsync(request, token);
            case "failure_context":
                return await BuildFailureContextAsync(request, token);
            case "runtime_status":
                return new(request.RequestId, "completed", "Runtime identity and health read without native input.", Data: RuntimeStatus());
            case "focus":
                if (!request.Payload.TryGetProperty("windowHandle", out var focusHandleValue) || !focusHandleValue.TryGetInt64(out var focusHandle) || focusHandle == 0)
                    return new(request.RequestId, "blocked", "Cần windowHandle từ diagnostics hiện tại.");
                try
                {
                    var focused = Windows.Focus((nint)focusHandle);
                    return new(request.RequestId, "completed", "Đã đưa đúng cửa sổ MISA lên trước; chưa gửi nghiệp vụ.", false, focused);
                }
                catch (Exception error) { return new(request.RequestId, "blocked", error.Message); }
            case "observe":
            case "inspect":
                if (!request.Payload.TryGetProperty("windowHandle", out var handleValue)
                    || !handleValue.TryGetInt64(out var handle) || handle == 0)
                    return new(request.RequestId, "blocked", "Cần windowHandle từ diagnostics hiện tại; chưa gửi input.");
                var query = request.Payload.TryGetProperty("query", out var queryValue) ? queryValue.GetString() : null;
                var scope = request.Payload.TryGetProperty("scopeAutomationId", out var scopeValue) ? scopeValue.GetString() : null;
                try
                {
                    var observed = await Native.ObserveAsync((nint)handle, query, token, scope);
                    return new(request.RequestId, "completed", "Đã quan sát native UIA3/Win32; chưa gửi input.", Data: observed);
                }
                catch (Exception error)
                {
                    return new(request.RequestId, "blocked", "Không quan sát được đúng phiên MISA: " + error.Message);
                }
            case "export_tab":
                if (!request.Payload.TryGetProperty("windowHandle", out var exportHandleValue)
                    || !exportHandleValue.TryGetInt64(out var exportHandle) || exportHandle == 0)
                    return new(request.RequestId, "blocked", "Cần windowHandle từ diagnostics hiện tại; chưa gửi input.");
                if (!request.Payload.TryGetProperty("scopeAutomationId", out var exportScopeValue)
                    || exportScopeValue.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(exportScopeValue.GetString()))
                    return new(request.RequestId, "blocked", "Cần scopeAutomationId của tab MISA; chưa gửi input.");
                try
                {
                    var exported = await Native.ExportTabAsync((nint)exportHandle, exportScopeValue.GetString()!, token);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(exported, ContractJson.Compact);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                    var path = Path.Combine(eventStore.RootPath, "native-tab-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + hash[..8] + ".json");
                    StateFiles.WriteAtomic(path, new
                    {
                        documentType = "MisaManager.NativeTabExport",
                        schemaVersion = 1,
                        createdAt = DateTimeOffset.UtcNow,
                        sha256 = hash,
                        evidence = "fresh_native_observation",
                        export = exported
                    });
                    return new(request.RequestId, "completed", "Đã export cây UIA3 của tab MISA; chưa gửi input.", Data: new
                    {
                        snapshotPath = path,
                        snapshotSha256 = hash,
                        scopeAutomationId = exported.ScopeAutomationId,
                        frame = exported.Observation.Frame,
                        elementCount = exported.Observation.Elements.Count,
                        visitedCount = exported.Observation.VisitedCount,
                        truncated = exported.Observation.Truncated,
                        complete = exported.Complete,
                        completenessReason = exported.CompletenessReason,
                        inputSent = false
                    });
                }
                catch (Exception error)
                {
                    return new(request.RequestId, "blocked", "Không export được cây UIA3 của tab MISA: " + error.Message);
                }
            case "workflow_status":
                return new(request.RequestId, "completed", "Workflow status read.", Data: Workflow.Current?.Snapshot);
            case "get_operation_status":
            case "getOperationStatus":
                return GetOperationStatus(request);
            case "verification_history":
                return new(request.RequestId, "completed", "Live verification history read.",
                    Data: await Workflow.ReadLiveVerificationHistoryAsync(token));
            case "generation_status":
                return new(request.RequestId, "completed", "Generation pointer read.", Data: Generations.ReadActive());
            case "reload":
                return ScheduleReload(request);
            case "begin_repair":
                return await BeginRepairAsync(request, token);
            case "complete_repair":
                var callers = request.Payload.GetProperty("affected_callers").Deserialize<string[]>(ContractJson.Options)
                    ?? throw new ArgumentException("affected_callers phải là danh sách.");
                var handoff = await Repairs.CompleteAsync(RequiredText(request, "incident_id"), request.SessionId,
                    RequiredText(request, "release_directory"), RequiredText(request, "root_cause"), callers,
                    RequiredText(request, "message"), token);
                return new(request.RequestId, "completed", "Source handoff đã kiểm hash; cần restart candidate rồi rebind/readback, chưa gửi input.", Data: handoff);
            case "acknowledge_restart":
                var acknowledgement = await Repairs.AcknowledgeRestartAsync(RequiredText(request, "incident_id"),
                    request.SessionId, RequiredText(request, "release_directory"), RequiredText(request, "window_identity"), token);
                return new(request.RequestId, "completed", "Candidate restart acknowledged by version/hash/window identity; no resume or input was sent.", Data: acknowledgement);
            case "rebind_after_restart":
                var incidentId = RequiredText(request, "incident_id");
                await Repairs.ReadRestartAcknowledgementAsync(incidentId, request.SessionId, token);
                var reboundCurrent = Workflow.Current;
                if (reboundCurrent is null || reboundCurrent.Snapshot.RunId != incidentId)
                    return new(request.RequestId, "blocked", "Rebind incident không khớp workflow hiện tại.");
                if (request.ExpectedRevision is { } reboundRevision
                    && reboundRevision != reboundCurrent.Invoice.Fingerprint)
                    return new(request.RequestId, "blocked", "Expected revision không khớp hóa đơn trong workflow.");
                var expected = request.Payload.TryGetProperty("expectedStatus", out var expectedValue)
                    ? expectedValue.Deserialize<WorkflowStatus>(ContractJson.Options)
                    : throw new ArgumentException("expectedStatus is required.");
                var restartFromBeginning = request.Payload.TryGetProperty("restartFromBeginning", out var restartValue)
                    ? restartValue.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                        ? restartValue.GetBoolean()
                        : throw new ArgumentException("restartFromBeginning must be a boolean.")
                    : false;
                if (restartFromBeginning)
                {
                    if (reboundCurrent.Snapshot.Status is not (WorkflowStatus.Failed or WorkflowStatus.Blocked
                        or WorkflowStatus.InputUncertain))
                        return new(request.RequestId, "blocked", "Chỉ chạy lại live từ đầu với một attempt lỗi chưa Cất.");
                    var rerun = await Workflow.RunLiveAsync(reboundCurrent.Invoice,
                        reboundCurrent.CompanyIdentity ?? "", RequiredText(request, "window_identity"), token,
                        liveValidation: true, profileRevision: WorkflowProfile.Revision,
                        allowKgToTon: reboundCurrent.AllowKgToTon, requestId: request.RequestId,
                        operationId: ReadOperationId(request), executionMode: reboundCurrent.ExecutionMode);
                    return new(request.RequestId, rerun.Outcome is "saved" or "discarded" or "tested" ? "completed" : "blocked",
                        "Đã chạy lại cùng hóa đơn từ Ready; không tiếp tục checkpoint cũ.",
                        rerun.Context.Snapshot.InputMayHaveBeenSent, Data: new
                        {
                            outcome = rerun.Outcome,
                            retryFromStart = rerun.RetryFromStart,
                            continuationAllowed = false,
                            runId = rerun.Context.Snapshot.RunId,
                            previousIncidentId = incidentId,
                            checkpoint = rerun.Context.Snapshot.Status,
                            verification = rerun.Verification,
                            error = rerun.Error ?? rerun.Context.Snapshot.ErrorChain
                        });
                }
                if (reboundCurrent.LiveMode)
                    return new(request.RequestId, "blocked",
                        "Live run lỗi chỉ được chạy lại từ Ready với restartFromBeginning=true; không được tiếp tục checkpoint.");
                var rebound = await Workflow.RebindAfterRestartAsync(RequiredText(request, "window_identity"), expected, token);
                var continueLiveTest = request.Payload.TryGetProperty("continueLiveTest", out var continueValue)
                    ? continueValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? continueValue.GetBoolean()
                        : throw new ArgumentException("continueLiveTest must be a boolean.")
                    : false;
                if (!continueLiveTest)
                    return new(request.RequestId, "completed", "Candidate rebound and checkpoint read back; no input was replayed.", Data: rebound.Snapshot);

                var target = ReadBatchCheckpoint(request.Payload, "until", WorkflowStatus.AwaitingSave);
                var maxSteps = 20;
                if (request.Payload.TryGetProperty("maxSteps", out var maxValue)
                    && (maxValue.ValueKind != JsonValueKind.Number || !maxValue.TryGetInt32(out maxSteps)))
                    throw new ArgumentException("maxSteps must be an integer.");
                if (maxSteps is < 1 or > 20) throw new ArgumentException("maxSteps must be between 1 and 20.");
                var continuation = await Workflow.RunUntilAsync(rebound.Snapshot.RunId, rebound.Invoice.Fingerprint,
                    expected, target, maxSteps, token);
                var continuationSnapshot = continuation.Context.Snapshot;
                var continuationStatus = continuation.InputDisposition is InputDisposition.Uncertain or InputDisposition.SentUnverified
                    ? "uncertain" : continuation.StopReason == "target_reached" ? "completed"
                    : continuation.StopReason == "step_limit" ? "accepted"
                    : continuation.StopReason == "interrupted" ? "cancelled" : "blocked";
                return new(request.RequestId, continuationStatus,
                    $"Candidate rebound at {expected}; live test continued to {continuationSnapshot.Status}: {continuation.StopReason}.",
                    continuation.InputMayHaveBeenSent, Data: new
                    {
                        rebound = rebound.Snapshot,
                        runId = continuationSnapshot.RunId,
                        expectedRevision = continuation.Context.Invoice.Fingerprint,
                        checkpoint = continuationSnapshot.Status,
                        target,
                        continuation.AttemptedSteps,
                        continuation.CompletedSteps,
                        continuation.StopReason,
                        continuation.InputDisposition,
                        canContinue = continuation.StopReason == "step_limit",
                        continuationSnapshot.ActiveLine,
                        continuationSnapshot.UpdatedAt,
                        continuationSnapshot.LastErrorCode,
                        continuationSnapshot.LastMessage,
                        continuationSnapshot.RuntimeIdentity,
                        error = continuation.Error ?? continuationSnapshot.ErrorChain,
                repairRequired = continuationSnapshot.Status is WorkflowStatus.Blocked or WorkflowStatus.Failed,
                repairIncidentId = continuationSnapshot.Status is WorkflowStatus.Blocked or WorkflowStatus.Failed
                    ? continuationSnapshot.RunId : null,
                        evidence = continuation.CompletedSteps > 0 ? "guarded_checkpoint_readback" : "saved_checkpoint_only"
                    });
            case "start_workflow":
            case "startWorkflow":
                return await StartWorkflowAsync(request, token);
            case "run_workflow":
                return await RunWorkflowAsync(request, token);
            case "live_run":
                return await RunLiveWorkflowAsync(request, token);
            case "advance":
                return await AdvanceWorkflowAsync(request, token);
            case "pause":
                return await PauseWorkflowAsync(request, token);
            case "resume":
                return await ResumeWorkflowAsync(request, token);
            case "save":
            case "discard":
                if (!request.Payload.TryGetProperty("confirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
                    return new(request.RequestId, "blocked", "Cất/Bỏ cần xác nhận explicit cho chứng từ hiện tại.");
                var resolveSaveUncertain = request.Payload.TryGetProperty("resolveSaveUncertain", out var resolve)
                    && resolve.ValueKind == JsonValueKind.True;
                var finished = await Workflow.FinishAsync(request.Action == "save", resolveSaveUncertain, token);
                return new(request.RequestId, finished.Snapshot.Status is WorkflowStatus.SaveUncertain or WorkflowStatus.InputUncertain ? "uncertain"
                    : finished.Snapshot.Status == WorkflowStatus.Blocked ? "blocked" : "completed", finished.Snapshot.LastMessage ?? "Đã xử lý chứng từ.",
                    finished.Snapshot.InputMayHaveBeenSent, finished.Snapshot);
            case "reconcile":
                var comparison = await Workflow.ReconcileAsync(token);
                return new(request.RequestId, comparison.Matched ? "completed" : "blocked", comparison.Matched
                    ? "Đã đối chiếu khách hàng, ngày, từng dòng và tổng MISA với nguồn." : "Dữ liệu MISA có sai khác với nguồn.", Data: comparison);
            case "recover":
                if (!request.Payload.TryGetProperty("windowIdentity", out var recoveryWindow)
                    || !request.Payload.TryGetProperty("expectedStatus", out var recoveryStatus))
                    return new(request.RequestId, "blocked", "Cần windowIdentity mới và expectedStatus để khôi phục.");
                var recovered = await Workflow.RecoverAsync(recoveryWindow.GetString() ?? "",
                    recoveryStatus.Deserialize<WorkflowStatus>(ContractJson.Options), token);
                return new(request.RequestId, "completed", recovered.Snapshot.LastMessage ?? "Đã khôi phục.", Data: recovered.Snapshot);
            case "native_read":
            case "native_set":
            case "native_invoke":
            case "native_key":
            case "selectTab":
            case "scroll":
                return await DirectAsync(request, token);
            case "correct_invoice":
                return await CorrectInvoiceAsync(request, token);
            case "edit_xml":
                return new(request.RequestId, "blocked", "edit_xml đã bị loại khỏi giao diện mới; dùng typed XLSX correction hoặc import lại workbook.");
            case "cancel":
                if (request.Payload.TryGetProperty("resetWorkflow", out var resetValue)
                    && resetValue.ValueKind == JsonValueKind.True)
                {
                    try
                    {
                        if (!request.Payload.TryGetProperty("confirmed", out var resetConfirmed)
                            || resetConfirmed.ValueKind != JsonValueKind.True)
                            return new(request.RequestId, "blocked", "resetWorkflow cần confirmed=true.");
                        if (!request.Payload.TryGetProperty("expectedRevision", out var revisionValue)
                            || revisionValue.ValueKind != JsonValueKind.String)
                            return new(request.RequestId, "blocked", "resetWorkflow cần expectedRevision.");
                        if (!request.Payload.TryGetProperty("windowHandle", out var resetHandleValue)
                            || !resetHandleValue.TryGetInt64(out var resetHandle) || resetHandle == 0)
                            return new(request.RequestId, "blocked", "resetWorkflow cần windowHandle từ fresh diagnostics.");
                        var current = Workflow.Current;
                        if (current is null || current.Invoice.Fingerprint != revisionValue.GetString())
                            return new(request.RequestId, "blocked", "Expected revision không khớp hóa đơn trong workflow.");
                        var observed = await Native.ObserveAsync((nint)resetHandle, null, token, WorkflowProfile.EditorId);
                        if (observed.Frame.Window.WindowHandle != (nint)resetHandle)
                            return new(request.RequestId, "blocked", "Fresh native observation không cùng HWND.");
                        if (!MatchesWindow(current.WindowIdentity, observed.Frame.Window))
                            return new(request.RequestId, "blocked", "Reset requires fresh observation of the workflow's bound MISA process and window.");
                        if (observed.Elements.Count != 0)
                            return new(request.RequestId, "blocked", "Voucher editor vẫn tồn tại; reset bị từ chối.", Data: observed);
                        if (observed.Truncated)
                            return new(request.RequestId, "blocked", "Quan sát chưa đầy đủ; chưa thể xác nhận editor đã đóng để reset.", Data: observed);
                        var reset = await Workflow.ResetUncertainAsync(revisionValue.GetString()!,
                            current.WindowIdentity, token);
                        return new(request.RequestId, "completed", "Workflow reset sau fresh readback; không lưu/bỏ voucher MISA.", Data: reset.Snapshot);
                    }
                    catch (Exception error) { return new(request.RequestId, "blocked", error.Message); }
                }
                try
                {
                    await Workflow.CancelAsync(token);
                    return new(request.RequestId, "completed", "Workflow cancelled; MISA voucher was not saved or discarded.");
                }
                catch (Exception error) { return new(request.RequestId, "blocked", error.Message); }
            default:
                return new(request.RequestId, "blocked", "Command chưa được nối vào workflow native được nghiệm thu: " + request.Action);
        }
    }

    private async Task<OperationResult> DirectAsync(ControlRequest request, CancellationToken token)
    {
        var guards = new List<string> { "request-contract" };
        var destination = "unresolved";
        NativeFrame? frame = null;
        var mayInput = false;
        var locked = false;
        try
        {
            var target = ContractValidation.ReadDirectPayload(request, request.Action == "native_set", request.Action == "native_key");
            destination = $"hwnd={target.WindowHandle};scope={target.ScopeAutomationId};id={target.AutomationId};name={target.Name}";
            await directGate.WaitAsync(token); locked = true;
            token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= request.Deadline) throw new OperationCanceledException("Request expired before native dispatch.", token);
            var window = Windows.Bind((nint)target.WindowHandle);
            guards.Add("current-window-identity");
            frame = await Input.BindAsync(window, token, target.ScopeAutomationId);
            guards.Add("native-scope");
            mayInput = request.Action != "native_read";
            Readback readback = request.Action switch
            {
                "native_read" => await Input.ReadAsync(frame, target.AutomationId ?? "", target.Name ?? "", token),
                "native_set" => await Input.SetValueAsync(frame, target.AutomationId ?? "", target.Name ?? "", target.Value!, token),
                "native_invoke" => await Input.InvokeAsync(frame, target.AutomationId ?? "", target.Name ?? "", token),
                "native_key" => await Input.PressKeyAsync(frame, target.AutomationId ?? "", target.Name ?? "", target.Key!, token),
                "selectTab" => await Input.SelectDirectTabAsync(frame, target.Name ?? throw new InvalidDataException("Tab caption is required."), token),
                "scroll" => await Input.ScrollAsync(frame, target.AutomationId ?? "", target.Name ?? "", token),
                _ => throw new InvalidDataException("Unsupported direct action.")
            };
            guards.Add("target-uniqueness");
            if (request.Action == "native_key") guards.Add("native-key-focus");
            var verified = readback.Status == "supported";
            if (verified) guards.Add("fresh-native-readback");
            return new(request.RequestId, verified ? OperationStatuses.Completed : OperationStatuses.Uncertain,
                verified ? "Đã đọc lại native target." : "Input đã gửi; cần quan sát kết quả nghiệp vụ mới.", mayInput,
                new DirectControlResult("direct_control", destination,
                    !mayInput ? InputDisposition.NotSent : verified ? InputDisposition.Verified : InputDisposition.SentUnverified,
                    mayInput, readback, readback.Reason, guards.ToArray(), DirectBypassedGuards));
        }
        catch (Exception error)
        {
            var sent = error is AutomationException automation ? automation.InputMayHaveBeenSent : mayInput;
            var status = sent ? OperationStatuses.Uncertain : error is OperationCanceledException
                ? request.Deadline <= DateTimeOffset.UtcNow ? OperationStatuses.Expired : OperationStatuses.Cancelled : OperationStatuses.Blocked;
            NativeFrame? fresh = null;
            var reason = error.Message;
            if (frame is not null)
            {
                try { fresh = await Input.CaptureAsync(CancellationToken.None); }
                catch (Exception readError) { reason += " Readback unavailable: " + readError.Message; }
            }
            return new(request.RequestId, status, error.Message, sent,
                new DirectControlResult("direct_control", destination, sent ? InputDisposition.Uncertain : InputDisposition.NotSent,
                    sent, fresh, reason, guards.ToArray(), DirectBypassedGuards,
                    (error as AutomationException)?.Code ?? error.GetType().Name, error.ToString()));
        }
        finally { if (locked) directGate.Release(); }
    }

    private static readonly string[] DirectBypassedGuards = ["workflow-checkpoint", "expected-invoice-revision", "business-readback", "profile", "dpi-bounds-age"];

    private async Task<OperationResult> BeginRepairAsync(ControlRequest request, CancellationToken token)
    {
        var baseline = await Repairs.BeginAsync(RequiredText(request, "incident_id"), request.SessionId,
            RequiredText(request, "source_root"), token);
        var current = Workflow.Current ?? throw new InvalidOperationException("Không có workflow hiện tại.");
        NativeObservation? fresh = null;
        string? observationError = null;
        try
        {
            var identity = current.WindowIdentity.Split(';').Select(part => part.Split('=', 2))
                .Where(part => part.Length == 2).ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
            var handle = long.Parse(identity["hwnd"], System.Globalization.CultureInfo.InvariantCulture);
            var pid = int.Parse(identity["pid"], System.Globalization.CultureInfo.InvariantCulture);
            var window = Windows.Bind((nint)handle, pid);
            var start = DateTimeOffset.Parse(identity["start"], System.Globalization.CultureInfo.InvariantCulture);
            if (window.ProcessStartUtc != start.UtcDateTime
                || !string.Equals(window.ExecutablePath, identity["exe"], StringComparison.OrdinalIgnoreCase))
                throw new AutomationException("WINDOW_IDENTITY_CHANGED", "Process MISA đã đổi; chưa rebind.");
            fresh = await Native.ObserveAsync(window.WindowHandle, null, token, WorkflowProfile.EditorId);
        }
        catch (Exception error) when (error is AutomationException or ArgumentException or FormatException
            or KeyNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { observationError = error.Message; }
        return new(request.RequestId, "completed", "Incident đã bind; source snapshot do app tạo. Chưa gửi input MISA.",
            Data: new { baseline, checkpoint = current.Snapshot, invoiceIdentity = current.Invoice.Identity,
                invoiceFingerprint = current.Invoice.Fingerprint, source = current.Invoice.Source,
                nativeValues = current.NativeValues, freshObservation = fresh, observationError,
                route = "APP_WORKFLOW.md; RECOVERY.md; src/MisaManager.Core/Workflow.cs; src/MisaManager.Automation/NativeWorkflowGateway.cs",
                plan = "Repair source, stage a checked release, complete_repair, restart candidate, acknowledge it, then rebind with restartFromBeginning=true so the same invoice runs from Ready; never resume a failed checkpoint." });
    }

    private static string RequiredText(ControlRequest request, string name)
        => request.Payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!
                : throw new ArgumentException("Thiếu trường " + name);

    private OperationResult GetOperationStatus(ControlRequest request)
    {
        if (!request.Payload.TryGetProperty("operationId", out var operationValue)
            || operationValue.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(operationValue.GetString()))
            return new(request.RequestId, "blocked", "get_operation_status cần operationId.");
        var operationId = operationValue.GetString()!;
        return operations.TryGetValue(operationId, out var status)
            ? new(request.RequestId, status.Status, status.Message, status.InputMayHaveBeenSent, status)
            : new(request.RequestId, "blocked", $"Không tìm thấy operation '{operationId}'.");
    }

    private static string ReadOperationId(ControlRequest request)
        => request.Payload.TryGetProperty("operationId", out var operationValue)
            && operationValue.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(operationValue.GetString())
            ? operationValue.GetString()!
            : request.RequestId;

    private void CompleteOperation(string operationId, string status, string message, bool inputMayHaveBeenSent)
    {
        operations.AddOrUpdate(operationId,
            _ => throw new InvalidOperationException($"Operation '{operationId}' was not registered."),
            (_, previous) => previous with
            {
                Status = status,
                CompletedAt = DateTimeOffset.UtcNow,
                Message = message,
                InputMayHaveBeenSent = inputMayHaveBeenSent
            });
    }

    public async ValueTask DisposeAsync()
    {
        await Control.StopAsync();
        await Native.DisposeAsync();
        await Input.DisposeAsync();
        nativeOwner.Dispose();
    }

    private async Task<OperationResult> StartWorkflowAsync(ControlRequest request, CancellationToken token)
    {
        if (!request.Payload.TryGetProperty("invoice", out var invoiceValue))
            return new(request.RequestId, "blocked", "start_workflow cần invoice typed và không được tự suy đoán dữ liệu.");
        try
        {
            var invoice = invoiceValue.Deserialize<InvoiceDocument>(ContractJson.Options)
                ?? throw new InvalidOperationException("invoice payload rỗng.");
            var company = request.Payload.TryGetProperty("companyIdentity", out var companyValue)
                ? companyValue.GetString() ?? "" : "";
            var window = request.Payload.TryGetProperty("windowIdentity", out var windowValue)
                ? windowValue.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(window))
                return new(request.RequestId, "blocked", "Thiếu windowIdentity: cần chọn phiên MISA hiện tại từ diagnostics.");
            var liveValidation = request.Payload.TryGetProperty("liveValidation", out var validation)
                && validation.ValueKind == JsonValueKind.True;
            var executionMode = ReadExecutionMode(request.Payload, required: false);
            var allowKgToTon = false;
            if (request.Payload.TryGetProperty("allowKgToTon", out var conversion))
            {
                if (conversion.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return new(request.RequestId, "blocked", "allowKgToTon phải là boolean.");
                allowKgToTon = conversion.GetBoolean();
            }
            var started = await Workflow.StartAsync(invoice, company, window, token, liveValidation, WorkflowProfile.Revision,
                request.RequestId, ReadOperationId(request), allowKgToTon,
                executionMode: executionMode);
            return new(request.RequestId, "accepted", "Workflow đã tạo checkpoint Ready; chưa gửi input MISA.", Data: started.Snapshot);
        }
        catch (Exception error)
        {
            // Preserve the full exception chain so malformed typed invoices do not
            // collapse into an un-actionable NullReferenceException message.
            return new(request.RequestId, "blocked", error.Message + "\n" + error);
        }
    }

    private static OperationResult ScheduleReload(ControlRequest request)
    {
        var executable = Environment.ProcessPath;
        if (request.Payload.TryGetProperty("release_directory", out var releaseValue)
            && releaseValue.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(releaseValue.GetString()))
        {
            var release = Path.GetFullPath(releaseValue.GetString()!);
            if (!File.Exists(Path.Combine(release, "release-manifest.json")))
                return new(request.RequestId, "blocked", "Release candidate thiếu release-manifest.json.");
            var candidate = Path.Combine(release, "payload", "app", "MisaManager.App.exe");
            if (!File.Exists(candidate))
                return new(request.RequestId, "blocked", "Release candidate không có payload/app/MisaManager.App.exe.");
            executable = candidate;
        }
        if (string.IsNullOrWhiteSpace(executable))
            return new(request.RequestId, "blocked", "Không xác định được executable hiện tại để reload.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory };
        start.ArgumentList.Add("--wait-for-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process.Start(start);
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            new Action(() => System.Windows.Application.Current.Shutdown()),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        return new(request.RequestId, "accepted", "Đã lên lịch restart candidate; state được giữ lại và app mới tự chờ process cũ thoát.");
    }

    private async Task<OperationResult> AdvanceWorkflowAsync(ControlRequest request, CancellationToken token)
    {
        try
        {
            var next = await Workflow.AdvanceAsync(token);
            return new(request.RequestId, next.Snapshot.Status is WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain ? "uncertain"
                    : next.Snapshot.Status == WorkflowStatus.Blocked ? "blocked" : "completed",
                next.Snapshot.LastMessage ?? "Checkpoint đã được readback.", next.Snapshot.InputMayHaveBeenSent, next.Snapshot);
        }
        catch (Exception error) { return new(request.RequestId, "blocked", error.Message); }
    }

    private async Task<OperationResult> RunWorkflowAsync(ControlRequest request, CancellationToken token)
    {
        WorkflowStatus target;
        WorkflowStatus expectedCheckpoint;
        string revision;
        string? runId;
        int maxSteps;
        var allowKgToTon = false;
        var start = request.Payload.TryGetProperty("invoice", out var invoiceValue);
        try
        {
            revision = request.ExpectedRevision ?? RequiredText(request, "expectedRevision");
            if (string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("expectedRevision is required.");
            if (request.Payload.TryGetProperty("expectedRevision", out var suppliedRevision)
                && (suppliedRevision.ValueKind != JsonValueKind.String || suppliedRevision.GetString() != revision))
                throw new ArgumentException("Conflicting invoice revisions.");
            target = ReadBatchCheckpoint(request.Payload, "until", WorkflowStatus.AwaitingSave);
            maxSteps = 20;
            if (request.Payload.TryGetProperty("maxSteps", out var maxValue)
                && (maxValue.ValueKind != JsonValueKind.Number || !maxValue.TryGetInt32(out maxSteps)))
                throw new ArgumentException("maxSteps must be an integer.");
            if (maxSteps is < 1 or > 20) throw new ArgumentException("maxSteps must be between 1 and 20.");
            if (request.Payload.TryGetProperty("allowKgToTon", out var conversion))
            {
                if (conversion.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("allowKgToTon must be a boolean.");
                allowKgToTon = conversion.GetBoolean();
            }
            if (start)
            {
                if (request.Payload.TryGetProperty("runId", out _) || request.Payload.TryGetProperty("expectedCheckpoint", out _))
                    throw new ArgumentException("Supply either a new invoice or an existing run, not both.");
                var invoice = invoiceValue.Deserialize<InvoiceDocument>(ContractJson.Options)
                    ?? throw new ArgumentException("invoice is required.");
                if (invoice.Fingerprint != revision) throw new ArgumentException("Invoice fingerprint differs from expectedRevision.");
                if (request.Payload.TryGetProperty("liveValidation", out var live)
                    && live.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("liveValidation must be a boolean.");
                runId = null;
                expectedCheckpoint = WorkflowStatus.Ready;
            }
            else
            {
                runId = RequiredText(request, "runId");
                expectedCheckpoint = ReadBatchCheckpoint(request.Payload, "expectedCheckpoint");
                if (request.Payload.TryGetProperty("companyIdentity", out _) || request.Payload.TryGetProperty("windowIdentity", out _)
                    || request.Payload.TryGetProperty("liveValidation", out _))
                    throw new ArgumentException("An existing run retains its bound target and live-validation policy.");
            }
            if (target < expectedCheckpoint) throw new ArgumentException("until must not precede expectedCheckpoint.");
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
        {
            return new(request.RequestId, "blocked", error.Message);
        }

        var workflowStarted = Stopwatch.GetTimestamp();
        if (start)
        {
            var started = await StartWorkflowAsync(request, token);
            if (started.Status != "accepted") return started;
            runId = ((WorkflowSnapshot)started.Data!).RunId;
        }
        WorkflowBatchResult result;
        try
        {
            result = await Workflow.RunUntilAsync(runId!, revision, expectedCheckpoint, target, maxSteps, token, allowKgToTon);
        }
        finally
        {
            workflowGateway.RecordWorkflowDuration(workflowStarted);
        }
        var snapshot = result.Context.Snapshot;
        var status = result.InputDisposition is InputDisposition.Uncertain or InputDisposition.SentUnverified
            ? "uncertain" : result.StopReason == "target_reached" ? "completed"
            : result.StopReason == "step_limit" ? "accepted"
            : result.StopReason == "interrupted" ? "cancelled" : "blocked";
        return new(request.RequestId, status, $"Workflow stopped at {snapshot.Status}: {result.StopReason}.",
            result.InputMayHaveBeenSent, new
            {
                operationId = ReadOperationId(request),
                runId = snapshot.RunId,
                expectedRevision = result.Context.Invoice.Fingerprint,
                checkpoint = snapshot.Status,
                target,
                result.AttemptedSteps,
                result.CompletedSteps,
                result.StopReason,
                result.InputDisposition,
                canContinue = result.StopReason == "step_limit",
                snapshot.ActiveLine,
                snapshot.UpdatedAt,
                snapshot.LastErrorCode,
                snapshot.LastMessage,
                snapshot.RuntimeIdentity,
                error = result.Error ?? snapshot.ErrorChain,
                repairRequired = snapshot.Status is WorkflowStatus.Blocked or WorkflowStatus.Failed,
                repairIncidentId = snapshot.Status is WorkflowStatus.Blocked or WorkflowStatus.Failed
                    ? snapshot.RunId : null,
                evidence = result.CompletedSteps > 0 ? "guarded_checkpoint_readback" : "saved_checkpoint_only"
            });
    }

    private async Task<OperationResult> RunLiveWorkflowAsync(ControlRequest request, CancellationToken token)
    {
        try
        {
            if (!request.Payload.TryGetProperty("invoice", out var invoiceValue))
                return new(request.RequestId, "blocked", "live_run cần invoice typed; không được tự suy đoán dữ liệu.");
            var invoice = invoiceValue.Deserialize<InvoiceDocument>(ContractJson.Options)
                ?? throw new InvalidDataException("invoice payload rỗng.");
            var executionMode = ReadExecutionMode(request.Payload, required: true);
            var company = request.Payload.TryGetProperty("companyIdentity", out var companyValue)
                ? companyValue.GetString() ?? "" : "";
            var window = request.Payload.TryGetProperty("windowIdentity", out var windowValue)
                ? windowValue.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(window))
                return new(request.RequestId, "blocked", "Thiếu windowIdentity: cần chọn phiên MISA hiện tại từ diagnostics.");
            var liveValidation = true;
            if (request.Payload.TryGetProperty("liveValidation", out var liveValue))
            {
                if (liveValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("liveValidation must be a boolean.");
                liveValidation = liveValue.GetBoolean();
            }
            var allowKgToTon = request.Payload.TryGetProperty("allowKgToTon", out var conversion)
                ? conversion.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? conversion.GetBoolean() : throw new ArgumentException("allowKgToTon phải là boolean.")
                : false;
            var result = await Workflow.RunLiveAsync(invoice, company, window, token, liveValidation,
                WorkflowProfile.Revision, allowKgToTon, request.RequestId, ReadOperationId(request), executionMode);
            RecordLiveRun(result);
            var status = result.Outcome is "saved" or "discarded" or "tested" ? "completed" : "blocked";
            return new(request.RequestId, status,
                result.Outcome == "saved" ? "Live production hoàn tất: đối chiếu khớp và đã Cất hóa đơn."
                : result.Outcome == "discarded" ? "Live run hoàn tất: đối chiếu không khớp, đã Bỏ hóa đơn."
                : result.Outcome == "tested" ? "Live test hoàn tất: đã đối chiếu và Bỏ chứng từ thử nghiệm, không ghi sổ."
                : "Live run lỗi; không được tiếp tục checkpoint. Hãy sửa/build version mới rồi chạy lại cùng hóa đơn từ đầu.",
                result.Context.Snapshot.InputMayHaveBeenSent,
                new
                {
                    outcome = result.Outcome,
                    retryFromStart = result.RetryFromStart,
                    continuationAllowed = false,
                    runId = result.Context.Snapshot.RunId,
                    invoiceId = result.Context.Invoice.InvoiceId,
                    expectedRevision = result.Context.Invoice.Fingerprint,
                    checkpoint = result.Context.Snapshot.Status,
                    executionMode = result.Context.ExecutionMode,
                    snapshot = result.Context.Snapshot,
                    verification = result.Verification,
                    repairRequired = result.Outcome == "failed",
                    repairIncidentId = result.Outcome == "failed" ? result.Context.Snapshot.RunId : null,
                    error = result.Error ?? result.Context.Snapshot.ErrorChain
                });
        }
        catch (Exception error)
        {
            return new(request.RequestId, "blocked", "Không thể khởi tạo live run: " + error.Message);
        }
    }

    private static WorkflowExecutionMode ReadExecutionMode(JsonElement payload, bool required)
    {
        if (!payload.TryGetProperty("executionMode", out var value))
        {
            if (required) throw new ArgumentException("executionMode bắt buộc: live_test hoặc live_production.");
            return WorkflowExecutionMode.Interactive;
        }
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException("executionMode phải là live_test hoặc live_production.");
        var mode = value.GetString() switch
        {
            "live_test" => WorkflowExecutionMode.LiveTest,
            "live_production" => WorkflowExecutionMode.LiveProduction,
            _ => throw new ArgumentException("executionMode phải là live_test hoặc live_production.")
        };
        return mode;
    }

    private static WorkflowStatus ReadBatchCheckpoint(JsonElement payload, string name, WorkflowStatus? fallback = null)
    {
        if (!payload.TryGetProperty(name, out var value))
            return fallback ?? throw new ArgumentException(name + " is required.");
        if (value.ValueKind != JsonValueKind.String
            || !Enum.TryParse<WorkflowStatus>(value.GetString(), ignoreCase: false, out var status)
            || status < WorkflowStatus.Ready || status > WorkflowStatus.AwaitingSave
            || value.GetString() != status.ToString())
            throw new ArgumentException(name + " must be a named checkpoint from Ready through AwaitingSave.");
        return status;
    }

    private async Task<OperationResult> PauseWorkflowAsync(ControlRequest request, CancellationToken token)
    {
        try
        {
            await Workflow.PauseAsync(token);
            return new(request.RequestId, "completed", "Workflow đã tạm dừng; chứng từ MISA không bị đóng, Cất hoặc Bỏ.", Data: Workflow.Current?.Snapshot);
        }
        catch (Exception error) { return new(request.RequestId, "blocked", error.Message); }
    }

    private async Task<OperationResult> ResumeWorkflowAsync(ControlRequest request, CancellationToken token)
    {
        try
        {
            var resumed = await Workflow.ResumeAsync(token);
            return new(request.RequestId, "completed", "Workflow đã tiếp tục từ checkpoint đã lưu.", Data: resumed.Snapshot);
        }
        catch (Exception error) { return new(request.RequestId, "blocked", error.Message); }
    }

    public Task<OperationResult> ExecuteAsync(string action, object? payload = null, string? expectedRevision = null,
        CancellationToken token = default)
    {
        var id = Guid.NewGuid().ToString("N");
        return DispatchAsync(new(ProductIdentity.ControlProtocolVersion, id, "ui:" + id, "ui", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.Add(ControlActions.RequestTimeout(action)), action, expectedRevision,
            JsonSerializer.SerializeToElement(payload ?? new { }, ContractJson.Options)), token);
    }

    private async Task<OperationResult> CorrectInvoiceAsync(ControlRequest request, CancellationToken token)
    {
        try
        {
            if (!request.Payload.TryGetProperty("correction", out var element))
                return new(request.RequestId, "blocked", "correct_invoice cần payload correction typed.");
            var correction = element.Deserialize<InvoiceCorrection>(ContractJson.Options)
                ?? throw new InvalidDataException("Correction payload rỗng.");
            if (!request.Payload.TryGetProperty("invoice", out var invoiceElement))
                throw new InvalidDataException("Correction requires the imported invoice draft.");
            var invoice = invoiceElement.Deserialize<InvoiceDocument>(ContractJson.Options) ?? throw new InvalidDataException("Invoice draft is empty.");
            if (request.ExpectedRevision != invoice.Fingerprint) throw new InvalidOperationException("Expected fingerprint differs from draft.");
            var active = Workflow.Current;
            if (active is not null && !WorkflowRunner.IsTerminal(active.Snapshot.Status))
                throw new InvalidOperationException("Resolve the current voucher before editing invoice source.");
            var result = await Drafts.CorrectAsync(invoice, correction, token);
            return new(request.RequestId, result.Issues.Any(issue => issue.Severity == IssueSeverity.Blocking) ? "blocked" : "completed",
                result.Issues.Any(issue => issue.Severity == IssueSeverity.Blocking)
                    ? "Correction đã lưu nhưng invoice còn lỗi chặn." : "Correction đã lưu và đã tăng revision; cần preflight lại.",
                false, result);
        }
        catch (Exception error) { return new(request.RequestId, "blocked", error.Message); }
    }

}
