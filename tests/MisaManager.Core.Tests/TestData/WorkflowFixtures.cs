namespace MisaManager.Core.Tests.TestData;

/// <summary>Minimal checkpoint objects for the audit-log tests; only plain data records are used.</summary>
internal static class WorkflowFixtures
{
    public static string NewRunId() => Guid.NewGuid().ToString("N");

    public static WorkflowContext Context(InvoiceDocument invoice, string runId, WorkflowStatus status = WorkflowStatus.Ready, string? message = null)
        => new(invoice,
            new WorkflowSnapshot(runId, invoice.InvoiceId, invoice.Revision, status, "CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT",
                "Bán hàng", null, DateTimeOffset.UtcNow, "0.1.0", ProductIdentity.StateSchemaVersion, LastMessage: message),
            "CÔNG TY TNHH THƯƠNG MẠI LÁ VIỆT", "Bán hàng");

    /// <summary>Writes the checkpoint file the audit store consults to decide whether a run is still unresolved.</summary>
    public static void WriteCheckpoint(string stateRoot, string runId, string status, bool inputPending = false, int schemaVersion = 1)
    {
        var path = Path.Combine(stateRoot, "runs", runId, "workflow.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = inputPending ? "true" : "false";
        File.WriteAllText(path, "{\"schemaVersion\":" + schemaVersion + ",\"data\":{\"snapshot\":{\"runId\":\"" + runId
            + "\",\"status\":\"" + status + "\",\"inputPending\":" + pending + "}}}");
    }
}
