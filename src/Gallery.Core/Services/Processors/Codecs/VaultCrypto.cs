using Gallery.Core.Models;
using System.Security.Cryptography;
using System.Text;

namespace Gallery.Core.Services.Processors.Codecs;

internal static class VaultCrypto
{
    public const int Iterations = 600_000;
    private const int NonceSizeBytes = 12;
    private const int AuthenticationTagSizeBytes = 16;
    private const int MagicSizeBytes = 8;
    internal const int EnvelopeHeaderSizeBytes = MagicSizeBytes + NonceSizeBytes + AuthenticationTagSizeBytes;
    private static readonly byte[] Magic = "DPNG0001"u8.ToArray();

    public static byte[] Derive(string secret, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] ContentFingerprint(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> sourceHash) =>
        ComputeKeyedFingerprint(masterKey, sourceHash, "DiffusionGenGallery/content-fingerprint/v1"u8);

    /// <summary>Keyed hash of a normalized full path, so drive lookups run in SQL without storing the path in plaintext.</summary>
    public static byte[] PathFingerprint(ReadOnlySpan<byte> masterKey, string path)
    {
        var normalizedPath = NormalizeFingerprintPath(path);
        return ComputePathFingerprint(masterKey, normalizedPath);
    }

    private static string NormalizeFingerprintPath(string path)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalizedPath.ToUpperInvariant() : normalizedPath;
    }

    private static byte[] ComputePathFingerprint(ReadOnlySpan<byte> masterKey, string normalizedPath) =>
        ComputeKeyedFingerprint(masterKey, Encoding.UTF8.GetBytes(normalizedPath),
            "DiffusionGenGallery/path-fingerprint/v1"u8);

    private static byte[] ComputeKeyedFingerprint(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> fingerprintInput,
        ReadOnlySpan<byte> fingerprintPurpose)
    {
        Span<byte> fingerprintKey = stackalloc byte[32];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, fingerprintKey,
                salt: ReadOnlySpan<byte>.Empty, info: fingerprintPurpose);
            return HMACSHA256.HashData(fingerprintKey, fingerprintInput);
        }
        finally { CryptographicOperations.ZeroMemory(fingerprintKey); }
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key, string purpose)
    {
        var encryptedEnvelopeBytes = AllocateEncryptedEnvelope(plain.Length);
        var nonce = FillEnvelopeNonce(encryptedEnvelopeBytes);
        EncryptPayload(plain, key, purpose, nonce, encryptedEnvelopeBytes);
        return encryptedEnvelopeBytes;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, string purpose)
    {
        // Preserve native fail-before-allocation safety even when called without an ingress owner.
        if (envelope.Length < EnvelopeHeaderSizeBytes || !envelope[..MagicSizeBytes].SequenceEqual(Magic))
            throw new CryptographicException("Invalid encrypted file format.");
        var plaintextBytes = new byte[envelope.Length - EnvelopeHeaderSizeBytes];
        try
        {
            DecryptPayload(envelope, key, purpose, plaintextBytes);
            return plaintextBytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            throw;
        }
    }

    // Each independently random compound word represents 8 bits: 32 words = 256 bits.
    public static string CreateRecoveryPhrase() => string.Join(' ',
        RandomNumberGenerator.GetBytes(RecoveryPhraseWords.Count)
            .Select(value => $"{RecoveryPhraseWords.Prefixes[value >> 4]}-{RecoveryPhraseWords.Suffixes[value & 15]}"));

    private static Span<byte> FillEnvelopeNonce(Span<byte> encryptedEnvelopeBytes)
    {
        var nonce = encryptedEnvelopeBytes.Slice(MagicSizeBytes, NonceSizeBytes);
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    private static byte[] AllocateEncryptedEnvelope(int plaintextLength)
    {
        var encryptedEnvelopeBytes = new byte[EnvelopeHeaderSizeBytes + plaintextLength];
        Magic.CopyTo(encryptedEnvelopeBytes, 0);
        return encryptedEnvelopeBytes;
    }

    private static void EncryptPayload(
        ReadOnlySpan<byte> plaintextBytes,
        ReadOnlySpan<byte> key,
        string purpose,
        ReadOnlySpan<byte> nonce,
        Span<byte> encryptedEnvelopeBytes)
    {
        using var aesGcm = new AesGcm(key, AuthenticationTagSizeBytes);
        aesGcm.Encrypt(
            nonce,
            plaintextBytes,
            encryptedEnvelopeBytes[EnvelopeHeaderSizeBytes..],
            encryptedEnvelopeBytes.Slice(MagicSizeBytes + NonceSizeBytes, AuthenticationTagSizeBytes),
            Encoding.UTF8.GetBytes(purpose));
    }

    private static void DecryptPayload(
        ReadOnlySpan<byte> encryptedEnvelopeBytes,
        ReadOnlySpan<byte> key,
        string purpose,
        Span<byte> plaintextBytes)
    {
        using var aesGcm = new AesGcm(key, AuthenticationTagSizeBytes);
        aesGcm.Decrypt(
            encryptedEnvelopeBytes.Slice(MagicSizeBytes, NonceSizeBytes),
            encryptedEnvelopeBytes[EnvelopeHeaderSizeBytes..],
            encryptedEnvelopeBytes.Slice(MagicSizeBytes + NonceSizeBytes, AuthenticationTagSizeBytes),
            plaintextBytes,
            Encoding.UTF8.GetBytes(purpose));
    }

}
