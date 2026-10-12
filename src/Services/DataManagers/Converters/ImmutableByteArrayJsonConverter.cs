using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gallery.Services.DataManagers.Converters;

internal sealed class ImmutableByteArrayJsonConverter : JsonConverter<ImmutableArray<byte>>
{
    public override ImmutableArray<byte> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Vault byte data must be base64-encoded.");
        return ImmutableArray.CreateRange(reader.GetBytesFromBase64());
    }

    public override void Write(
        Utf8JsonWriter writer,
        ImmutableArray<byte> immutableBytes,
        JsonSerializerOptions options)
    {
        if (immutableBytes.IsDefault)
            throw new JsonException("Vault byte data cannot be uninitialized.");
        writer.WriteBase64StringValue(immutableBytes.AsSpan());
    }
}
