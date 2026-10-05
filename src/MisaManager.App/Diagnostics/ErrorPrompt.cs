using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MisaManager.Core;

namespace MisaManager.App;

/// <summary>Turns a workflow error into short lines for the popup and a compact prompt to paste into an AI assistant.</summary>
internal static partial class ErrorPrompt
{
    private const int MaxStackLines = 40;

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
        var code = ErrorHints.FindCode(chain, message, details);
        var snapshot = data?["snapshot"] as JsonObject;
        var text = new StringBuilder()
            .AppendLine($"MISA Manager {ProductIdentity.SourceRevision} · lỗi khi thêm hóa đơn vào MISA")
            .AppendLine($"Hóa đơn: {invoice} · lúc {occurredAt.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)}")
            .AppendLine($"Mã lỗi: {code ?? "(không có)"}" + (snapshot?["Status"] is { } status ? $" · checkpoint: {(WorkflowStatus)status.GetValue<int>()}" : ""))
            .AppendLine("Lỗi: " + message);
        foreach (var cause in Causes(message, details)) text.AppendLine("- " + cause);
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
            foreach (var line in stack.Take(MaxStackLines)) text.AppendLine(line);
            if (stack.Length > MaxStackLines) text.AppendLine($"... (+{stack.Length - MaxStackLines} dòng)");
        }
        var recent = steps.TakeLast(15).ToArray();
        if (recent.Length > 0)
        {
            text.AppendLine().AppendLine("CÁC BƯỚC TRƯỚC LỖI (cũ → mới)");
            foreach (var step in recent)
                text.AppendLine($"{step.Name} | {step.Status}{(string.IsNullOrWhiteSpace(step.Error) ? "" : " | " + step.Error)}");
        }
        if (data is not null)
            text.AppendLine().AppendLine("DỮ LIỆU KỸ THUẬT (rút gọn)").AppendLine(Compact(data));
        return text.ToString();
    }

    private static (string Chain, string? LastMessage, JsonObject? Data) Parse(string details)
    {
        JsonObject? data = null;
        try { data = JsonNode.Parse(details) as JsonObject; }
        catch (JsonException) { }
        if (data is null) return (details, null, null);
        var snapshot = data["snapshot"] as JsonObject;
        var chain = Text(data["error"]) ?? Text(snapshot?["ErrorChain"]) ?? "";
        return (chain, Text(snapshot?["LastMessage"]), data);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IEnumerable<JsonObject> Mismatches(JsonObject? data)
        => (data?["verification"]?["Comparison"]?["Values"] as JsonArray ?? [])
            .OfType<JsonObject>().Where(value => value["Matched"] is JsonValue matched && matched.TryGetValue<bool>(out var ok) && !ok);

    // Drops what the prompt already shows (error chain, native frame) and every matched comparison value.
    private static string Compact(JsonObject data)
    {
        var copy = (JsonObject)data.DeepClone();
        copy.Remove("error");
        if (copy["snapshot"] is JsonObject snapshot) { snapshot.Remove("ErrorChain"); snapshot.Remove("NativeFrame"); }
        if (copy["verification"] is JsonObject verification)
        {
            verification.Remove("ErrorChain");
            verification.Remove("Error");
            if (verification["Comparison"] is JsonObject comparison)
                comparison["Values"] = new JsonArray([.. Mismatches(data).Select(value => value.DeepClone())]);
        }
        return copy.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    [GeneratedRegex(@"(?:^|--->\s*)[\w.`]+(?:Exception|Error): (?<message>[^\r\n]+)", RegexOptions.Multiline)]
    private static partial Regex ExceptionMessage();
}
