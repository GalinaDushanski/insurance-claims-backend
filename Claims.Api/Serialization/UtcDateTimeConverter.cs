using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claims.Serialization;

// Parse explicit offsets before DateTime can convert them to the server's local time.
public class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected an ISO 8601 date string.");

        if (reader.TryGetDateTime(out var date) && date.Kind == DateTimeKind.Unspecified)
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);

        if (reader.TryGetDateTimeOffset(out var timestamp)) return timestamp.UtcDateTime;
        throw new JsonException("Expected an ISO 8601 date string.");
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
