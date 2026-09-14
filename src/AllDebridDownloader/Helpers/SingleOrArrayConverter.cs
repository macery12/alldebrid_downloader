using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllDebridDownloader.Helpers;

/// <summary>
/// Reads a property that AllDebrid sends as either a JSON array or, when there is only
/// one of something, a bare object.
///
/// This is not in the documentation but is real: /v4.1/magnet/status in live mode returns
///     "magnets": [ {...}, {...} ]      on a fullsync
///     "magnets": { "id": 1, ... }      on a delta carrying a single changed magnet
/// Verified against the live API. A known-working older client carried the same
/// dict-or-list tolerance, so treat it as the API's actual contract.
/// </summary>
public sealed class SingleOrArrayConverter<T> : JsonConverter<List<T>>
{
    public override List<T>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.StartArray:
                var list = new List<T>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) return list;

                    var element = JsonSerializer.Deserialize<T>(ref reader, options);
                    if (element is not null) list.Add(element);
                }
                throw new JsonException("Unterminated array.");

            case JsonTokenType.StartObject:
                var single = JsonSerializer.Deserialize<T>(ref reader, options);
                return single is null ? new List<T>() : [single];

            default:
                throw new JsonException(
                    "Expected an array or an object, got " + reader.TokenType + ".");
        }
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}
