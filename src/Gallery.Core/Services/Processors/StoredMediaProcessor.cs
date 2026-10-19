using Gallery.Core.Services.Processors.Validators;
using Gallery.Core.Services.Processors.Codecs;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class StoredMediaProcessor
{
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
}
