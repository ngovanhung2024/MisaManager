namespace MisaManager.Core;

public sealed record OperationCorrelation(string RequestId, string OperationId);

public static class OperationContext
{
    private static readonly AsyncLocal<OperationCorrelation?> CurrentValue = new();
    public static OperationCorrelation? Current => CurrentValue.Value;

    public static IDisposable Begin(string requestId, string operationId)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = new(requestId, operationId);
        return new Scope(() => CurrentValue.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
