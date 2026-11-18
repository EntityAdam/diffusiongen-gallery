using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Ports.Models;
using Gallery.Core.Services.Processors.Models;
using Gallery.Core.Services.Processors;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services.Sessions;
using SixLabors.ImageSharp;

namespace Gallery.Core.Services;

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
        var scanStore = galleryLifetime.Current.GalleryStore;
        var imageRecords = await scanStore.ReadRecordsAsync(keyLease.Bytes);
        var cachedHashesByImageId = await scanStore.ReadSettingsAsync<Dictionary<string, PerceptualHash>>(
            PerceptualHashesId, PerceptualHashesPurpose, keyLease.Bytes) ?? [];
        Dictionary<string, PerceptualHash> perceptualHashesByImageId;
        List<ImageRecord> pendingImageRecords;
        (perceptualHashesByImageId, pendingImageRecords) = ReconcileCachedHashes(imageRecords, cachedHashesByImageId);
        await ComputePendingHashesAsync(scanStore, keyLease.Bytes, pendingImageRecords, perceptualHashesByImageId, imageRecords.Count, progress);
        await scanStore.SaveSettingsAsync(perceptualHashesByImageId, PerceptualHashesId, PerceptualHashesPurpose, keyLease.Bytes);
        var duplicateScan = await BuildDuplicateScanAsync(imageRecords, perceptualHashesByImageId);
        await scanStore.SaveSettingsAsync(duplicateScan, DuplicateScanId, DuplicateScanPurpose, keyLease.Bytes);
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
            if (cachedHashesByImageId.TryGetValue(imageRecord.Id, out var cachedPerceptualHash)
                && cachedPerceptualHash.PngSha256 == imageRecord.ContentSha256)
                hashesByImageId[imageRecord.Id] = cachedPerceptualHash;
            else
                pendingImageRecords.Add(imageRecord);
        }
        return (hashesByImageId, pendingImageRecords);
    }

    private static async Task ComputePendingHashesAsync(IGalleryStore scanStore, byte[] key, IReadOnlyList<ImageRecord> pendingImageRecords,
        IDictionary<string, PerceptualHash> perceptualHashesByImageId, int totalImageCount,
        IProgress<(int Completed, int Total)>? progress)
    {
        var completedCount = totalImageCount - pendingImageRecords.Count;
        progress?.Report((completedCount, totalImageCount));
        foreach (var imageRecordBatch in pendingImageRecords.Chunk(HashBatchSize))
        {
            var imageThumbnails = new List<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)>(imageRecordBatch.Length);
            try
            {
                await ReadHashThumbnailsAsync(scanStore, imageRecordBatch, key, imageThumbnails);
                var batchPerceptualHashesByImageId = await PerceptualHashBatchProcessor.ComputeAsync(imageThumbnails);
                foreach (var (imageId, perceptualHash) in batchPerceptualHashesByImageId)
                    perceptualHashesByImageId.Add(imageId, perceptualHash);
            }
            finally
            {
                DisposeHashThumbnails(imageThumbnails);
            }
            completedCount += imageRecordBatch.Length;
            progress?.Report((completedCount, totalImageCount));
        }
    }

    private static void DisposeHashThumbnails(
        IEnumerable<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails)
    {
        foreach (var (_, imageThumbnail) in imageThumbnails) imageThumbnail.Dispose();
    }

    private static async Task ReadHashThumbnailsAsync(IGalleryStore scanStore, IReadOnlyList<ImageRecord> imageRecords,
        byte[] key, List<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails)
    {
        foreach (var imageRecord in imageRecords)
        {
            try { imageThumbnails.Add((imageRecord, await scanStore.ReadThumbnailAsync(imageRecord.Id, key))); }
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
        var groups = await Task.Run(() => DuplicateRules.GroupDuplicates(hashedImageRecords.ToArray(),
            imageRecord => hashesByImageId[imageRecord.Id].Hash, StorageFileSystem.FileLength));
        return new DuplicateScan(groups.ToImmutableArray(),
            hashedImageRecords.Count, DateTimeOffset.UtcNow);
    }
}
