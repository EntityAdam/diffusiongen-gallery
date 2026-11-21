using System.Security.Cryptography;

namespace Gallery.Core.Features.Vaults.Security;

internal static class VaultEnvelopeValidator
{
    public static void Validate(ReadOnlySpan<byte> encryptedEnvelopeBytes, string source)
    {
        if (!VaultCrypto.IsValidEnvelopeFormat(encryptedEnvelopeBytes))
            throw new CryptographicException($"Invalid encrypted file format from {source}.");
    }
}
