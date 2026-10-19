using Gallery.Core.Services.Processors.Validators;
using Gallery.Core.Services.Processors.Codecs;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Services.DataManagers.Models;

namespace Gallery.Core.Services.Processors;

internal static class DriveOperationProcessor
{
    public static byte[] PathFingerprint(byte[] key, string path) => VaultCrypto.PathFingerprint(key, path);

    public static NewDriveOperation Create(string kind, string path, string? imageId, byte[] key, bool recursive = false)
    {
        var target = VaultCrypto.PathFingerprint(key, path);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new DriveOperationPayload(path, recursive));
        try
        {
            var payload = VaultCrypto.Encrypt(plaintext, key, $"drive-operation:{kind}:{Convert.ToHexString(target)}");
            return new(kind, target, imageId, payload);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static DriveOperationPayload Read(DriveOperation operation, byte[] key)
    {
        VaultEnvelopeValidator.Validate(operation.Payload.AsSpan(), $"drive operation payload '{operation.Sequence}'");
        var plaintext = VaultCrypto.Decrypt(operation.Payload.AsSpan(), key,
            $"drive-operation:{operation.Kind}:{Convert.ToHexString(operation.Target.AsSpan())}");
        try { return JsonSerializer.Deserialize<DriveOperationPayload>(plaintext) ?? throw new InvalidOperationException("Drive operation payload is empty."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
