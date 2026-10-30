using System.Security.Cryptography;

namespace Gallery.Core.Services.Processors.Validators;

internal static class VaultEnvelopeValidator
{
    private const int EnvelopeHeaderSizeBytes = 36;
    private const int MagicSizeBytes = 8;
    private static ReadOnlySpan<byte> Magic => "DPNG0001"u8;
    public static void Validate(ReadOnlySpan<byte> encryptedEnvelopeBytes, string source)
    {
        if (!HasValidEnvelopeFormat(encryptedEnvelopeBytes))
            throw new CryptographicException($"Invalid encrypted file format from {source}.");
    }

    private static bool HasValidEnvelopeFormat(ReadOnlySpan<byte> encryptedEnvelopeBytes) =>
        encryptedEnvelopeBytes.Length >= EnvelopeHeaderSizeBytes
        && encryptedEnvelopeBytes[..MagicSizeBytes].SequenceEqual(Magic);

}
