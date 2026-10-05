using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MisaManager.Core;

namespace MisaManager.App;

/// <summary>Formats captured failure evidence for the popup, saved history and developer report.</summary>
internal static partial class ErrorPrompt
{
    public static string? Code(string message, string details)
    {
        var (_, _, data) = Parse(details);
        return Text(Property(Object(data, "snapshot"), "LastErrorCode"))
            ?? Text(Property(Object(data, "verification"), "ErrorCode"))
            ?? Text(Property(Object(data, "error"), "code", "Code")) ?? ErrorHints.FindCode(message, details);
    }

    /// <summary>Renders the fields needed to diagnose a failed run without opening the zip report.</summary>
    public static string DiagnosticSummary(string message, string details,
        IEnumerable<PerformanceTraceEntry> steps)
    {
        var (chain, lastMessage, data) = Parse(details);
        var lines = new List<string>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{label}: {value.Trim()}");
        }

        var snapshot = Object(data, "snapshot");
        var verification = Object(data, "verification");
        var error = Object(data, "error");
        var diagnostics = Object(data, "diagnostics");
        Add("Mã lỗi", Code(message, details) ?? "không có");
        Add("Checkpoint sau xử lý lỗi", EnumText<WorkflowStatus>(Property(snapshot, "Status")));
        Add("Checkpoint lúc lỗi", Text(Property(diagnostics, "checkpointAtFailure")));
        Add("Bước thất bại", Text(Property(error, "failedStep")) ?? Text(Property(error, "FailedStep")));
        Add("Dòng đang xử lý", Number(Property(diagnostics, "activeLine")) ?? Number(Property(snapshot, "ActiveLine"), oneBased: true));
        Add("Phiên chạy", Text(Property(snapshot, "RunId")));
        Add("Thông báo exception", lastMessage ?? message);
        foreach (var cause in Causes(message, details)) Add("Nguyên nhân ghi nhận", cause);

        var disposition = EnumText<InputDisposition>(Property(snapshot, "LastInputDisposition"));
        var sent = Bool(Property(snapshot, "InputMayHaveBeenSent"));
        var pending = Bool(Property(snapshot, "InputPending"));
        if (disposition is not null || sent is not null || pending is not null)
            Add("An toàn nhập liệu", $"InputDisposition={disposition ?? "không rõ"}; "
                + $"InputMayHaveBeenSent={sent?.ToString() ?? "không rõ"}; InputPending={pending?.ToString() ?? "không rõ"}");
        Add("Thao tác chờ xác nhận", Text(Property(snapshot, "PendingAction")));
        if (disposition == nameof(InputDisposition.Uncertain)
            || EnumText<WorkflowStatus>(Property(snapshot, "Status")) is nameof(WorkflowStatus.InputUncertain) or nameof(WorkflowStatus.SaveUncertain))
            Add("Guard", "Không tự gửi lại input khi kết quả chưa rõ; cần xác minh chứng từ trong MISA.");

        var runtime = Object(snapshot, "RuntimeIdentity") ?? Object(verification, "RuntimeIdentity");
        if (runtime is not null)
            Add("Runtime", JoinParts(
                Text(Property(runtime, "AppVersion")), Text(Property(runtime, "SourceRevision")), Text(Property(runtime, "ProfileRevision"))));
        Add("Cửa sổ MISA", Text(Property(snapshot, "WindowIdentity")));

        var expected = Object(data, "expected");
        var activeLine = Object(expected, "activeLine");
        if (activeLine is not null)
            Add("Excel dòng hàng", JoinParts(
                $"mã={Text(Property(activeLine, "ItemCode")) ?? "(trống)"}",
                $"tên={Text(Property(activeLine, "Name")) ?? "(trống)"}",
                $"ĐVT={Text(Property(activeLine, "Unit")) ?? "(trống)"}"));
        var source = Object(expected, "Source");
        if (source is not null) Add("Nguồn hóa đơn", source.ToJsonString());
        var resolution = Object(diagnostics, "itemResolution");
        if (resolution is not null)
        {
            Add("Nguồn mã đã chọn", Text(Property(resolution, "CodeSource")));
            Add("Mã đã chọn", Text(Property(resolution, "ResolvedCode")) ?? "chưa chọn được");
            Add("MISA đọc lại", JoinParts("mã=" + (Text(Property(resolution, "NativeCode")) ?? "chưa đọc"),
                "tên=" + (Text(Property(resolution, "NativeName")) ?? "chưa đọc"),
                "ĐVT=" + (Text(Property(resolution, "NativeUnit")) ?? "chưa đọc")));
        }
        var catalog = Object(diagnostics, "catalog");
        Add("File danh mục", Text(Property(catalog, "SourceFile")));
        Add("Khóa tên dùng để tra", Text(Property(catalog, "matchKey")));
        if (Property(catalog, "candidates") is JsonArray candidates)
        {
            Add("Số mã khớp tên", candidates.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var candidate in candidates.OfType<JsonObject>().Take(12))
                Add("  Mã ứng viên", JoinParts(Text(Property(candidate, "Code")), Text(Property(candidate, "Name")),
                    "ĐVT=" + Text(Property(candidate, "Unit")), "chuyển đổi=" + Text(Property(candidate, "ConversionUnit")),
                    "nguồn=" + Text(Property(candidate, "origin"))));
            if (candidates.Count > 12) Add("  Còn lại", "Xem đầy đủ trong Copy lỗi / báo cáo ZIP.");
        }

        var mismatches = Mismatches(data).Take(8).Select(value =>
            $"{Text(Property(value, "Field")) ?? "trường"}: Excel='{Text(Property(value, "Expected")) ?? ""}', MISA='{Text(Property(value, "Actual")) ?? ""}'").ToArray();
        if (mismatches.Length > 0)
        {
            lines.Add("Đối chiếu lệch:");
            lines.AddRange(mismatches.Select(value => "  • " + value));
        }

        var frames = SourceFrames(chain).Take(8).ToArray();
        if (frames.Length > 0)
        {
            lines.Add("Vị trí mã nguồn:");
            lines.AddRange(frames.Select(frame => "  • " + frame));
        }
        else Add("Vị trí mã nguồn", "Không có file:dòng trong bằng chứng đã lưu; xem exception và mã lỗi trong Copy lỗi.");
        Add("Cách xử lý", ErrorHints.For(Code(message, details)));
        if (Property(data, "steps") is JsonArray captured && captured.Count > 0)
        {
            lines.Add("Timeline tại thời điểm lỗi:");
            lines.AddRange(captured.OfType<JsonObject>().TakeLast(12).Select(step =>
                $"  • {Text(Property(step, "Step"))} | {Text(Property(step, "Outcome"))} | {Text(Property(step, "StartedAt"))}"));
        }
        var timeline = steps.TakeLast(12).ToArray();
        if (timeline.Length > 0)
        {
            lines.Add("Timeline gần lỗi:");
            lines.AddRange(timeline.Select(step => $"  • {step.Name} | {step.Status} | {step.StartedDisplay}"
                + (string.IsNullOrWhiteSpace(step.Error) ? "" : " | " + step.Error)));
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>What went wrong, in plain words: inner exception messages and mismatched fields, no JSON or stack trace.</summary>
    public static IReadOnlyList<string> Causes(string message, string details)
    {
        var (chain, lastMessage, data) = Parse(details);
        var causes = new List<string>();
        void Add(string? text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || text == message.Trim() || causes.Contains(text)) return;
            causes.Add(text);
        }
        Add(lastMessage);
        foreach (Match match in ExceptionMessage().Matches(chain)) Add(match.Groups["message"].Value);
        if (data is null && causes.Count == 0 && !chain.Contains(" at ", StringComparison.Ordinal))
            foreach (var line in chain.Split('\n').Take(6)) Add(line);
        foreach (var value in Mismatches(data).Take(5))
            Add($"Lệch {value["Field"]}: Excel '{value["Expected"]}', MISA '{value["Actual"]}'");
        return causes;
    }

    public static string Build(string message, string details, DateTimeOffset occurredAt, string invoice,
        IEnumerable<PerformanceTraceEntry> steps)
    {
        var (chain, _, data) = Parse(details);
        var code = Code(message, details);
        var snapshot = Object(data, "snapshot");
        var revision = Text(Property(Object(snapshot, "RuntimeIdentity"), "SourceRevision")) ?? ProductIdentity.SourceRevision;
        var recent = steps.ToArray();
        var text = new StringBuilder()
            .AppendLine($"MISA Manager {revision} · lỗi khi thêm hóa đơn vào MISA")
            .AppendLine($"Hóa đơn: {invoice} · lúc {occurredAt.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)}")
            .AppendLine($"Mã lỗi: {code ?? "(không có)"}")
            .AppendLine("Lỗi: " + message);
        foreach (var cause in Causes(message, details)) text.AppendLine("- " + cause);
        text.AppendLine().AppendLine("CHẨN ĐOÁN CHI TIẾT").AppendLine(DiagnosticSummary(message, details, recent));
        text.AppendLine()
            .AppendLine("YÊU CẦU: sửa lỗi này trong repo MisaManager (đọc AGENTS.md trước).")
            .AppendLine("1. Tìm nguyên nhân gốc từ mã lỗi, chuỗi exception và file:dòng trong stack trace.")
            .AppendLine("2. Tái hiện bằng kịch bản MISA giả lập (tests/MisaManager.FakeMisa) và test trong tests/MisaManager.Integration.Tests, rồi mới sửa.")
            .AppendLine("3. Giữ guard an toàn: không tự gửi lại input khi InputUncertain/SaveUncertain, readback mọi input, selector chỉ khai báo trong profile.")
            .AppendLine("4. Trả lời ngắn: nguyên nhân, chỗ sửa, cách kiểm chứng.");
        var stack = chain.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).ToArray();
        if (stack.Length > 0)
        {
            text.AppendLine().AppendLine("STACK TRACE");
            foreach (var line in stack) text.AppendLine(line);
        }
        if (recent.Length > 0)
        {
            text.AppendLine().AppendLine("CÁC BƯỚC TRƯỚC LỖI (cũ → mới)");
            foreach (var step in recent)
                text.AppendLine($"{step.StartedDisplay} | {step.Name} | {step.Status} | {step.DurationDisplay} | {step.Details}"
                    + (string.IsNullOrWhiteSpace(step.Error) ? "" : " | " + step.Error));
        }
        if (data is not null)
            text.AppendLine().AppendLine("DỮ LIỆU KỸ THUẬT ĐẦY ĐỦ").AppendLine(data.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        return text.ToString();
    }

    private static (string Chain, string? LastMessage, JsonObject? Data) Parse(string details)
    {
        JsonObject? data = null;
        try
        {
            var parsed = JsonNode.Parse(details);
            data = parsed as JsonObject;
            if (data is null && Text(parsed) is { } decoded) return (decoded, null, null);
        }
        catch (JsonException) { }
        if (data is null) return (details, null, null);
        var snapshot = Object(data, "snapshot");
        var error = Object(data, "error");
        var chain = string.Join(Environment.NewLine, new[] { Text(Property(error, "chain", "Chain")),
            Text(Property(snapshot, "ErrorChain")), Text(Property(Object(data, "verification"), "ErrorChain")),
            Text(Property(data, "error")) }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
        return (chain, Text(Property(snapshot, "LastMessage")) ?? Text(Property(error, "message")), data);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonNode? Property(JsonObject? value, params string[] names)
    {
        if (value is null) return null;
        foreach (var name in names)
        {
            if (value.TryGetPropertyValue(name, out var node) && node is not null) return node;
            var actual = value.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
            if (actual.Value is not null) return actual.Value;
        }
        return null;
    }

    private static JsonObject? Object(JsonObject? value, string name) => Property(value, name, char.ToUpperInvariant(name[0]) + name[1..]) as JsonObject;

    private static bool? Bool(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;

    private static string? Number(JsonNode? node, bool oneBased = false)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? (oneBased ? (number + 1).ToString(CultureInfo.InvariantCulture) : number.ToString(CultureInfo.InvariantCulture)) : null;

    private static string JoinParts(params string?[] values) => string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string? EnumText<T>(JsonNode? node) where T : struct, Enum
    {
        if (Text(node) is { } text)
        {
            var normalized = string.Concat(text.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
            return Enum.TryParse<T>(normalized, true, out var parsed) ? parsed.ToString() : text;
        }
        return node is JsonValue value && value.TryGetValue<int>(out var number)
            ? Enum.ToObject(typeof(T), number).ToString() : null;
    }

    public static IReadOnlyList<string> SourceFrames(string chain)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var frames = new List<string>();
        foreach (Match match in SourceFrame().Matches(chain))
        {
            var frame = match.Value.Trim();
            if (seen.Add(frame)) frames.Add(frame);
        }
        return frames;
    }

    private static IEnumerable<JsonObject> Mismatches(JsonObject? data)
    {
        var verification = Object(data, "verification");
        var comparison = Object(verification, "comparison");
        var values = Property(comparison, "Values") as JsonArray ?? [];
        return values.OfType<JsonObject>().Where(value => Bool(Property(value, "Matched")) == false);
    }

    [GeneratedRegex(@"(?:^|--->\s*)[\w.`]+(?:Exception|Error): (?<message>[^\r\n]+)", RegexOptions.Multiline)]
    private static partial Regex ExceptionMessage();

    [GeneratedRegex(@"at\s+.+?(?:in\s+)?(?<file>[A-Za-z]:\\[^\r\n:]+|[^\r\n:]+\.cs):line\s+(?<line>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceFrame();
}
