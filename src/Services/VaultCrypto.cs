using System.Security.Cryptography;
using System.Text;

namespace Gallery.Services;

public static class VaultCrypto
{
    public const int Iterations = 600_000;
    private static readonly byte[] Magic = "DPNG0001"u8.ToArray();
    private static readonly string[] First = "amber aqua azure birch coral dusk ember fern frost gold jade lunar mist moss oak pearl".Split(' ');
    private static readonly string[] Second = "bay bird bloom brook cloud dune field flame glen hill lake leaf moon reed star wave".Split(' ');

    public static byte[] Derive(string secret, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] ContentFingerprint(ReadOnlySpan<byte> masterKey, string sourceSha256)
    {
        var hash = Convert.FromHexString(sourceSha256);
        if (hash.Length != 32) throw new InvalidOperationException("Invalid source SHA-256 in the image catalog.");
        Span<byte> fingerprintKey = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, fingerprintKey,
            salt: ReadOnlySpan<byte>.Empty, info: "DiffusionGenGallery/content-fingerprint/v1"u8);
        try { return HMACSHA256.HashData(fingerprintKey, hash); }
        finally { CryptographicOperations.ZeroMemory(fingerprintKey); }
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key, string purpose)
    {
        var result = new byte[Magic.Length + 12 + 16 + plain.Length];
        Magic.CopyTo(result, 0);
        var nonce = result.AsSpan(8, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, result.AsSpan(36), result.AsSpan(20, 16), Encoding.UTF8.GetBytes(purpose));
        return result;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, string purpose)
    {
        if (envelope.Length < 36 || !envelope[..8].SequenceEqual(Magic))
            throw new CryptographicException("Invalid encrypted file format.");
        var plain = new byte[envelope.Length - 36];
        using var aes = new AesGcm(key, 16);
        try
        {
            aes.Decrypt(envelope.Slice(8, 12), envelope[36..], envelope.Slice(20, 16), plain,
                Encoding.UTF8.GetBytes(purpose));
            return plain;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plain);
            throw;
        }
    }

    // Each independently random compound word represents 8 bits: 32 words = 256 bits.
    public static string CreateRecoveryPhrase() => string.Join(' ',
        RandomNumberGenerator.GetBytes(32).Select(b => $"{First[b >> 4]}-{Second[b & 15]}"));

    public static string NormalizeRecovery(string phrase)
    {
        var words = phrase.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 32 || words.Any(word =>
        {
            var parts = word.Split('-');
            return parts.Length != 2 || !First.Contains(parts[0]) || !Second.Contains(parts[1]);
        }))
            throw new InvalidOperationException("Recovery requires all 32 compound words in their original order.");
        return string.Join(' ', words);
    }
}
