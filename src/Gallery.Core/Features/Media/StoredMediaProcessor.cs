using System.Security.Cryptography;
using Gallery.Core.Features.Ingestion;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Media;

internal static class StoredMediaProcessor
{
    internal const string EncryptedPngExtension = ".dpng";
    internal const string EncryptedMp4Extension = ".dmp4";
    private const long MaximumUnclassifiedEnvelopeBytes =
        ImportMediaPreparer.MaxVideoBytes + VaultCrypto.EnvelopeHeaderSizeBytes;

    public static string RelativePath(ImageRecord record)
    {
        if (!IsValidStorageIdentifier(record.Id))
            throw new InvalidOperationException("The gallery contains an invalid media identifier.");
        return Path.Combine("images", record.Id[..2], record.Id[2..4],
            record.Id + (record.IsVideo ? EncryptedMp4Extension : EncryptedPngExtension));
    }

    private static bool IsValidStorageIdentifier(string imageId) =>
        imageId.Length >= 4 && imageId.All(char.IsAsciiHexDigit);

    public static Task<byte[]> ReadAsync(ImageRecord record, byte[] key) =>
        ReadAsync(record.StoredPath, record.Id, key,
            (record.IsVideo ? ImportMediaPreparer.MaxVideoBytes : ImportMediaPreparer.MaxFileBytes)
            + VaultCrypto.EnvelopeHeaderSizeBytes);

    public static Task<byte[]> ReadAsync(string path, string imageId, byte[] key) =>
        ReadAsync(path, imageId, key, MaximumUnclassifiedEnvelopeBytes);

    private static async Task<byte[]> ReadAsync(string path, string imageId, byte[] key, long maximumEnvelopeBytes)
    {
        await using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (fileStream.Length > maximumEnvelopeBytes)
            throw TooLarge(imageId);
        if (fileStream.Length < VaultCrypto.EnvelopeHeaderSizeBytes)
            VaultEnvelopeValidator.Validate(ReadOnlySpan<byte>.Empty, $"stored media '{imageId}'");

        var envelopeHeader = new byte[VaultCrypto.EnvelopeHeaderSizeBytes];
        try
        {
            await fileStream.ReadExactlyAsync(envelopeHeader);
            VaultEnvelopeValidator.Validate(envelopeHeader, $"stored media '{imageId}'");

            var envelopeLength = fileStream.Length;
            if (envelopeLength > maximumEnvelopeBytes) throw TooLarge(imageId);
            if (envelopeLength < VaultCrypto.EnvelopeHeaderSizeBytes)
                VaultEnvelopeValidator.Validate(ReadOnlySpan<byte>.Empty, $"stored media '{imageId}'");
            var encryptedEnvelopeBytes = new byte[checked((int)envelopeLength)];
            try
            {
                envelopeHeader.CopyTo(encryptedEnvelopeBytes, 0);
                await fileStream.ReadExactlyAsync(encryptedEnvelopeBytes.AsMemory(envelopeHeader.Length));
                if (await fileStream.ReadAsync(new byte[1]) != 0) throw TooLarge(imageId);
                return VaultCrypto.Decrypt(encryptedEnvelopeBytes, key, $"image:{imageId}");
            }
            finally { CryptographicOperations.ZeroMemory(encryptedEnvelopeBytes); }
        }
        finally { CryptographicOperations.ZeroMemory(envelopeHeader); }
    }

    private static InvalidDataException TooLarge(string imageId) =>
        new($"Stored media '{imageId}' exceeds the supported size.");

    public static (byte[] EncryptedMedia, byte[] EncryptedThumbnail) EncryptImport(
        ImageRecord record, ReadOnlySpan<byte> content, ReadOnlySpan<byte> thumbnail, byte[] key) =>
        (VaultCrypto.Encrypt(content, key, $"image:{record.Id}"),
            VaultCrypto.Encrypt(thumbnail, key, $"thumbnail:{record.Id}"));

    internal static async Task VerifyCiphertextAsync(string path, string imageId, byte[] key)
    {
        var plain = await ReadAsync(path, imageId, key);
        CryptographicOperations.ZeroMemory(plain);
    }
}
