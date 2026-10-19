using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gallery.Core.Services.DataManagers.Converters;

internal sealed class ImmutableByteArrayJsonConverter : JsonConverter<ImmutableArray<byte>>
{
    public override ImmutableArray<byte> Read(
        ref Utf8JsonReader utf8JsonReader,
        Type type,
        JsonSerializerOptions jsonSerializerOptions)
    {
        if (utf8JsonReader.TokenType != JsonTokenType.String)
            throw new JsonException("Gallery byte data must be base64-encoded.");
        return ImmutableArray.CreateRange(utf8JsonReader.GetBytesFromBase64());
    }

    public override void Write(
        Utf8JsonWriter utf8JsonWriter,
        ImmutableArray<byte> immutableBytes,
        JsonSerializerOptions jsonSerializerOptions)
    {
        if (immutableBytes.IsDefault)
            throw new JsonException("Gallery byte data cannot be uninitialized.");
        utf8JsonWriter.WriteBase64StringValue(immutableBytes.AsSpan());
    }
}
