using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using MisaManager.Core;
using Microsoft.Win32.SafeHandles;

namespace MisaManager.Control;

public interface IControlDispatcher
{
    Task<OperationResult> DispatchAsync(ControlRequest request, CancellationToken cancellationToken);
}

public sealed class ControlServer(IControlDispatcher dispatcher, string? pipeName = null)
{
    public const int MaxPayloadBytes = 1024 * 1024;
    private readonly string pipeName = pipeName ?? ProductIdentity.TechnicalName + ".control";
    private readonly ConcurrentDictionary<string, (string Body, OperationResult Result)> completed = new(StringComparer.Ordinal);
    private readonly ControlJournal journal = new();
    private readonly string sessionNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private readonly CancellationTokenSource stopping = new();
    private readonly object cancellationGate = new();
    private readonly Dictionary<(string Session, string Request), CancellationTokenSource> activeRequests = new();
    private readonly Dictionary<(string Session, string Request), DateTimeOffset> cancelledRequests = new();
    private Task? loop;

    public void Start()
    {
        if (loop is not null) return;
        // Surface listener creation failures before the WPF shell opens.
        var pipe = CreatePipe();
        loop = Task.Run(() => AcceptLoopAsync(pipe, stopping.Token));
    }

    public async Task StopAsync()
    {
        stopping.Cancel();
        if (loop is not null) await loop;
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var clients = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(token);
                var connected = pipe;
                pipe = CreatePipe();
                clients.RemoveAll(task => task.IsCompleted);
                clients.Add(ServeClientAsync(connected, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Trace.TraceError("Control accept failed: {0}", error);
            throw;
        }
        finally
        {
            await pipe.DisposeAsync();
            stopping.Cancel();
            await Task.WhenAll(clients);
        }
    }

    public static async Task CheckListenerHealthAsync()
    {
        var name = ProductIdentity.TechnicalName + ".health." + Guid.NewGuid().ToString("N");
        using var pipe = CreatePipe(name);
        using var next = CreatePipe(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = pipe.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await accepting;
        await client.WriteAsync(new byte[] { 0x4d }, deadline.Token);
        var received = new byte[1];
        await pipe.ReadExactlyAsync(received, deadline.Token);
        if (received[0] != 0x4d) throw new IOException("Control listener health readback mismatch.");
    }

    private NamedPipeServerStream CreatePipe() => CreatePipe(pipeName);

    private static NamedPipeServerStream CreatePipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current Windows user could not be resolved.");
        // Apply DACL and label at creation, without asking .NET to request audit
        // privileges or reopening later instances with WRITE_OWNER access.
        var sddl = $"D:P(A;;GA;;;{user.Value})S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor
            };
            const uint duplexOverlapped = 0x00000003 | 0x40000000;
            const uint byteModeRejectRemote = 0x00000008;
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, duplexOverlapped, byteModeRejectRemote,
                255, MaxPayloadBytes, MaxPayloadBytes, 0, ref security);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes security);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe) await ServeAsync(pipe, token);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            while (pipe.IsConnected && !serverToken.IsCancellationRequested)
            {
                var (line, tooLarge) = await ReadBoundedLineAsync(reader, serverToken);
                if (tooLarge)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { status = "error", code = "PAYLOAD_TOO_LARGE" }, ContractJson.Compact));
                    break;
                }
                if (line is null) break;
                var response = await HandleAsync(line, serverToken);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, ContractJson.Compact));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Trace.TraceError("Control client disconnected or failed: {0}", error); }
    }

    private static async Task<(string? Line, bool TooLarge)> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var characterBuffer = new char[1];
        var builder = new StringBuilder();
        while (true)
        {
            if (await reader.ReadAsync(characterBuffer.AsMemory(), token) == 0)
            {
                if (builder.Length == 0) return (null, false);
                var line = builder.ToString();
                var tooLarge = Encoding.UTF8.GetByteCount(line) > MaxPayloadBytes;
                return (tooLarge ? null : line, tooLarge);
            }
            var character = characterBuffer[0];
            if (character == '\n')
            {
                if (builder.Length > 0 && builder[^1] == '\r') builder.Length--;
                var line = builder.ToString();
                return (Encoding.UTF8.GetByteCount(line) > MaxPayloadBytes ? null : line,
                    Encoding.UTF8.GetByteCount(line) > MaxPayloadBytes);
            }
            builder.Append(character);
            if (builder.Length > MaxPayloadBytes * 2) return (null, true);
        }
    }

    public static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        var result = await ReadBoundedLineAsync(reader, token);
        if (result.TooLarge) throw new InvalidDataException("Control message exceeds the payload limit.");
        return result.Line;
    }

    private async Task<OperationResult> HandleAsync(string body, CancellationToken serverToken)
    {
        ControlRequest request;
        try { request = JsonSerializer.Deserialize<ControlRequest>(body, ContractJson.Options) ?? throw new JsonException(); }
        catch (Exception error) { return new("", "error", "Request không đúng protocol: " + error.Message); }
        try { ContractValidation.Validate(request); }
        catch (Exception error) { return new(request.RequestId ?? "", "error", "Invalid request: " + error.Message); }
        if (request.ProtocolVersion != ProductIdentity.ControlProtocolVersion)
            return new(request.RequestId, "error", "Protocol version không được hỗ trợ.");
        request = request with { Action = ControlActions.Canonical(request.Action ?? "") };
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.Action)
            || request.IdempotencyKey.Length > 256 || request.RequestId.Length > 256 || request.SessionId.Length > 256
            || request.Payload.ValueKind != JsonValueKind.Object)
            return new(request.RequestId ?? "", "error", "Request thiếu định danh, action hoặc payload object hợp lệ.");
        var handshake = request.Action == "capabilities" && string.IsNullOrWhiteSpace(request.SessionNonce);
        if (!handshake && !string.Equals(request.SessionNonce, sessionNonce, StringComparison.Ordinal))
            return new(request.RequestId, "error", "Session nonce không hợp lệ; gọi capabilities để tạo phiên mới.");
        var now = DateTimeOffset.UtcNow;
        if (request.Deadline <= now || request.IssuedAt > now.AddMinutes(1))
            return new(request.RequestId, "expired", "Request đã hết hạn trước khi dispatch.");
        if (request.Deadline - now > ControlActions.RequestTimeout(request.Action))
            return new(request.RequestId, "error", "Request deadline exceeds the bounded budget for this action.");
        if (request.Action == "cancel_request")
        {
            if (!request.Payload.TryGetProperty("requestId", out var target) || target.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(target.GetString()) || target.GetString()!.Length > 256)
                return new(request.RequestId, "error", "Cancellation requires a bounded requestId.");
            lock (cancellationGate)
            {
                PruneCancellations(now);
                var key = (request.SessionId, target.GetString()!);
                if (cancelledRequests.Count >= 4096 && !cancelledRequests.ContainsKey(key))
                    return new(request.RequestId, "blocked", "Cancellation capacity exceeded.");
                cancelledRequests[key] = now.AddMinutes(10);
                if (activeRequests.TryGetValue(key, out var active)) active.Cancel();
            }
            return new(request.RequestId, "completed", "Cancellation requested; query the original request result before retrying.");
        }
        if (handshake)
        {
            var connected = await dispatcher.DispatchAsync(request, serverToken);
            return connected.Data is CapabilitySnapshot capabilities
                ? connected with { Data = capabilities with { SessionNonce = sessionNonce } } : connected;
        }
        // Reads need no lost-response replay; skipping the fsync'd journal keeps
        // agent polling off the disk and out of the in-memory completion cache.
        if (!MayMutate(request.Action)) return await DispatchReadAsync(request, serverToken);
        // Transport timestamps and the app nonce change after reconnect; semantic identity does not.
        body = JsonSerializer.Serialize(new { request.ProtocolVersion,
            request.Action, request.ExpectedRevision, request.Payload }, ContractJson.Compact);
        try { journal.BindRequest(request, body); }
        catch (Exception error) { return new(request.RequestId, "error", error.Message); }
        if (completed.TryGetValue(request.IdempotencyKey, out var previous))
            return string.Equals(previous.Body, body, StringComparison.Ordinal)
                ? previous.Result with { RequestId = request.RequestId }
                : new(request.RequestId, "error", "Idempotency key đã dùng cho body khác.");
        ControlJournalRecord? journalEntry;
        try
        {
            journalEntry = journal.Reserve(request.IdempotencyKey, body, now);
        }
        catch (Exception error)
        {
            return new(request.RequestId, "error", "Unable to read control idempotency journal: " + error.Message);
        }
        if (journalEntry is not null)
        {
            if (!string.Equals(journalEntry.Body, body, StringComparison.Ordinal))
                return new(request.RequestId, "error", "Idempotency key is already used for a different body.");
            if (journalEntry.State == "completed" && journalEntry.Result is not null)
            {
                completed.TryAdd(request.IdempotencyKey, (body, journalEntry.Result));
                return journalEntry.Result with { RequestId = request.RequestId };
            }
            if (journalEntry.State == "pending")
                return new(request.RequestId, "uncertain", "A previous request started but did not record a result; do not replay input.", ControlActions.MaySendInput(request.Action));
            return new(request.RequestId, "error", "Control idempotency journal has an unsupported state.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var remaining = request.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) deadline.Cancel(); else deadline.CancelAfter(remaining);
        var requestKey = (request.SessionId, request.RequestId);
        lock (cancellationGate)
        {
            PruneCancellations(now);
            activeRequests.Add(requestKey, deadline);
            if (cancelledRequests.ContainsKey(requestKey)) deadline.Cancel();
        }
        OperationResult result;
        var dispatched = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            dispatched = true;
            result = await dispatcher.DispatchAsync(request, deadline.Token);
        }
        catch (AutomationException error) { result = new(request.RequestId, error.InputMayHaveBeenSent ? "uncertain" : "blocked", error.Message, error.InputMayHaveBeenSent); }
        catch (Exception error)
        {
            var uncertain = dispatched && MayMutate(request.Action);
            result = new(request.RequestId, uncertain ? "uncertain" : error is OperationCanceledException ? "cancelled" : "error",
                error.Message, dispatched && ControlActions.MaySendInput(request.Action));
        }
        finally { lock (cancellationGate) activeRequests.Remove(requestKey); }
        try
        {
            journal.WriteCompleted(request.IdempotencyKey, body, result, DateTimeOffset.UtcNow);
            completed.TryAdd(request.IdempotencyKey, (body, result));
            return result;
        }
        catch (Exception error)
        {
            var uncertain = new OperationResult(request.RequestId, "uncertain",
                "Request finished but its completion could not be recorded; do not replay input: " + error.Message,
                result.InputMayHaveBeenSent, result.Data);
            completed.TryAdd(request.IdempotencyKey, (body, uncertain));
            return uncertain;
        }
    }

    private async Task<OperationResult> DispatchReadAsync(ControlRequest request, CancellationToken serverToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var remaining = request.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) deadline.Cancel(); else deadline.CancelAfter(remaining);
        var requestKey = (request.SessionId, request.RequestId);
        lock (cancellationGate)
        {
            PruneCancellations(DateTimeOffset.UtcNow);
            if (!activeRequests.TryAdd(requestKey, deadline))
                return new(request.RequestId, "error", "Request ID is already running in this session.");
            if (cancelledRequests.ContainsKey(requestKey)) deadline.Cancel();
        }
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            return await dispatcher.DispatchAsync(request, deadline.Token);
        }
        catch (AutomationException error) { return new(request.RequestId, "blocked", error.Message); }
        catch (OperationCanceledException error) { return new(request.RequestId, "cancelled", error.Message); }
        catch (Exception error) { return new(request.RequestId, "error", error.Message); }
        finally { lock (cancellationGate) activeRequests.Remove(requestKey); }
    }

    public string SessionNonce => sessionNonce;

    private void PruneCancellations(DateTimeOffset now)
    {
        foreach (var key in cancelledRequests.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            cancelledRequests.Remove(key);
    }

    public static bool MayMutate(string action) => ControlActions.MayMutate(action);
}

public sealed class ControlDispatcher : IControlDispatcher
{
    private readonly Func<ControlRequest, CancellationToken, Task<OperationResult>> handler;
    public ControlDispatcher(Func<ControlRequest, CancellationToken, Task<OperationResult>> handler) => this.handler = handler;
    public Task<OperationResult> DispatchAsync(ControlRequest request, CancellationToken cancellationToken) => handler(request, cancellationToken);
}
