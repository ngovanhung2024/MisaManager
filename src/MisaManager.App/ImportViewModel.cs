using System.Collections.Immutable;
using MisaManager.Core;
using MisaManager.Import;

namespace MisaManager.App;

/// <summary>Owns workbook/profile/import state so the view only handles dialogs and presentation.</summary>
public sealed class ImportViewModel(ImportSession session)
{
    private long generation;
    private CancellationTokenSource? work;
    private string? selectedSheet;
    private bool profileLoaded;
    public bool MappingEdited { get; set; }
    public bool IsBusy => work is not null;
    public WorkbookData? Workbook { get; private set; }
    public WorkbookMappingSchema? Mapping { get; private set; }
    public ImportResult? Result { get; private set; }
    public ImportConfirmation? Confirmation { get; private set; }
    public string? Locale { get; private set; }
    public bool Purchase { get => session.Purchase; set => session.Purchase = value; }

    public async Task<WorkbookData> ReadAsync(string path, CancellationToken token)
    {
        var current = Interlocked.Increment(ref generation);
        Workbook = null;
        ClearSelection();
        var workbook = await session.ReadAsync(path, token);
        token.ThrowIfCancellationRequested();
        if (current != Volatile.Read(ref generation)) throw new OperationCanceledException(token);
        Workbook = workbook;
        Mapping = null;
        Result = null;
        Confirmation = null;
        Locale = null;
        return workbook;
    }

    public WorkbookMappingSchema Describe(string sheet)
    {
        Interlocked.Increment(ref generation);
        ClearSelection();
        selectedSheet = sheet;
        Mapping = session.Describe(Workbook ?? throw new InvalidOperationException("Workbook has not been selected."), sheet);
        return Mapping;
    }

    public async Task<WorkbookProfile?> LoadProfileAsync(string sheet, CancellationToken token)
    {
        var current = Volatile.Read(ref generation);
        var mapping = Mapping ?? throw new InvalidOperationException("Column mapping has not been prepared.");
        var profile = await session.ReadProfileAsync(Workbook!, sheet, mapping, token);
        token.ThrowIfCancellationRequested();
        if (current != Volatile.Read(ref generation) || sheet != selectedSheet) throw new OperationCanceledException(token);
        if (profile is { } saved && saved.WorkbookSha256 == Workbook?.Sha256)
        {
            Confirmation = saved.Confirmation;
            Locale = saved.Locale;
        }
        profileLoaded = true;
        return profile;
    }

    public async Task<ImportResult> ImportAsync(string sheet,
        ImmutableDictionary<string, int>? columnMap, CancellationToken token)
    {
        var current = Volatile.Read(ref generation);
        var workbook = Workbook ?? throw new InvalidOperationException("Workbook has not been selected.");
        var mapping = Mapping ?? throw new InvalidOperationException("Column mapping has not been prepared.");
        if (sheet != selectedSheet) throw new InvalidOperationException("Worksheet selection has changed.");
        if (!profileLoaded) await LoadProfileAsync(sheet, token);
        if (current != Volatile.Read(ref generation)) throw new OperationCanceledException(token);
        var imported = await session.ImportAsync(workbook, sheet, mapping, columnMap, Confirmation, Locale, token);
        token.ThrowIfCancellationRequested();
        if (current != Volatile.Read(ref generation)) throw new OperationCanceledException(token);
        return Result = imported;
    }

    public void SetConfirmation(ImportConfirmation? confirmation) => Confirmation = confirmation;
    public void SetLocale(string? locale) => Locale = locale;
    public void ClearResult() => Result = null;

    /// <summary>Forgets the open workbook, its mapping and result.</summary>
    public void Reset()
    {
        Cancel();
        Workbook = null;
        ClearSelection();
    }

    /// <summary>Shows the invoices saved from the previous session; the workbook itself is not re-read.</summary>
    public void Restore(ImportResult result)
    {
        Interlocked.Increment(ref generation);
        Workbook = null;
        Mapping = null;
        Result = result;
    }

    private void ClearSelection()
    {
        Mapping = null; Confirmation = null; Locale = null; Result = null;
        selectedSheet = null; profileLoaded = false; MappingEdited = false;
    }

    public CancellationTokenSource BeginWork()
    {
        Cancel(); ClearResult();
        return work = new CancellationTokenSource();
    }
    public bool IsCurrent(CancellationTokenSource candidate) => ReferenceEquals(work, candidate) && !candidate.IsCancellationRequested;
    public void EndWork(CancellationTokenSource candidate)
    {
        if (ReferenceEquals(work, candidate)) work = null;
        candidate.Dispose();
    }
    public void Cancel()
    {
        Interlocked.Increment(ref generation);
        work?.Cancel(); work = null;
    }
    public void ApplyCorrection(InvoiceDocument invoice)
    {
        if (Result is null) return;
        var invoices = Result.Invoices.Select(item => item.InvoiceId == invoice.InvoiceId ? invoice : item).ToArray();
        Result = Result with { Invoices = invoices, Issues = Result.Issues.Where(issue => issue.InvoiceId is null)
            .Concat(invoices.SelectMany(item => item.Issues)).ToArray() };
    }
}
