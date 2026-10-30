using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.DataManagers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Immutable;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using SixLabors.ImageSharp;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    private const string PerceptualHashesId = "perceptual-hashes";
    private const string PerceptualHashesPurpose = "perceptual-hashes:v1";
    private const string DuplicateScanId = "duplicate-scan";
    private const string DuplicateScanPurpose = "duplicate-scan:v1";
    private const string PreferencesId = "ui-preferences";
    private const string PreferencesPurpose = "ui-preferences:v1";
    private const int HashBatchSize = 64;

    public async Task<UiPreferences> LoadPreferencesAsync() =>
        PreferencesNormalizer.Normalize(
            await ReadEncryptedSettingAsync<UiPreferences>(PreferencesId, PreferencesPurpose) ?? new());

    public Task SavePreferencesAsync(UiPreferences preferences) =>
        WriteEncryptedSettingAsync(PreferencesNormalizer.Normalize(preferences), PreferencesId, PreferencesPurpose);

    public Task<DuplicateScan?> CachedDuplicateScanAsync() =>
        ReadEncryptedSettingAsync<DuplicateScan>(DuplicateScanId, DuplicateScanPurpose);

    public async Task<DuplicateScan> ScanDuplicatesAsync(IProgress<(int Completed, int Total)>? progress = null)
    {
        // One lease spans the scan, so a gallery switch cannot drain and publish another location until the
        // results are stored in the gallery that produced them.
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var scanStore = galleryStore;
        var imageRecords = await ListWithKeyAsync(keyLease.Bytes);
        var cachedHashesByImageId = await scanStore.ReadSettingsAsync<Dictionary<string, PerceptualHash>>(
            PerceptualHashesId, PerceptualHashesPurpose, keyLease.Bytes) ?? [];
        Dictionary<string, PerceptualHash> perceptualHashesByImageId;
        List<ImageRecord> pendingImageRecords;
        (perceptualHashesByImageId, pendingImageRecords) = ReconcileCachedHashes(imageRecords, cachedHashesByImageId);
        await ComputePendingHashesAsync(scanStore, pendingImageRecords, perceptualHashesByImageId, imageRecords.Count, progress);
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
                && cachedPerceptualHash.PngSha256 == imageRecord.PngSha256)
                hashesByImageId[imageRecord.Id] = cachedPerceptualHash;
            else
                pendingImageRecords.Add(imageRecord);
        }
        return (hashesByImageId, pendingImageRecords);
    }

    private async Task ComputePendingHashesAsync(GalleryStore scanStore, IReadOnlyList<ImageRecord> pendingImageRecords,
        IDictionary<string, PerceptualHash> perceptualHashesByImageId, int totalImageCount,
        IProgress<(int Completed, int Total)>? progress)
    {
        var completedCount = totalImageCount - pendingImageRecords.Count;
        progress?.Report((completedCount, totalImageCount));
        foreach (var imageRecordBatch in pendingImageRecords.Chunk(HashBatchSize))
        {
            // Re-acquiring per batch lets an explicit lock stop a long scan between batches.
            using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
            var imageThumbnails = new List<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)>(imageRecordBatch.Length);
            try
            {
                await ReadHashThumbnailsAsync(scanStore, imageRecordBatch, keyLease.Bytes, imageThumbnails);
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

    private static async Task ReadHashThumbnailsAsync(GalleryStore scanStore, IReadOnlyList<ImageRecord> imageRecords,
        byte[] key, List<(ImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails)
    {
        foreach (var imageRecord in imageRecords)
        {
            try { imageThumbnails.Add((imageRecord, await scanStore.ReadThumbnailAsync(imageRecord.Id, key))); }
            catch (Exception exception) when (exception is GalleryException or InvalidOperationException or IOException
                or UnauthorizedAccessException or CryptographicException)
            {
                throw new InvalidOperationException(
                    $"Could not read the thumbnail for image '{imageRecord.Id}' during duplicate scanning.", exception);
            }
        }
    }

    private static async Task<DuplicateScan> BuildDuplicateScanAsync(IEnumerable<ImageRecord> imageRecords,
        IReadOnlyDictionary<string, PerceptualHash> hashesByImageId)
    {
        var hashedImageRecords = imageRecords.Where(imageRecord => hashesByImageId.ContainsKey(imageRecord.Id)).ToList();
        var groups = await Task.Run(() => DuplicateRules.GroupDuplicates(hashedImageRecords.ToArray(),
            imageRecord => hashesByImageId[imageRecord.Id].Hash, FileLength));
        return new DuplicateScan(groups.ToImmutableArray(),
            hashedImageRecords.Count, DateTimeOffset.UtcNow);
    }

    /// <summary>Unmarks the kept copies, marks the other copies for deletion, and returns the committed records.</summary>
    public async Task<List<ImageRecord>> ApplyDuplicateDecisionAsync(IReadOnlyCollection<string> groupIds, IReadOnlySet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        ArgumentNullException.ThrowIfNull(keep);
        if (keep.Count == 0) throw new InvalidOperationException("Keep at least one image in the group.");
        var invalidKeeperIds = keep.Where(imageId => !groupIds.Contains(imageId)).ToArray();
        if (invalidKeeperIds.Length > 0)
            throw new InvalidOperationException(
                $"Kept images [{string.Join(", ", invalidKeeperIds)}] must belong to the group. Valid image IDs: [{string.Join(", ", groupIds)}].");
        var retainedIds = keep.ToHashSet(StringComparer.Ordinal);
        return await UpdateManyAsync(groupIds,
            record => SetDeletionMark(record, !retainedIds.Contains(record.Id)));
    }

    private async Task<T?> ReadEncryptedSettingAsync<T>(string id, string purpose) where T : class
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryStore.ReadSettingsAsync<T>(id, purpose, keyLease.Bytes);
    }

    private async Task WriteEncryptedSettingAsync<T>(T value, string id, string purpose)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.SaveSettingsAsync(value, id, purpose, keyLease.Bytes);
    }
}
