using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors.Hashing;
using Gallery.Core.Services.Processors.Models;
using SixLabors.ImageSharp;
using System.Security.Cryptography;

namespace Gallery.Core.Services.Processors;

internal static class PerceptualHashBatchProcessor
{
    // The caller owns every thumbnail; all parallel readers finish before this task returns or throws.
    public static Task<Dictionary<string, PerceptualHash>> ComputeAsync(
        IReadOnlyList<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails) => Task.Run(() =>
    {
        var computedHashes = new PerceptualHash[imageThumbnails.Count];
        Parallel.For(0, imageThumbnails.Count, index =>
        {
            ImageRecord imageRecord;
            ImageThumbnail imageThumbnail;
            (imageRecord, imageThumbnail) = imageThumbnails[index];
            try
            {
                computedHashes[index] = new(imageRecord.PngSha256,
                    PerceptualImageHasher.DifferenceHash(imageThumbnail.Content.Span));
            }
            catch (Exception exception) when (exception is CryptographicException or ImageFormatException or UnknownImageFormatException)
            {
                throw new InvalidOperationException($"Could not compute a perceptual hash for image '{imageRecord.Id}'.", exception);
            }
        });
        var hashesByImageId = new Dictionary<string, PerceptualHash>(imageThumbnails.Count, StringComparer.Ordinal);
        for (var index = 0; index < imageThumbnails.Count; index++)
            hashesByImageId.Add(imageThumbnails[index].ImageRecord.Id, computedHashes[index]);
        return hashesByImageId;
    });
}
