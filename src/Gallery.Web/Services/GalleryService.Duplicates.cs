using Gallery.Web.Services.DataManagers.Models;
using Gallery.Web.Services.Processors;
using Gallery.Core.Processors;
using Gallery.Web.Services.DataManagers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Immutable;
using Gallery.Web.Exceptions;
using Gallery.Web.Models;
using SixLabors.ImageSharp;

namespace Gallery.Web.Services;

public sealed partial class GalleryService
{
    private static class PreferencesNormalizer
    {
        public static WebUiPreferences Normalize(WebUiPreferences preferences)
        {
            var density = WebUiPreferences.Densities.Contains(preferences.GridDensity)
                ? preferences.GridDensity
                : WebUiPreferences.ComfortableDensity;
            var autoLock = preferences.AutoLockMinutes switch
            {
                < 0 => WebUiPreferences.DefaultAutoLockMinutes,
                > 1440 => 1440,
                _ => preferences.AutoLockMinutes
            };
            return preferences with { GridDensity = density, AutoLockMinutes = autoLock };
        }
    }

    private const string PerceptualHashesId = "perceptual-hashes";
    private const string PerceptualHashesPurpose = "perceptual-hashes:v1";
    private const string DuplicateScanId = "duplicate-scan";
    private const string DuplicateScanPurpose = "duplicate-scan:v1";
    private const string PreferencesId = "ui-preferences";
    private const string PreferencesPurpose = "ui-preferences:v1";
    private const int HashBatchSize = 64;

    private sealed record PerceptualHash(string PngSha256, ulong Hash);

    private sealed class PerceptualHashProcessor(GalleryStore galleryStore)
    {
        public async Task<Dictionary<string, PerceptualHash>> ComputeAsync(
            IReadOnlyList<WebImageRecord> imageRecords, byte[] key)
        {
            var imageThumbnails = new List<(WebImageRecord ImageRecord, ImageThumbnail ImageThumbnail)>(imageRecords.Count);
            try
            {
                await LoadThumbnailsAsync(imageRecords, key, imageThumbnails);
                var perceptualHashes = await ComputeHashesAsync(imageThumbnails);
                return MapHashes(imageThumbnails, perceptualHashes);
            }
            finally
            {
                foreach (var imageThumbnail in imageThumbnails)
                    CryptographicOperations.ZeroMemory(imageThumbnail.ImageThumbnail.PngBytes);
            }
        }
        private async Task LoadThumbnailsAsync(IReadOnlyList<WebImageRecord> imageRecords, byte[] key,
            List<(WebImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails)
        {
        foreach (var imageRecord in imageRecords)
                {
                    try
                    {
                        imageThumbnails.Add((imageRecord, await galleryStore.ReadThumbnailAsync(imageRecord.Id, key)));
                    }
                    catch (Exception exception) when (exception is GalleryException or InvalidOperationException or IOException
                        or UnauthorizedAccessException or CryptographicException)
                    {
                        throw new InvalidOperationException(
                            $"Could not read the thumbnail for image '{imageRecord.Id}' during duplicate scanning.", exception);
                    }
                }

        }

        private static async Task<PerceptualHash?[]> ComputeHashesAsync(
            List<(WebImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails)
        {
                return await Task.Run(() =>
                {
                    var computedHashes = new PerceptualHash?[imageThumbnails.Count];
                    Parallel.For(0, imageThumbnails.Count, index =>
                    {
                        WebImageRecord imageRecord;
                        ImageThumbnail imageThumbnail;
                        (imageRecord, imageThumbnail) = imageThumbnails[index];
                        try
                        {
                            var plainThumbnail = imageThumbnail.PngBytes;
                            try
                            {
                                computedHashes[index] = new(
                                    imageRecord.PngSha256, PerceptualImageHasher.DifferenceHash(plainThumbnail));
                            }
                            finally { CryptographicOperations.ZeroMemory(plainThumbnail); }
                        }
                        catch (Exception exception) when (exception is CryptographicException
                            or ImageFormatException or UnknownImageFormatException)
                        {
                            throw new InvalidOperationException(
                                $"Could not compute a perceptual hash for image '{imageRecord.Id}'.", exception);
                        }
                    });
                    return computedHashes;
                });

        }

        private static Dictionary<string, PerceptualHash> MapHashes(
            List<(WebImageRecord ImageRecord, ImageThumbnail ImageThumbnail)> imageThumbnails,
            PerceptualHash?[] perceptualHashes)
        {
                var hashesByImageId = new Dictionary<string, PerceptualHash>(imageThumbnails.Count);
                for (var index = 0; index < imageThumbnails.Count; index++)
                {
                    if (perceptualHashes[index] is { } perceptualHash)
                        hashesByImageId.Add(imageThumbnails[index].ImageRecord.Id, perceptualHash);
                }
                return hashesByImageId;
        }
    }

    public async Task<WebUiPreferences> LoadPreferencesAsync() =>
        PreferencesNormalizer.Normalize(
            await ReadEncryptedSettingAsync<WebUiPreferences>(PreferencesId, PreferencesPurpose) ?? new());

    public Task SavePreferencesAsync(WebUiPreferences preferences) =>
        WriteEncryptedSettingAsync(PreferencesNormalizer.Normalize(preferences), PreferencesId, PreferencesPurpose);

    public Task<WebDuplicateScan?> CachedDuplicateScanAsync() =>
        ReadEncryptedSettingAsync<WebDuplicateScan>(DuplicateScanId, DuplicateScanPurpose);

    public async Task<WebDuplicateScan> ScanDuplicatesAsync(IProgress<(int Completed, int Total)>? progress = null)
    {
        var imageRecords = await ListAsync();
        var cachedHashesByImageId =
            await ReadEncryptedSettingAsync<Dictionary<string, PerceptualHash>>(PerceptualHashesId, PerceptualHashesPurpose) ?? [];
        Dictionary<string, PerceptualHash> perceptualHashesByImageId;
        List<WebImageRecord> pendingImageRecords;
        (perceptualHashesByImageId, pendingImageRecords) = ReconcileCachedHashes(imageRecords, cachedHashesByImageId);
        await ComputePendingHashesAsync(pendingImageRecords, perceptualHashesByImageId, imageRecords.Count, progress);
        await WriteEncryptedSettingAsync(perceptualHashesByImageId, PerceptualHashesId, PerceptualHashesPurpose);
        var duplicateScan = await BuildDuplicateScanAsync(imageRecords, perceptualHashesByImageId);
        await WriteEncryptedSettingAsync(duplicateScan, DuplicateScanId, DuplicateScanPurpose);
        return duplicateScan;
    }

    private static (Dictionary<string, PerceptualHash> PerceptualHashesByImageId, List<WebImageRecord> PendingImageRecords)
        ReconcileCachedHashes(IEnumerable<WebImageRecord> imageRecords,
            IReadOnlyDictionary<string, PerceptualHash> cachedHashesByImageId)
    {
        var hashesByImageId = new Dictionary<string, PerceptualHash>();
        var pendingImageRecords = new List<WebImageRecord>();
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

    private async Task ComputePendingHashesAsync(IReadOnlyList<WebImageRecord> pendingImageRecords,
        IDictionary<string, PerceptualHash> perceptualHashesByImageId, int totalImageCount,
        IProgress<(int Completed, int Total)>? progress)
    {
        var completedCount = totalImageCount - pendingImageRecords.Count;
        progress?.Report((completedCount, totalImageCount));
        var perceptualHashProcessor = new PerceptualHashProcessor(galleryStore);
        foreach (var imageRecordBatch in pendingImageRecords.Chunk(HashBatchSize))
        {
            using var key = vaultSession.AcquireUnlockedSession().Borrow();
            var batchPerceptualHashesByImageId = await perceptualHashProcessor.ComputeAsync(imageRecordBatch, key.Bytes);
            foreach (var (imageId, perceptualHash) in batchPerceptualHashesByImageId)
                perceptualHashesByImageId.Add(imageId, perceptualHash);
            completedCount += imageRecordBatch.Length;
            progress?.Report((completedCount, totalImageCount));
        }
    }

    private static async Task<WebDuplicateScan> BuildDuplicateScanAsync(IEnumerable<WebImageRecord> imageRecords,
        IReadOnlyDictionary<string, PerceptualHash> hashesByImageId)
    {
        var hashedImageRecords = imageRecords.Where(imageRecord => hashesByImageId.ContainsKey(imageRecord.Id)).ToList();
        var groups = await Task.Run(() => DuplicateRules.GroupDuplicates(hashedImageRecords.Select(record => record.ToDomain()).ToArray(),
            imageRecord => hashesByImageId[imageRecord.Id].Hash, FileLength));
        return new WebDuplicateScan(groups.Select(group => group.ToWeb()).ToImmutableArray(),
            hashedImageRecords.Count, DateTimeOffset.UtcNow);
    }

    /// <summary>Unmarks the kept copies, marks the other copies for deletion, and returns the committed records.</summary>
    public async Task<List<WebImageRecord>> ApplyDuplicateDecisionAsync(IReadOnlyCollection<string> groupIds, IReadOnlySet<string> keep)
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
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryStore.ReadSettingsAsync<T>(id, purpose, key.Bytes);
    }

    private async Task WriteEncryptedSettingAsync<T>(T value, string id, string purpose)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.SaveSettingsAsync(value, id, purpose, key.Bytes);
    }
}
