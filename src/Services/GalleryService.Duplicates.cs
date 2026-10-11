using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Immutable;
using Gallery.Core;
using Gallery.Models;
using SixLabors.ImageSharp;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    private static class PreferencesNormalizer
    {
        public static UiPreferences Normalize(UiPreferences preferences) =>
            UiPreferences.Densities.Contains(preferences.GridDensity)
                ? preferences
                : preferences with { GridDensity = UiPreferences.ComfortableDensity };
    }

    private const string PerceptualHashesId = "perceptual-hashes";
    private const string PerceptualHashesPurpose = "perceptual-hashes:v1";
    private const string DuplicateScanId = "duplicate-scan";
    private const string DuplicateScanPurpose = "duplicate-scan:v1";
    private const string PreferencesId = "ui-preferences";
    private const string PreferencesPurpose = "ui-preferences:v1";
    private const int HashBatchSize = 64;

    private sealed record PerceptualHash(string PngSha256, ulong Hash);

    private sealed class PerceptualHashProcessor(GalleryStore store)
    {
        public async Task<Dictionary<string, PerceptualHash>> ComputeAsync(
            IReadOnlyList<ImageRecord> imageRecords, byte[] key)
        {
            var encryptedThumbnails = new List<(ImageRecord Record, byte[] Bytes)>(imageRecords.Count);
            foreach (var imageRecord in imageRecords)
            {
                try
                {
                    encryptedThumbnails.Add((imageRecord, await store.ReadThumbnailAsync(imageRecord.Id)));
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException
                    or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        $"Could not read the thumbnail for image '{imageRecord.Id}' during duplicate scanning.", exception);
                }
            }

            var perceptualHashes = await Task.Run(() =>
            {
                var computedHashes = new PerceptualHash?[encryptedThumbnails.Count];
                Parallel.For(0, encryptedThumbnails.Count, index =>
                {
                    var (imageRecord, encryptedThumbnail) = encryptedThumbnails[index];
                    try
                    {
                        var plainThumbnail = VaultCrypto.Decrypt(encryptedThumbnail, key, $"thumbnail:{imageRecord.Id}");
                        try
                        {
                            computedHashes[index] = new(
                                imageRecord.PngSha256, DuplicateRules.DifferenceHash(plainThumbnail));
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

            var hashesByImageId = new Dictionary<string, PerceptualHash>(encryptedThumbnails.Count);
            for (var index = 0; index < encryptedThumbnails.Count; index++)
            {
                if (perceptualHashes[index] is { } perceptualHash)
                    hashesByImageId.Add(encryptedThumbnails[index].Record.Id, perceptualHash);
            }
            return hashesByImageId;
        }
    }

    public async Task<UiPreferences> LoadPreferencesAsync() =>
        PreferencesNormalizer.Normalize(
            await ReadEncryptedSettingAsync<UiPreferences>(PreferencesId, PreferencesPurpose) ?? new());

    public Task SavePreferencesAsync(UiPreferences preferences) =>
        WriteEncryptedSettingAsync(PreferencesNormalizer.Normalize(preferences), PreferencesId, PreferencesPurpose);

    public Task<DuplicateScan?> CachedDuplicateScanAsync() =>
        ReadEncryptedSettingAsync<DuplicateScan>(DuplicateScanId, DuplicateScanPurpose);

    public async Task<DuplicateScan> ScanDuplicatesAsync(IProgress<(int Completed, int Total)>? progress = null)
    {
        var imageRecords = await ListAsync();
        var cachedHashesByImageId =
            await ReadEncryptedSettingAsync<Dictionary<string, PerceptualHash>>(PerceptualHashesId, PerceptualHashesPurpose) ?? [];
        var (perceptualHashesByImageId, pendingImageRecords) = ReconcileCachedHashes(imageRecords, cachedHashesByImageId);
        await ComputePendingHashesAsync(pendingImageRecords, perceptualHashesByImageId, imageRecords.Count, progress);
        await WriteEncryptedSettingAsync(perceptualHashesByImageId, PerceptualHashesId, PerceptualHashesPurpose);
        var scan = await BuildDuplicateScanAsync(imageRecords, perceptualHashesByImageId);
        await WriteEncryptedSettingAsync(scan, DuplicateScanId, DuplicateScanPurpose);
        return scan;
    }

    private static (Dictionary<string, PerceptualHash> HashesByImageId, List<ImageRecord> PendingImageRecords)
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

    private async Task ComputePendingHashesAsync(IReadOnlyList<ImageRecord> pendingImageRecords,
        IDictionary<string, PerceptualHash> perceptualHashesByImageId, int totalImageCount,
        IProgress<(int Completed, int Total)>? progress)
    {
        var completedCount = totalImageCount - pendingImageRecords.Count;
        progress?.Report((completedCount, totalImageCount));
        var hashProcessor = new PerceptualHashProcessor(store);
        foreach (var imageRecordBatch in pendingImageRecords.Chunk(HashBatchSize))
        {
            using var key = session.Borrow();
            var batchHashes = await hashProcessor.ComputeAsync(imageRecordBatch, key.Bytes);
            foreach (var (imageId, perceptualHash) in batchHashes)
                perceptualHashesByImageId.Add(imageId, perceptualHash);
            completedCount += imageRecordBatch.Length;
            progress?.Report((completedCount, totalImageCount));
        }
    }

    private static async Task<DuplicateScan> BuildDuplicateScanAsync(IEnumerable<ImageRecord> imageRecords,
        IReadOnlyDictionary<string, PerceptualHash> hashesByImageId)
    {
        var hashedImageRecords = imageRecords.Where(imageRecord => hashesByImageId.ContainsKey(imageRecord.Id)).ToList();
        var groups = await Task.Run(() => DuplicateRules.GroupDuplicates(hashedImageRecords,
            imageRecord => hashesByImageId[imageRecord.Id].Hash, FileLength));
        return new DuplicateScan(groups.ToImmutableArray(), hashedImageRecords.Count, DateTimeOffset.UtcNow);
    }

    /// <summary>Unmarks the kept copies and marks every other copy in the group for deletion. Returns the failure count.</summary>
    public async Task ApplyDuplicateDecisionAsync(IReadOnlyCollection<string> groupIds, IReadOnlySet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        ArgumentNullException.ThrowIfNull(keep);
        if (keep.Count == 0) throw new InvalidOperationException("Keep at least one image in the group.");
        if (!keep.All(groupIds.Contains)) throw new InvalidOperationException("Kept images must belong to the group.");
        var retainedIds = keep.ToHashSet(StringComparer.Ordinal);
        await UpdateManyAsync(groupIds.Distinct(StringComparer.Ordinal).ToList(),
            record => record.MarkedForDeletion = !retainedIds.Contains(record.Id));
    }

    private async Task<T?> ReadEncryptedSettingAsync<T>(string id, string purpose) where T : class
    {
        var encrypted = await store.ReadSettingsAsync(id);
        if (encrypted is null) return null;
        using var key = session.Borrow();
        var plain = VaultCrypto.Decrypt(encrypted, key.Bytes, purpose);
        try { return JsonSerializer.Deserialize<T>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task WriteEncryptedSettingAsync<T>(T value, string id, string purpose)
    {
        using var key = session.Borrow();
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try { await store.SaveSettingsAsync(VaultCrypto.Encrypt(plain, key.Bytes, purpose), id); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
