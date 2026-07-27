using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parallels.Contracts;

/// <summary>
/// The single serializer configuration used on every hop.
///
/// The API's HTTP binding, the <c>--job</c> argument handed to a backtest
/// process, and the live worker's environment variable all use these exact
/// options. If they differed, a job could validate in the API and then fail to
/// deserialize inside the worker — the failure mode that having one typed
/// representation (spec 3.5) is meant to rule out.
/// </summary>
public static class ParallelsJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Environment variable the live worker reads its desired state from (spec 3.5).</summary>
    public const string LiveConfigEnvVar = "PARALLELS_LIVE_CONFIG";

    /// <summary>Optional file path for running a backtest by hand, without the API. Never used by the API itself.</summary>
    public const string JobConfigPathEnvVar = "JOB_CONFIG_PATH";

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new DateOnlyJsonConverter());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>
/// ISO-8601 date-only round-tripping. System.Text.Json handles DateOnly
/// natively, but pinning the format explicitly keeps the JSON stable for the
/// TypeScript side, which parses these as plain "YYYY-MM-DD" strings.
/// </summary>
public sealed class DateOnlyJsonConverter : JsonConverter<DateOnly>
{
    private const string Format = "yyyy-MM-dd";

    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new JsonException("Expected a yyyy-MM-dd date string.");
        return DateOnly.ParseExact(text, Format, System.Globalization.CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(Format, System.Globalization.CultureInfo.InvariantCulture));
}
