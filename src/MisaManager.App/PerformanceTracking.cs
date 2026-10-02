using System.Globalization;

namespace MisaManager.App;

public sealed record PerformanceTraceEntry(
    string Name,
    string Details,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string? Error = null)
{
    public long DurationMilliseconds
    {
        get
        {
            var end = CompletedAt ?? DateTimeOffset.UtcNow;
            return Math.Max(0, (long)(end - StartedAt).TotalMilliseconds);
        }
    }

    public string DurationDisplay => DurationMilliseconds < 1000
        ? DurationMilliseconds.ToString("N0", CultureInfo.InvariantCulture) + " ms"
        : TimeSpan.FromMilliseconds(DurationMilliseconds).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

    public string StartedDisplay => StartedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

public sealed class WorkflowErrorEventArgs(string message, string details, DateTimeOffset occurredAt) : EventArgs
{
    public string Message { get; } = message;
    public string Details { get; } = details;
    public DateTimeOffset OccurredAt { get; } = occurredAt;
}
