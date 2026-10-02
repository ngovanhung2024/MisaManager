using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MisaManager.Automation;
using MisaManager.Core;

namespace MisaManager.App;

public sealed partial class AppServices
{
    private object RuntimeStatus()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            observedAt = DateTimeOffset.UtcNow,
            runtime = new RuntimeStateIdentity(ProductIdentity.RuntimeVersion, ProductIdentity.SourceRevision, WorkflowProfile.Revision),
            processId = process.Id,
            processStartUtc = process.StartTime.ToUniversalTime(),
            executable = Environment.ProcessPath,
            initialization = Initialization.Status.ToString(),
            initializationError = Initialization.Exception?.GetBaseException().Message,
            nativeQueue = nativeOwner.Metrics,
            workflowNative = workflowGateway.Metrics,
            profile = new { WorkflowProfile.Status, WorkflowProfile.Revision },
            inputSent = false
        };
    }

    private static bool MatchesWindow(string serialized, WindowIdentity window)
    {
        var parts = serialized.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Where(part => part.Length == 2).ToArray();
        string? Part(string key) => parts.FirstOrDefault(part => part[0] == key)?[1];
        return long.TryParse(Part("hwnd"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var handle)
            && handle == window.WindowHandle.ToInt64()
            && int.TryParse(Part("pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            && pid == window.ProcessId
            && DateTimeOffset.TryParse(Part("start"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            && start.UtcDateTime == window.ProcessStartUtc
            && string.Equals(Part("exe"), window.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<NativeObservation> ObserveWorkflowAsync(WorkflowContext? current, long? handle, CancellationToken token)
    {
        var candidates = await Native.FindCandidatesAsync(token);
        WindowIdentity? selected;
        if (current is not null)
            selected = candidates.SingleOrDefault(candidate => MatchesWindow(current.WindowIdentity, candidate)
                && (!handle.HasValue || candidate.WindowHandle.ToInt64() == handle.Value));
        else if (handle.HasValue)
            selected = candidates.SingleOrDefault(candidate => candidate.WindowHandle.ToInt64() == handle.Value);
        else
            throw new AutomationException("WINDOW_SELECTION_REQUIRED", "No current workflow; select an explicit windowHandle from diagnostics.");
        if (selected is null)
            throw new AutomationException("WINDOW_IDENTITY_CHANGED", "No fresh MISA window matches the selected workflow HWND/PID/process start/executable.");
        var observed = await Native.ObserveAsync(selected.WindowHandle, null, token);
        if (observed.Frame.Window.ProcessId != selected.ProcessId
            || observed.Frame.Window.ProcessStartUtc != selected.ProcessStartUtc
            || !string.Equals(observed.Frame.Window.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("WINDOW_IDENTITY_CHANGED", "MISA identity changed during observation.");
        return observed;
    }

    private async Task<OperationResult> ReadLiveStateAsync(ControlRequest request, CancellationToken token)
    {
        var current = Workflow.Current;
        long? handle = null;
        if (request.Payload.TryGetProperty("windowHandle", out var value))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number == 0)
                throw new ArgumentException("windowHandle must be a nonzero integer.");
            handle = number;
        }
        try
        {
            var observed = await ObserveWorkflowAsync(current, handle, token);
            var unchanged = current == Workflow.Current;
            return new(request.RequestId, unchanged ? "completed" : "blocked",
                unchanged ? "Fresh native observation read without changing focus." : "Workflow changed during observation; poll again.",
                Data: new
                {
                    workflow = current?.Snapshot, workflowEvidence = "saved_checkpoint",
                    expectedRevision = current?.Invoice.Fingerprint,
                    freshObservation = observed, workflowUnchanged = unchanged,
                    runtime = RuntimeStatus(), inputSent = false
                });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return new(request.RequestId, "blocked", "Fresh native observation unavailable: " + error.Message,
                Data: new { workflow = current?.Snapshot, workflowEvidence = "saved_checkpoint",
                    errorCode = (error as AutomationException)?.Code ?? error.GetType().Name,
                    observationError = error.Message, runtime = RuntimeStatus(), inputSent = false });
        }
    }

    private async Task<OperationResult> BuildFailureContextAsync(ControlRequest request, CancellationToken token)
    {
        var includeNative = true;
        if (request.Payload.TryGetProperty("includeNative", out var value))
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("includeNative must be a boolean.");
            includeNative = value.GetBoolean();
        }
        var current = Workflow.Current;
        NativeObservation? observation = null;
        string? observationError = includeNative ? null : "Native observation was not requested.";
        string? observationErrorCode = null;
        if (includeNative)
        {
            try { observation = await ObserveWorkflowAsync(current, null, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                observationError = error.Message;
                observationErrorCode = (error as AutomationException)?.Code ?? error.GetType().Name;
            }
        }
        var snapshot = current?.Snapshot;
        // InputMayHaveBeenSent also records successfully verified historical input.
        var uncertain = snapshot?.InputPending == true || snapshot is not null && !WorkflowRunner.IsTerminal(snapshot.Status)
            && (snapshot.LastInputDisposition is InputDisposition.Uncertain or InputDisposition.SentUnverified
                || snapshot.Status is WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain
                || snapshot.PausedFromStatus is WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain);
        var lineIndex = snapshot?.ActiveLine;
        var activeLine = lineIndex is >= 0 && current is not null && lineIndex < current.Invoice.Lines.Length
            ? current.Invoice.Lines[lineIndex.Value] : null;
        var recent = operations.Values.Where(operation => operation.SessionId == request.SessionId
                && operation.RequestId != request.RequestId
                && operation.Action is not ("failure_context" or "runtime_status" or "live_test_state" or "diagnostics" or "verification_history"))
            .OrderByDescending(operation => operation.StartedAt).Take(10).ToArray();
        var verification = current is null ? null
            : (await Workflow.ReadLiveVerificationHistoryAsync(token))
                .FirstOrDefault(item => item.RunId == current.Snapshot.RunId);
        var unchanged = current == Workflow.Current;
        return new(request.RequestId, "completed", "Diagnostic packet collected; saved evidence and fresh observation are labelled separately.",
            Data: new
            {
                collectedAt = DateTimeOffset.UtcNow, runtime = RuntimeStatus(),
                incidentId = snapshot?.RunId, expectedRevision = current?.Invoice.Fingerprint,
                checkpoint = snapshot, checkpointEvidence = "saved_checkpoint",
                liveVerification = verification,
                expected = current is null ? null : new
                {
                    current.Invoice.Identity, current.Invoice.Source, current.CompanyIdentity,
                    current.Invoice.Buyer, current.Invoice.VoucherDate, current.Invoice.PostedDate,
                    current.Invoice.Subtotal, current.Invoice.TaxTotal, current.Invoice.GrandTotal,
                    lineCount = current.Invoice.Lines.Length, activeLine,
                    issues = current.Invoice.Issues.Take(50).ToArray(), issuesTruncated = current.Invoice.Issues.Length > 50
                },
                priorNativeValues = current?.NativeValues?.Take(100).ToDictionary(pair => pair.Key, pair => pair.Value),
                priorNativeValuesTruncated = current?.NativeValues?.Count > 100,
                priorNativeValuesEvidence = "saved_readback_not_current_UI",
                recentSessionOperations = recent, freshObservation = observation, observationError, observationErrorCode,
                workflowUnchanged = unchanged,
                recovery = new
                {
                    inputUncertain = uncertain,
                    nextStep = !unchanged ? "Workflow changed; collect a new packet."
                        : uncertain ? "Observe and resolve the current voucher before any retry; do not replay input."
                        : snapshot?.Status is WorkflowStatus.Blocked or WorkflowStatus.Failed
                            ? "For source repair, call misa_begin_repair with this incidentId before editing; after a checked restart use misa_rebind_after_restart with restartFromBeginning=true for a live run."
                            : "Inspect the current checkpoint and fresh native evidence before choosing an action.",
                    repairSequence = new[] { "misa_begin_repair", "edit source and stage checked release", "misa_complete_repair",
                        "misa_restart action=reload", "misa_restart action=acknowledge_restart",
                        "misa_restart action=rebind_after_restart restartFromBeginning=true", "fresh native readback" },
                    sourceRoutes = new[] { "src/MisaManager.Core/Workflow.cs", "src/MisaManager.Automation/NativeWorkflowGateway.cs",
                        "src/MisaManager.Automation/NativeWorkflowOperations.cs", "src/MisaManager.App/AppServices.cs" }
                },
                inputSent = false
            });
    }
}
