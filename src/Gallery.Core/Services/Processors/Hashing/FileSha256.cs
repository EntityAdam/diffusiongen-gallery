using System.Security.Cryptography;

namespace Gallery.Core.Services.Processors.Hashing;

internal static class FileSha256
{
    public static async Task<string> ComputeAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
