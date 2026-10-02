using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MisaManager.Core;

namespace MisaManager.Import;

public sealed record WorkbookProfile(int MappingVersion, string HeaderFingerprint,
    ImmutableDictionary<string, int>? Columns, string WorkbookSha256, int Revision,
    ImportConfirmation? Confirmation = null, string? Locale = null);

public sealed class WorkbookProfileStore(IStateStore state)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<WorkbookProfile?> ReadAsync(WorkbookData workbook, string sheet, WorkbookMappingSchema schema,
        CancellationToken token = default)
    {
        var profile = await state.ReadAsync<WorkbookProfile>("workbook-profile", ProfilePath(workbook, sheet), token);
        if (profile is null || profile.HeaderFingerprint != HeaderHash(schema)) return null;
        if (profile.MappingVersion != ProductIdentity.WorkbookSchemaVersion || profile.Revision < 1)
            throw new InvalidDataException("Cấu hình cột không đúng phiên bản; không tự đặt lại.");
        return profile;
    }

    public async Task<WorkbookProfile> SaveAsync(WorkbookData workbook, string sheet, WorkbookMappingSchema schema,
        ImmutableDictionary<string, int>? columns, CancellationToken token = default, ImportConfirmation? confirmation = null,
        string? locale = null)
    {
        await gate.WaitAsync(token);
        try
        {
            var previous = await state.ReadAsync<WorkbookProfile>("workbook-profile", ProfilePath(workbook, sheet), token);
            var headerHash = HeaderHash(schema);
            var sameColumns = previous?.Columns is null ? columns is null : columns is not null
                && previous.Columns.Count == columns.Count && previous.Columns.All(pair => columns.GetValueOrDefault(pair.Key) == pair.Value);
            var same = previous is not null && previous.MappingVersion == ProductIdentity.WorkbookSchemaVersion
                && previous.HeaderFingerprint == headerHash && previous.WorkbookSha256 == workbook.Sha256 && sameColumns
                && previous.Confirmation == confirmation && previous.Locale == locale;
            if (confirmation is not null && confirmation.WorkbookSha256 != workbook.Sha256)
                throw new InvalidDataException("Xác nhận nhập không khớp workbook hiện tại.");
            var profile = new WorkbookProfile(ProductIdentity.WorkbookSchemaVersion, headerHash, columns, workbook.Sha256,
                same ? previous!.Revision : checked((previous?.Revision ?? 0) + 1), confirmation, locale);
            await state.WriteAsync("workbook-profile", ProfilePath(workbook, sheet), profile, token);
            return profile;
        }
        finally { gate.Release(); }
    }

    private static string HeaderHash(WorkbookMappingSchema schema)
        => Hash(JsonSerializer.Serialize(new { schema.HeaderRow, schema.Columns, fields = InvoiceWorkbookImporter.Fields }, ContractJson.Compact));
    private static string ProfilePath(WorkbookData workbook, string sheet)
        => Path.Combine("import-profiles", Hash(JsonSerializer.Serialize(new[] { workbook.FilePath.ToUpperInvariant(), sheet })) + ".json");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
