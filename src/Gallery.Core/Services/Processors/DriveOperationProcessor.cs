using Gallery.Core.Services.Processors.Validators;
using Gallery.Core.Services.Processors.Codecs;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Services.DataManagers.Models;

namespace Gallery.Core.Services.Processors;

internal static class DriveOperationProcessor
{
    public static byte[] PathFingerprint(byte[] key, string path) => VaultCrypto.PathFingerprint(key, path);

    public static NewDriveOperation Create(string kind, string path, string? imageId, byte[] key, bool operationIsRecursive = false)
    {
        var target = VaultCrypto.PathFingerprint(key, path);
        var payload = ProtectPayload(new DriveOperationPayload(path, operationIsRecursive), key, PayloadPurpose(kind, target));
        return new(kind, target, imageId, payload);
    }

    public static DriveOperationPayload Read(DriveOperation driveOperation, byte[] key)
    {
        VaultEnvelopeValidator.Validate(driveOperation.Payload.AsSpan(), $"drive operation payload '{driveOperation.Sequence}'");
        return UnprotectPayload(driveOperation.Payload.AsSpan(), key, PayloadPurpose(driveOperation.Kind, driveOperation.Target.AsSpan()));
    }

    private static string PayloadPurpose(string kind, ReadOnlySpan<byte> target) =>
        $"drive-operation:{kind}:{Convert.ToHexString(target)}";

    private static byte[] ProtectPayload(DriveOperationPayload driveOperationPayload, byte[] key, string purpose)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(driveOperationPayload);
        try { return VaultCrypto.Encrypt(plaintext, key, purpose); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static DriveOperationPayload UnprotectPayload(ReadOnlySpan<byte> payload, byte[] key, string purpose)
    {
        var plaintext = VaultCrypto.Decrypt(payload, key, purpose);
        try { return JsonSerializer.Deserialize<DriveOperationPayload>(plaintext) ?? throw new InvalidOperationException("Drive operation payload is empty."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
