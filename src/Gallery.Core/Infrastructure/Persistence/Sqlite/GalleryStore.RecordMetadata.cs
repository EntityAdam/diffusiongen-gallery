using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence.Sqlite;

internal sealed partial class GalleryStore
{
    private static readonly JsonSerializerOptions CatalogRecordJson = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                type =>
                {
                    if (type.Type == typeof(ImageRecord))
                        type.Properties.Remove(type.Properties.Single(property => property.Name == nameof(ImageRecord.Metadata)));
                }
            }
        }
    };

    // Legacy SQLite records keep generation documents inside the authenticated record envelope.
    // Copy the existing JSON value through an edit without materializing it as a string.
    private static byte[] EncodeUpdatedRecord(ImageRecord record, byte[] previous, byte[] key)
    {
        var purpose = $"record:{record.Id}";
        VaultEnvelopeValidator.Validate(previous, $"stored value '{purpose}'");
        var original = VaultCrypto.Decrypt(previous, key, purpose);
        try
        {
            var metadata = ReadRawMetadata(original);
            var edited = JsonSerializer.SerializeToUtf8Bytes(record, CatalogRecordJson);
            try
            {
                if (metadata.IsEmpty) return VaultCrypto.Encrypt(edited, key, purpose);

                ReadOnlySpan<byte> property = ",\"Metadata\":"u8;
                var updated = new byte[edited.Length + property.Length + metadata.Length];
                try
                {
                    var closingBrace = edited.Length - 1;
                    edited.AsSpan(0, closingBrace).CopyTo(updated);
                    property.CopyTo(updated.AsSpan(closingBrace));
                    metadata.CopyTo(updated.AsSpan(closingBrace + property.Length));
                    updated[^1] = (byte)'}';
                    return VaultCrypto.Encrypt(updated, key, purpose);
                }
                finally { CryptographicOperations.ZeroMemory(updated); }
            }
            finally { CryptographicOperations.ZeroMemory(edited); }
        }
        finally { CryptographicOperations.ZeroMemory(original); }
    }

    private static ReadOnlySpan<byte> ReadRawMetadata(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                !reader.ValueTextEquals("Metadata"u8)) continue;
            if (!reader.Read()) throw new JsonException("The stored metadata value is missing.");
            var start = checked((int)reader.TokenStartIndex);
            reader.Skip();
            return json[start..checked((int)reader.BytesConsumed)];
        }
        return [];
    }
}
