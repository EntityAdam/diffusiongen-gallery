using System.Collections.Immutable;
using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Duplicates.Hashing;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Features.Files.Processors;

namespace Gallery.Core.Features.Duplicates;

internal sealed class DuplicateScanner(GalleryLifetime galleryLifetime, VaultSession vaultSession)
{
    private const string PerceptualHashesId = "perceptual-hashes";
    private const string PerceptualHashesPurpose = "perceptual-hashes:v1";
    private const string DuplicateScanId = "duplicate-scan";
    private const string DuplicateScanPurpose = "duplicate-scan:v1";
    private const int HashBatchSize = 64;

    public async Task<DuplicateScan?> CachedDuplicateScanAsync()
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        return await store.ReadSettingsAsync<DuplicateScan>(DuplicateScanId, DuplicateScanPurpose, keyLease.Bytes);
    }

    public async Task<DuplicateScan> ScanDuplicatesAsync(IProgress<(int Completed, int Total)>? progress = null)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var galleryStore = galleryLifetime.Current.GalleryStore;
        var imageRecords = await galleryStore.ReadRecordsAsync(keyLease.Bytes);
        var cachedHashesByImageId = await galleryStore.ReadSettingsAsync<Dictionary<string, PerceptualHash>>(
            PerceptualHashesId, PerceptualHashesPurpose, keyLease.Bytes) ?? [];
        Dictionary<string, PerceptualHash> perceptualHashesByImageId;
        List<ImageRecord> pendingImageRecords;
        (perceptualHashesByImageId, pendingImageRecords) = ReconcileCachedHashes(imageRecords, cachedHashesByImageId);
        await ComputePendingHashesAsync(galleryStore, keyLease.Bytes, pendingImageRecords, perceptualHashesByImageId, imageRecords.Count, progress);
        await galleryStore.SaveSettingsAsync(perceptualHashesByImageId, PerceptualHashesId, PerceptualHashesPurpose, keyLease.Bytes);
        var duplicateScan = await BuildDuplicateScanAsync(imageRecords, perceptualHashesByImageId);
        await galleryStore.SaveSettingsAsync(duplicateScan, DuplicateScanId, DuplicateScanPurpose, keyLease.Bytes);
        return duplicateScan;
    }

    private static (Dictionary<string, PerceptualHash> PerceptualHashesByImageId, List<ImageRecord> PendingImageRecords)
        ReconcileCachedHashes(IEnumerable<ImageRecord> imageRecords,
            IReadOnlyDictionary<string, PerceptualHash> cachedHashesByImageId)
    {
        var hashesByImageId = new Dictionary<string, PerceptualHash>();
        var pendingImageRecords = new List<ImageRecord>();
        foreach (var imageRecord in imageRecords)
        {
            if (TryGetCurrentCachedHash(cachedHashesByImageId, imageRecord, out var cachedPerceptualHash))
                hashesByImageId[imageRecord.Id] = cachedPerceptualHash;
            else
                pendingImageRecords.Add(imageRecord);
        }
        return (hashesByImageId, pendingImageRecords);
    }

    private static bool TryGetCurrentCachedHash(IReadOnlyDictionary<string, PerceptualHash> cachedHashesByImageId,
        ImageRecord imageRecord, out PerceptualHash cachedPerceptualHash)
    {
        if (cachedHashesByImageId.TryGetValue(imageRecord.Id, out var candidateHash)
            && candidateHash.ContentSha256 == imageRecord.ContentSha256)
        {
            cachedPerceptualHash = candidateHash;
            return true;
        }
        cachedPerceptualHash = null!;
        return false;
    }

    private static async Task ComputePendingHashesAsync(IGalleryStore galleryStore, byte[] key, IReadOnlyList<ImageRecord> pendingImageRecords,
        IDictionary<string, PerceptualHash> perceptualHashesByImageId, int totalImageCount,
        IProgress<(int Completed, int Total)>? progress)
    {
        var completedCount = totalImageCount - pendingImageRecords.Count;
        progress?.Report((completedCount, totalImageCount));
        foreach (var imageRecordBatch in pendingImageRecords.Chunk(HashBatchSize))
        {
            await ProcessHashBatchAsync(galleryStore, key, imageRecordBatch, perceptualHashesByImageId);
            completedCount += imageRecordBatch.Length;
            progress?.Report((completedCount, totalImageCount));
        }
    }

    private static async Task ProcessHashBatchAsync(IGalleryStore galleryStore, byte[] key,
        IReadOnlyList<ImageRecord> imageRecordBatch, IDictionary<string, PerceptualHash> perceptualHashesByImageId)
    {
        var imageThumbnails = new List<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)>(imageRecordBatch.Count);
        try
        {
            await ReadHashThumbnailsAsync(galleryStore, imageRecordBatch, key, imageThumbnails);
            var batchPerceptualHashesByImageId = await PerceptualHashBatchProcessor.ComputeAsync(imageThumbnails);
            foreach (var (imageId, perceptualHash) in batchPerceptualHashesByImageId)
                perceptualHashesByImageId.Add(imageId, perceptualHash);
        }
        finally
        {
            DisposeHashThumbnails(imageThumbnails);
        }
    }

    private static void DisposeHashThumbnails(
        IEnumerable<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails)
    {
        foreach (var imageThumbnail in imageThumbnails) imageThumbnail.ImageThumbnail.Dispose();
    }

    private static async Task ReadHashThumbnailsAsync(IGalleryStore galleryStore, IReadOnlyList<ImageRecord> imageRecords,
        byte[] key, List<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails)
    {
        foreach (var imageRecord in imageRecords)
        {
            try { imageThumbnails.Add((imageRecord, await galleryStore.ReadThumbnailAsync(imageRecord.Id, key))); }
            catch (Exception exception) when (exception is GalleryException or InvalidOperationException or IOException
                or UnauthorizedAccessException or CryptographicException)
            {
                throw new InvalidOperationException(
                    $"Could not read the thumbnail for media item '{imageRecord.Id}' during duplicate scanning.", exception);
            }
        }
    }

    private static async Task<DuplicateScan> BuildDuplicateScanAsync(IEnumerable<ImageRecord> imageRecords,
        IReadOnlyDictionary<string, PerceptualHash> hashesByImageId)
    {
        var hashedImageRecords = imageRecords.Where(imageRecord => hashesByImageId.ContainsKey(imageRecord.Id)).ToList();
        var groups = await Task.Run(() => DuplicateRules.GroupDuplicates(hashedImageRecords,
            imageRecord => hashesByImageId[imageRecord.Id].Hash, StorageFileSystem.FileLength));
        return new DuplicateScan(groups.ToImmutableArray(),
            hashedImageRecords.Count, DateTimeOffset.UtcNow);
    }
}
