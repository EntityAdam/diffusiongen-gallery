using Gallery.Core.Services.Processors.Validators;
using Gallery.Core.Services.Processors.Codecs;
using System.Security.Cryptography;
using System.Text.Json;

namespace Gallery.Core.Services.Processors;

internal static class ArchiveManifestProcessor
{
    private const string Purpose = "backup-manifest:v1";

    public static byte[] Protect<T>(T manifest, byte[] key) =>
        EncryptAndZero(JsonSerializer.SerializeToUtf8Bytes(manifest), key);

    public static T Read<T>(ReadOnlySpan<byte> envelope, byte[] key)
    {
        VaultEnvelopeValidator.Validate(envelope, "backup manifest");
        return DeserializeAndZero<T>(VaultCrypto.Decrypt(envelope, key, Purpose));
    }

    private static byte[] EncryptAndZero(byte[] plaintext, byte[] key)
    {
        try { return VaultCrypto.Encrypt(plaintext, key, Purpose); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static T DeserializeAndZero<T>(byte[] plaintext)
    {
        try { return JsonSerializer.Deserialize<T>(plaintext) ?? throw new InvalidOperationException("Backup manifest is empty."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
