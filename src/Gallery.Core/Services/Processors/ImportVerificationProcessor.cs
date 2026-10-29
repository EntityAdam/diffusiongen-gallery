using Gallery.Core.Services.Processors.Codecs;
using System.Security.Cryptography;
using Gallery.Core.Models;
using SixLabors.ImageSharp;

namespace Gallery.Core.Services.Processors;

internal static class ImportVerificationProcessor
{
    public static void Verify(ImageRecord record, ReadOnlySpan<byte> thumbnail, byte[] plain)
    {
        try
        {
            if (record.ContentSha256.Length == 0 || Convert.ToHexString(SHA256.HashData(plain)) != record.ContentSha256)
                throw new CryptographicException("Saved file did not match the imported content.");
            if (!record.IsVideo)
            {
                var imageInfo = Image.Identify(plain);
                if (imageInfo.Width != record.Width || imageInfo.Height != record.Height)
                    throw new CryptographicException("Saved media dimensions did not match the catalog.");
            }
            Image.Identify(thumbnail);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
