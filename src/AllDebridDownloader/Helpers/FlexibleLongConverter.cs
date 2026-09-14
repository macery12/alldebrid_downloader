using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllDebridDownloader.Helpers;

/// <summary>
/// AllDebrid is inconsistent about magnet ids: /v4.1/magnet/status returns them as JSON
/// numbers (123456) while /v4/magnet/files returns them as JSON strings ("123").
/// This accepts either, so one model type covers both endpoints.
/// </summary>
public sealed class FlexibleLongConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetInt64();

            case JsonTokenType.String:
                var s = reader.GetString();
                if (long.TryParse(s, out var parsed)) return parsed;
                // Tolerate a float-ish string rather than throwing on odd data.
                if (double.TryParse(s, out var d)) return (long)d;
                throw new JsonException("Expected a number or numeric string, got \"" + s + "\".");

            case JsonTokenType.Null:
                return 0;

            case JsonTokenType.True:
                return 1;

            case JsonTokenType.False:
                return 0;

            default:
                throw new JsonException("Expected a number or numeric string, got " + reader.TokenType + ".");
        }
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>Nullable counterpart of <see cref="FlexibleLongConverter"/>.</summary>
public sealed class FlexibleNullableLongConverter : JsonConverter<long?>
{
    private static readonly FlexibleLongConverter Inner = new();

    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        return Inner.Read(ref reader, typeof(long), options);
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}

/// <summary>
/// Accepts a bool that may arrive as true/false, "true"/"false", or 1/0.
/// The API has been seen to use the string form for the "demo" flag.
/// </summary>
public sealed class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.GetInt64() != 0,
            JsonTokenType.Null => false,
            JsonTokenType.String => bool.TryParse(reader.GetString(), out var b)
                ? b
                : reader.GetString() == "1",
            _ => throw new JsonException("Expected a boolean, got " + reader.TokenType + ".")
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}
