using System.Security.Cryptography;
using System.Text;

namespace Gallery.Services.Processors;

internal static class VaultCrypto
{
    public const int Iterations = 600_000;
    private const int NonceSizeBytes = 12;
    private const int AuthenticationTagSizeBytes = 16;
    private const int MagicSizeBytes = 8;
    private const int EnvelopeHeaderSizeBytes = MagicSizeBytes + NonceSizeBytes + AuthenticationTagSizeBytes;
    private const int RecoveryWordCount = 32;
    private static readonly byte[] Magic = "DPNG0001"u8.ToArray();
    private static readonly string[] recoveryWordPrefixes = "amber aqua azure birch coral dusk ember fern frost gold jade lunar mist moss oak pearl".Split(' ');
    private static readonly string[] recoveryWordSuffixes = "bay bird bloom brook cloud dune field flame glen hill lake leaf moon reed star wave".Split(' ');

    public static byte[] Derive(string secret, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] ContentFingerprint(ReadOnlySpan<byte> masterKey, string sourceSha256)
    {
        byte[] hash;
        try { hash = Convert.FromHexString(sourceSha256); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"Source SHA-256 value '{sourceSha256}' must contain exactly 64 hexadecimal characters.", exception);
        }
        if (hash.Length != 32)
            throw new InvalidOperationException(
                $"Source SHA-256 value '{sourceSha256}' must contain exactly 64 hexadecimal characters.");
        Span<byte> fingerprintKey = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, fingerprintKey,
            salt: ReadOnlySpan<byte>.Empty, info: "DiffusionGenGallery/content-fingerprint/v1"u8);
        try { return HMACSHA256.HashData(fingerprintKey, hash); }
        finally { CryptographicOperations.ZeroMemory(fingerprintKey); }
    }

    /// <summary>Keyed hash of a normalized full path, so drive lookups run in SQL without storing the path in plaintext.</summary>
    public static byte[] PathFingerprint(ReadOnlySpan<byte> masterKey, string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        Span<byte> fingerprintKey = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, fingerprintKey,
            salt: ReadOnlySpan<byte>.Empty, info: "DiffusionGenGallery/path-fingerprint/v1"u8);
        try { return HMACSHA256.HashData(fingerprintKey, Encoding.UTF8.GetBytes(normalized)); }
        finally { CryptographicOperations.ZeroMemory(fingerprintKey); }
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key, string purpose)
    {
        var encryptedEnvelopeBytes = AllocateEncryptedEnvelope(plain.Length);
        var nonce = encryptedEnvelopeBytes.AsSpan(MagicSizeBytes, NonceSizeBytes);
        RandomNumberGenerator.Fill(nonce);
        EncryptPayload(plain, key, purpose, nonce, encryptedEnvelopeBytes);
        return encryptedEnvelopeBytes;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, string purpose)
    {
        ValidateEncryptedEnvelope(envelope);
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
        RandomNumberGenerator.GetBytes(RecoveryWordCount)
            .Select(value => $"{recoveryWordPrefixes[value >> 4]}-{recoveryWordSuffixes[value & 15]}"));

    private static string RecoveryRequirementMessage =>
        $"Recovery requires all {RecoveryWordCount} compound words in their original order.";

    public static string NormalizeRecovery(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            throw new InvalidOperationException(RecoveryRequirementMessage);
        var words = phrase.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!IsValidRecoveryPhrase(words))
            throw new InvalidOperationException(RecoveryRequirementMessage);
        return string.Join(' ', words);
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

    private static void ValidateEncryptedEnvelope(ReadOnlySpan<byte> encryptedEnvelopeBytes)
    {
        if (encryptedEnvelopeBytes.Length < EnvelopeHeaderSizeBytes ||
            !encryptedEnvelopeBytes[..MagicSizeBytes].SequenceEqual(Magic))
            throw new CryptographicException("Invalid encrypted file format.");
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

    private static bool IsValidRecoveryPhrase(string[] words) =>
        words.Length == RecoveryWordCount && words.All(IsValidRecoveryWord);

    private static bool IsValidRecoveryWord(string word)
    {
        var parts = word.Split('-');
        return parts.Length == 2 &&
            recoveryWordPrefixes.Contains(parts[0]) &&
            recoveryWordSuffixes.Contains(parts[1]);
    }
}
