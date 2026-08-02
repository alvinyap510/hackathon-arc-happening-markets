using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Venue.Api;

/// <summary>
/// System.Text.Json converter for BigInteger: serialized as a decimal string (the UI
/// carries amounts as strings to avoid float loss), deserialized from either a JSON
/// number or a string.
/// </summary>
public sealed class BigIntegerJsonConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return BigInteger.Parse(System.Text.Encoding.UTF8.GetString(reader.ValueSpan));
        var s = reader.GetString();
        return string.IsNullOrWhiteSpace(s) ? BigInteger.Zero : BigInteger.Parse(s);
    }

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
