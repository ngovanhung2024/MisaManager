using System.Text.Json;

namespace MisaManager.Core;

public sealed record DirectControlPayload(long WindowHandle, string? AutomationId, string? Name, string? Value, string? Key,
    string? ScopeAutomationId = null);

public static class ContractValidation
{
    public static void Validate(ControlRequest request)
    {
        if (request.ProtocolVersion != ProductIdentity.ControlProtocolVersion)
            throw new InvalidDataException("Unsupported control protocol version.");
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 256)
            throw new InvalidDataException("RequestId is required and bounded.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 256)
            throw new InvalidDataException("IdempotencyKey is required and bounded.");
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 256)
            throw new InvalidDataException("SessionId is required and bounded.");
        if (string.IsNullOrWhiteSpace(request.Action) || request.Action.Length > 128)
            throw new InvalidDataException("Action is required and bounded.");
        if (request.Payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Payload must be an object.");
        if (request.Deadline <= request.IssuedAt) throw new InvalidDataException("Deadline must be after IssuedAt.");
    }

    public static DirectControlPayload ReadDirectPayload(ControlRequest request, bool requireValue = false, bool requireKey = false)
    {
        var p = request.Payload;
        if (!p.TryGetProperty("windowHandle", out var handle) || !handle.TryGetInt64(out var hwnd) || hwnd == 0)
            throw new InvalidDataException("windowHandle is required.");
        var automationId = p.TryGetProperty("automationId", out var id) ? id.GetString() : null;
        var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("automationId or name is required.");
        var value = p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var key = p.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        if (requireValue && value is null) throw new InvalidDataException("value is required.");
        if (requireKey && key is null) throw new InvalidDataException("key is required.");
        return new(hwnd, automationId, name, value, key, p.TryGetProperty("scopeAutomationId", out var scope) ? scope.GetString() : null);
    }
}
