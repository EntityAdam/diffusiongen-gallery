namespace Gallery.Core.Services.DataManagers.Validators;

internal static class SourceFingerprintValidator
{
    public static byte[] Decode(string sourceSha256)
    {
        byte[] hash;
        try { hash = Convert.FromHexString(sourceSha256); }
        catch (FormatException formatException)
        {
            throw new InvalidOperationException(
                $"Source SHA-256 value '{sourceSha256}' must contain exactly 64 hexadecimal characters.", formatException);
        }
        if (hash.Length != 32)
            throw new InvalidOperationException($"Source SHA-256 value '{sourceSha256}' must contain exactly 64 hexadecimal characters.");
        return hash;
    }
}
