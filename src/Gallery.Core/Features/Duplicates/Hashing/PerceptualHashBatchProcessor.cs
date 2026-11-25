using System.Security.Cryptography;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using SixLabors.ImageSharp;

namespace Gallery.Core.Features.Duplicates.Hashing;

internal static class PerceptualHashBatchProcessor
{
    // The caller owns every thumbnail; all parallel readers finish before this task returns or throws.
    public static Task<Dictionary<string, PerceptualHash>> ComputeAsync(
        IReadOnlyList<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails) => Task.Run(() =>
    {
        var computedHashes = ComputeHashesInParallel(imageThumbnails);
        return IndexHashesByImageId(imageThumbnails, computedHashes);
    });

    private static PerceptualHash[] ComputeHashesInParallel(
        IReadOnlyList<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails)
    {
        var computedHashes = new PerceptualHash[imageThumbnails.Count];
        Parallel.For(0, imageThumbnails.Count, index =>
        {
            ImageRecord imageRecord;
            ImageThumbnail imageThumbnail;
            (imageRecord, imageThumbnail) = imageThumbnails[index];
            try
            {
                computedHashes[index] = new(imageRecord.ContentSha256,
                    PerceptualImageHasher.DifferenceHash(imageThumbnail.Content.Span));
            }
            catch (Exception exception) when (exception is CryptographicException or ImageFormatException or UnknownImageFormatException)
            {
                throw new InvalidOperationException($"Could not compute a perceptual hash for media item '{imageRecord.Id}'.", exception);
            }
        });
        return computedHashes;
    }

    private static Dictionary<string, PerceptualHash> IndexHashesByImageId(
        IReadOnlyList<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails,
        PerceptualHash[] computedHashes)
    {
        var hashesByImageId = new Dictionary<string, PerceptualHash>(imageThumbnails.Count, StringComparer.Ordinal);
        for (var index = 0; index < imageThumbnails.Count; index++)
            hashesByImageId.Add(imageThumbnails[index].ImageRecord.Id, computedHashes[index]);
        return hashesByImageId;
    }
}
