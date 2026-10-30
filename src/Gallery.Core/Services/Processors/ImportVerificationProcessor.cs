using Gallery.Core.Services.Processors.Codecs;
using System.Security.Cryptography;
using Gallery.Core.Models;
using SixLabors.ImageSharp;

namespace Gallery.Core.Services.Processors;

internal static class ImportVerificationProcessor
{
    public static void Verify(ImageRecord imageRecord, ReadOnlySpan<byte> thumbnail, byte[] plain)
    {
        try
        {
            VerifyContentHash(imageRecord, plain);
            VerifyImageDimensions(imageRecord, plain);
            VerifyThumbnail(thumbnail);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void VerifyContentHash(ImageRecord imageRecord, byte[] plain)
    {
        if (!SavedContentMatches(imageRecord, plain))
            throw new CryptographicException("Saved file did not match the imported content.");
    }

    private static bool SavedContentMatches(ImageRecord imageRecord, byte[] plain) =>
        imageRecord.PngSha256.Length > 0 && Convert.ToHexString(SHA256.HashData(plain)) == imageRecord.PngSha256;

    private static void VerifyImageDimensions(ImageRecord imageRecord, byte[] plain)
    {
        if (imageRecord.IsVideo) return;
        if (!DimensionsMatch(Image.Identify(plain), imageRecord))
            throw new CryptographicException("Saved image dimensions did not match the catalog.");
    }

    private static bool DimensionsMatch(ImageInfo imageInfo, ImageRecord imageRecord) =>
        imageInfo.Width == imageRecord.Width && imageInfo.Height == imageRecord.Height;

    private static void VerifyThumbnail(ReadOnlySpan<byte> thumbnail) => Image.Identify(thumbnail);
}
