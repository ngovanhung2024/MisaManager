using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MisaManager.Core;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented = true)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = indented
        };
        options.Converters.Add(new DecimalStringConverter());
        options.Converters.Add(new NativeHandleConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

public sealed class NativeHandleConverter : JsonConverter<nint>
{
    public override nint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => checked((nint)reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, nint value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.ToInt64());
}

public sealed partial class DecimalStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !DecimalValue.TryParseInvariant(reader.GetString()!, out var value))
            throw new JsonException("Accounting decimal must be an exact invariant decimal string (max 24 digits, scale 8).");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

public static partial class DecimalValue
{
    [GeneratedRegex(@"^-?[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex InvariantPattern();

    public static bool TryParseInvariant(string raw, out decimal value)
    {
        value = 0;
        if (!InvariantPattern().IsMatch(raw)) return false;
        var unsigned = raw.TrimStart('-');
        var parts = unsigned.Split('.');
        var scale = parts.Length == 2 ? parts[1].Length : 0;
        var digits = unsigned.Replace(".", "", StringComparison.Ordinal).TrimStart('0').Length;
        if (scale > 8 || digits > 24) return false;
        return decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value);
    }
}
