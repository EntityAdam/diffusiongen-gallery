using System.Security.Cryptography;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Media;

internal static class StoredMediaProcessor
{
    internal const string EncryptedPngExtension = ".dpng";
    internal const string EncryptedMp4Extension = ".dmp4";

    public static string RelativePath(ImageRecord record)
    {
        if (record.Id.Length < 4 || record.Id.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidOperationException("The gallery contains an invalid media identifier.");
        return Path.Combine("images", record.Id[..2], record.Id[2..4],
            record.Id + (record.IsVideo ? EncryptedMp4Extension : EncryptedPngExtension));
    }

    public static Task<byte[]> ReadAsync(ImageRecord record, byte[] key) =>
        ReadAsync(record.StoredPath, record.Id, key);

    public static async Task<byte[]> ReadAsync(string path, string imageId, byte[] key)
    {
        var envelope = await File.ReadAllBytesAsync(path);
        VaultEnvelopeValidator.Validate(envelope, $"stored media '{imageId}'");
        return VaultCrypto.Decrypt(envelope, key, $"image:{imageId}");
    }

    public static (ImageRecord Record, byte[] Encrypted, byte[] EncryptedThumb) EncryptImport(
        ImageRecord record, ReadOnlySpan<byte> content, ReadOnlySpan<byte> thumbnail, byte[] key) =>
        (record, VaultCrypto.Encrypt(content, key, $"image:{record.Id}"),
            VaultCrypto.Encrypt(thumbnail, key, $"thumbnail:{record.Id}"));

    internal static async Task VerifyCiphertextAsync(string path, string imageId, byte[] key)
    {
        var plain = await ReadAsync(path, imageId, key);
        CryptographicOperations.ZeroMemory(plain);
    }
}
