using System.Collections.Immutable;
using MisaManager.Core;
using MisaManager.Import;

namespace MisaManager.App;

/// <summary>Coordinates read-only workbook/profile/import operations away from the WPF view.</summary>
public sealed class ImportSession(WorkbookReader reader, WorkbookProfileStore profiles, InvoiceDraftStore drafts)
{
    public async Task<WorkbookData> ReadAsync(string path, CancellationToken token)
    {
        var workbook = await reader.ReadAsync(path, token);
        token.ThrowIfCancellationRequested();
        return workbook;
    }

    public WorkbookMappingSchema Describe(WorkbookData workbook, string sheet)
        => new InvoiceWorkbookImporter(reader).DescribeMapping(workbook, sheet);

    public Task<WorkbookProfile?> ReadProfileAsync(WorkbookData workbook, string sheet, WorkbookMappingSchema mapping, CancellationToken token)
        => profiles.ReadAsync(workbook, sheet, mapping, token);

    public async Task<ImportResult> ImportAsync(WorkbookData workbook, string sheet, WorkbookMappingSchema mapping,
        ImmutableDictionary<string, int>? columnMap, ImportConfirmation? confirmation, string? locale, CancellationToken token)
    {
        if (columnMap is not null && columnMap.Values.Distinct().Count() != columnMap.Count)
            throw new WorkbookImportException("MAPPING_INVALID", "Each source column may be selected only once.");
        var profile = await profiles.SaveAsync(workbook, sheet, mapping, columnMap, token, confirmation, locale);
        var imported = await Task.Run(() => new InvoiceWorkbookImporter(reader).Import(workbook,
            new WorkbookImportOptions(sheet, locale, ColumnMap: columnMap, ImportRevision: profile.Revision, Confirmation: confirmation), token), token);
        token.ThrowIfCancellationRequested();
        var invoices = new List<InvoiceDocument>();
        foreach (var invoice in imported.Invoices)
        {
            token.ThrowIfCancellationRequested();
            invoices.Add(await drafts.ReadAsync(invoice, token));
        }
        token.ThrowIfCancellationRequested();
        var aggregate = imported.Issues.Where(issue => issue.InvoiceId is null)
            .Concat(invoices.SelectMany(invoice => invoice.Issues)).ToArray();
        imported = imported with { Invoices = invoices, Issues = aggregate };
        return imported;
    }
}
