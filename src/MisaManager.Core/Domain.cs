using System.Collections.Immutable;

namespace MisaManager.Core;

public static class ProductIdentity
{
    public const string DisplayName = "MISA Excel Manager";
    public const string TechnicalName = "MisaManager";
    public const int StateSchemaVersion = 1;
    public const int WorkbookSchemaVersion = 1;
    public const int LocatorProfileVersion = 1;

    public static string RuntimeVersion => typeof(ProductIdentity).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    public static string SourceRevision => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ProductIdentity).Assembly)?.InformationalVersion ?? "unavailable";
    private static readonly Lazy<string> RuntimeHash = new(() => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(ProductIdentity).Assembly.Location))).ToLowerInvariant());
    public static string RuntimeAssemblySha256 => RuntimeHash.Value;
}

public sealed record Money
{
    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length > 8) throw new ArgumentException("Currency is required.", nameof(currency));
        if (!DecimalValue.TryParseInvariant(amount.ToString(System.Globalization.CultureInfo.InvariantCulture), out _))
            throw new ArgumentException("Money precision/scale exceeds the contract.", nameof(amount));
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }
    public string Currency { get; }
}

public sealed record RuntimeStateIdentity(string AppVersion, string SourceRevision, string ProfileRevision);

public enum IssueSeverity { Info, Warning, Blocking }
public enum ValuePresence { Missing, Provided, NotApplicable }
public enum PartyKind { Unspecified, Organization, Individual }
public enum InputDisposition { NotSent, SentUnverified, Verified, Uncertain }
// LiveTest is retired but kept so run history written by older versions still deserializes
// (ContractJson disallows unknown values) and keeps blocking duplicate entry.
public enum WorkflowExecutionMode { Interactive, LiveTest, LiveProduction }
public enum WorkflowStatus
{
    Ready, PreflightPassed, SalesSurface, VoucherCreated, CustomerVerified,
    DatesVerified, InvoiceVerified, ItemsVerified, TotalsVerified, AwaitingSave,
    Saved, Paused, Blocked, InputUncertain, SaveUncertain, Cancelled, Discarded, Failed
}
public sealed record Bounds(double X, double Y, double Width, double Height);

public sealed record SourceReference
{
    public string FilePath { get; }
    public string Sha256 { get; }
    public string Worksheet { get; }
    public int FirstRow { get; }
    public int LastRow { get; }
    public int ImportRevision { get; }

    public SourceReference(string filePath, string sha256, string worksheet, int firstRow, int lastRow, int importRevision = 1)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path is required.", nameof(filePath));
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("SHA-256 is required.", nameof(sha256));
        if (string.IsNullOrWhiteSpace(worksheet) || firstRow < 1 || lastRow < firstRow || importRevision < 1)
            throw new ArgumentException("Invalid source range.", nameof(worksheet));
        FilePath = Path.GetFullPath(filePath);
        Sha256 = sha256.ToLowerInvariant();
        Worksheet = worksheet;
        FirstRow = firstRow;
        LastRow = lastRow;
        ImportRevision = importRevision;
    }
}

public sealed record SourceCell(
    string Worksheet,
    int Row,
    int Column,
    string Address,
    string RawValue,
    string CellType,
    string NumberFormat,
    string? Formula,
    string? ParsedValue,
    string? InheritedFrom = null,
    string ValueOrigin = "workbook",
    string? Confirmation = null)
{
    public bool HasFormula => !string.IsNullOrWhiteSpace(Formula);
}

public sealed record InvoiceIdentity(string SellerTaxCode, string Series, string Number, DateOnly? Issued)
{
    public override string ToString() => string.Join("|", SellerTaxCode, Series, Number, Issued?.ToString("yyyy-MM-dd") ?? "");
}

public sealed record ImportConfirmation(bool SumLineTotals, bool UseLegacySalesDefaults,
    string CompanyIdentity, string WorkbookSha256, string Confirmation, bool CalculateUnitPriceFromAmount = false);

public sealed record Party(string Name, string TaxCode, string Address, string Code, ValuePresence TaxCodePresence,
    PartyKind Kind = PartyKind.Unspecified, string Contact = "")
{
    public static Party Empty(string name = "") => new(name, "", "", "", ValuePresence.Missing);
}

public sealed record TaxSummary(string Rate, decimal AmountBeforeTax, decimal TaxAmount, SourceCell? Source);

public sealed record InvoiceLine(
    string LineId,
    string ItemCode,
    string Name,
    string Unit,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? AmountBeforeTax,
    decimal? Discount,
    string TaxRate,
    decimal? TaxAmount,
    decimal? TotalWithTax,
    decimal? DiscountRate,
    string Kind,
    ImmutableDictionary<string, SourceCell> Cells)
{
    public bool HasFormula => Cells.Values.Any(cell => cell.HasFormula);
}

public sealed record InvoiceDocument(
    string InvoiceId,
    int Revision,
    string Fingerprint,
    InvoiceIdentity Identity,
    SourceReference Source,
    Party Seller,
    Party Buyer,
    string Template,
    string Currency,
    string PaymentMethod,
    DateOnly? Issued,
    DateOnly? VoucherDate,
    DateOnly? PostedDate,
    string? InvoiceType,
    decimal? ExchangeRate,
    ImmutableArray<InvoiceLine> Lines,
    decimal? Subtotal,
    decimal? TaxTotal,
    decimal? GrandTotal,
    decimal? InvoiceDiscount,
    ImmutableArray<TaxSummary> TaxSummaries,
    string Notes,
    ImmutableArray<ImportIssue> Issues,
    ImmutableArray<SourceCell> Cells,
    ImportConfirmation? ImportConfirmation = null,
    ImmutableArray<ImportIssue> SourceIssues = default,
    ImmutableArray<CorrectionAudit> Corrections = default,
    ImmutableDictionary<string, SourceCell>? HeaderCells = null,
    DateOnly? Signed = null)
{
    public bool IsBlocked => Issues.IsDefault
        || Issues.Any(issue => issue is null || issue.Severity == IssueSeverity.Blocking);
}

public sealed record ImportIssue(
    string Code,
    IssueSeverity Severity,
    string Message,
    string? Worksheet = null,
    string? Cell = null,
    string? InvoiceId = null,
    string? LineId = null,
    string? Expected = null,
    string? Actual = null,
    string? Resolution = null);

public sealed record ImportResult(
    SourceReference Source,
    IReadOnlyList<string> Worksheets,
    IReadOnlyList<InvoiceDocument> Invoices,
    IReadOnlyList<ImportIssue> Issues,
    int ReadRows,
    int IgnoredEmptyRows,
    int SkippedUnnumberedRows = 0,
    int SkippedNegativeInvoices = 0,
    // Purchase invoices left out because they buy services/expenses rather than stock goods.
    IReadOnlyList<ServiceInvoice>? Services = null)
{
    public bool IsBlocked => Issues.Any(issue => issue.Severity == IssueSeverity.Blocking)
        || Invoices.Any(invoice => invoice.IsBlocked);
}

public sealed record WorkflowSnapshot(
    string RunId,
    string InvoiceId,
    int InvoiceRevision,
    WorkflowStatus Status,
    string? CompanyIdentity,
    string? WindowIdentity,
    int? ActiveLine,
    DateTimeOffset UpdatedAt,
    string AppVersion,
    int SchemaVersion,
    WorkflowStatus? PausedFromStatus = null,
    string? NativeFrame = null,
    bool InputPending = false,
    string? PendingAction = null,
    string? LastMessage = null,
    bool InputMayHaveBeenSent = false,
    string? LastErrorCode = null,
    InputDisposition LastInputDisposition = InputDisposition.NotSent,
    RuntimeStateIdentity? RuntimeIdentity = null,
    string? RequestId = null, string? OperationId = null, string? ErrorChain = null);

public sealed record OperationResult(
    string RequestId,
    string Status,
    string Message,
    bool InputMayHaveBeenSent = false,
    object? Data = null,
    // Refused before a run started (WorkflowRunner.RefusedBeforeStart): nothing reached MISA, a batch moves on.
    bool RefusedBeforeStart = false);

// A live run is an end-to-end business attempt.  It is deliberately separate
// from checkpoint state so both a saved invoice and a verified discard remain
// auditable even when the native workflow fails part way through.
public sealed record LiveVerificationRecord(
    string RunId,
    string InvoiceId,
    int InvoiceRevision,
    string InvoiceFingerprint,
    string SourceSha256,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Outcome,
    bool Reconciled,
    bool Matched,
    ReconciliationResult? Comparison = null,
    string? ErrorCode = null,
    string? Error = null,
    string? ErrorChain = null,
    bool RetryFromStart = false,
    RuntimeStateIdentity? RuntimeIdentity = null);

public sealed record LiveRunResult(
    WorkflowContext Context,
    LiveVerificationRecord Verification,
    string Outcome,
    bool RetryFromStart,
    string? Error = null,
    // The failure looked like user interference and left no partial voucher, so a fresh rerun cannot duplicate data.
    bool AutoRerunSafe = false,
    // Nothing of this run is left in MISA (voucher never opened, or its discard was confirmed) and no save is
    // uncertain, so a batch may safely move on to the next invoice.
    bool MisaClean = false);

public static class OperationStatuses
{
    public const string Accepted = "accepted";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Blocked = "blocked";
    public const string Uncertain = "uncertain";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string Error = "error";
}

public sealed record InvoiceCorrection(
    string InvoiceId,
    int ExpectedRevision,
    string Field,
    string Value,
    string Provenance = "user_override",
    string? Confirmation = null);

public sealed record CorrectionChange(string Field, string Before, string After, string Provenance);
public sealed record CorrectionAudit(int Revision, DateTimeOffset OccurredAt, string Confirmation,
    IReadOnlyList<CorrectionChange> Changes);
public sealed record InvoiceCorrectionResult(InvoiceDocument Invoice, IReadOnlyList<CorrectionChange> Changes,
    IReadOnlyList<ImportIssue> Issues);

public sealed record RunEvent(string RunId, long Sequence, DateTimeOffset OccurredAt, string Checkpoint,
    string InvoiceId, int InvoiceRevision, string AppVersion, int SchemaVersion, WorkflowStatus Status,
    string? NativeFrame, string? InputDisposition, string? ErrorCode, string Message,
    string? RequestId = null, string? OperationId = null, string? ProfileRevision = null,
    string? SourceEpoch = null, string? SourceRevision = null, string? ErrorChain = null);

