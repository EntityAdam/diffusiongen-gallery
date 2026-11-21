using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Shared.Files;

namespace Gallery.Core.Features.Storage;

internal sealed class MediaStorageIndex(
    GalleryLifetime galleryLifetime, VaultSession vaultSession, GalleryDriveQueue galleryDriveQueue) : IGalleryStorage
{
    internal const string SettingId = "media-index";
    private const string SettingPurpose = "media-index:v1";

    private sealed record MediaIndexMarker(DateTimeOffset ScheduledAt);

    public Task RebuildStorageIndexAsync() =>
        RebuildStorageIndexAsync((store, key) => store.ClearMediaIndexAsync(key));

    internal async Task RebuildStorageIndexAsync(Func<IGalleryStore, byte[], Task> clearIndexAsync)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        var records = await store.ReadRecordsAsync(keyLease.Bytes);
        await store.Gate.WaitAsync();
        try
        {
            await store.DeleteSettingsAsync(keyLease.Bytes, SettingId);
            await clearIndexAsync(store, keyLease.Bytes);
            await ScheduleUnderGateAsync(records, keyLease.Bytes);
        }
        finally { store.Gate.Release(); }
        galleryDriveQueue.Start();
    }

    /// <summary>Schedules the one-time scan that seeds the media index; later loads read the index instead of the drive.</summary>
    internal async Task ScheduleAsync(IReadOnlyCollection<ImageRecord> records, byte[] key)
    {
        var store = galleryLifetime.Current.GalleryStore;
        await store.Gate.WaitAsync();
        try { await ScheduleUnderGateAsync(records, key); }
        finally { store.Gate.Release(); }
    }

    private async Task ScheduleUnderGateAsync(IReadOnlyCollection<ImageRecord> records, byte[] key)
    {
        var activity = System.Diagnostics.Activity.Current is { OperationName: "gallery.load" } load ? load : null;
        activity?.SetTag("gallery.load.media_index_scheduled", false);
        var location = galleryLifetime.Current;
        var galleryStore = location.GalleryStore;
        if (await galleryStore.ReadSettingsAsync<MediaIndexMarker>(SettingId, SettingPurpose, key) is not null) return;
        var imageRoot = Path.Combine(location.VaultStore.Root, "images");
        var scans = CreateScanPlan(imageRoot, records, key);
        await galleryStore.EnqueueDriveOperationsAsync(scans, key);
        await galleryStore.SaveSettingsAsync(new MediaIndexMarker(DateTimeOffset.UtcNow), SettingId, SettingPurpose, key);
        activity?.SetTag("gallery.load.media_index_scheduled", true);
    }

    internal async Task<bool> IsPendingAsync(byte[] key)
    {
        var store = galleryLifetime.Current.GalleryStore;
        return await store.ReadSettingsAsync<MediaIndexMarker>(SettingId, SettingPurpose, key) is null
            || await store.CountDriveOperationsAsync(DriveOperationKind.ScanDirectory, key) > 0;
    }

    public async Task<StorageOverview?> CachedStorageOverviewAsync()
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        var overview = await store.ReadSettingsAsync<StorageOverview>(StorageOverview.SettingId, StorageOverview.SettingPurpose, keyLease.Bytes);
        if (overview is null) return null;
        var currentRevision = await store.ReadStorageRevisionAsync(keyLease.Bytes);
        return overview with { IsStale = IsStaleAtRevision(overview, currentRevision) };
    }

    public async Task<StorageOverview> RefreshStorageOverviewAsync()
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var location = galleryLifetime.Current;
        var store = location.GalleryStore;
        var revision = await store.ReadStorageRevisionAsync(keyLease.Bytes);
        var records = await store.ReadRecordsAsync(keyLease.Bytes);
        var indexedLengthsByPathFingerprint = await store.ReadMediaIndexAsync(keyLease.Bytes);
        var indexPending = await IsPendingAsync(keyLease.Bytes);
        StorageOverview overview;
        List<(byte[] Directory, IndexedFile File)> measured;
        (overview, measured) = await Task.Run(() =>
            MeasureStorage(location.VaultStore.Root, records, indexedLengthsByPathFingerprint, keyLease.Bytes));
        await IndexMeasurementsIfCurrentAsync(store, revision, measured, keyLease.Bytes);
        var storageOverview = overview with { IsIndexPending = indexPending, CatalogRevision = revision };
        await store.SaveSettingsAsync(storageOverview, StorageOverview.SettingId, StorageOverview.SettingPurpose, keyLease.Bytes);
        var currentRevision = await store.ReadStorageRevisionAsync(keyLease.Bytes);
        return storageOverview with { IsStale = IsStaleAtRevision(storageOverview, currentRevision) };
    }

    private static (StorageOverview Overview, List<(byte[] Directory, IndexedFile File)> Measured) MeasureStorage(
        string vaultRoot, IEnumerable<ImageRecord> records, Dictionary<string, long> indexedLengthsByPathFingerprint, byte[] key)
    {
        var vaultMeasurement = MeasureVaultFiles(vaultRoot);
        var imageMeasurement = MeasureImageFiles(records, indexedLengthsByPathFingerprint, key);
        return (new(vaultMeasurement.DatabaseBytes, imageMeasurement.FileCount, imageMeasurement.Bytes,
            vaultMeasurement.FileCount, vaultMeasurement.Bytes, imageMeasurement.Missing, DateTimeOffset.UtcNow),
            imageMeasurement.Measured);
    }

    private static (long DatabaseBytes, int FileCount, long Bytes) MeasureVaultFiles(string vaultRoot)
    {
        var databasePath = Path.Combine(vaultRoot, GalleryStoreFactory.CatalogFileName);
        string[] databaseFiles = [databasePath, databasePath + "-wal", databasePath + "-shm"];
        long databaseBytes = 0, vaultBytes = 0;
        var vaultFileCount = 0;
        foreach (var path in databaseFiles.Append(Path.Combine(vaultRoot, VaultStore.HeaderFileName)))
        {
            var length = StorageFileSystem.FileLength(path);
            if (length is null) continue;
            vaultFileCount++;
            vaultBytes += length.Value;
            if (databaseFiles.Contains(path)) databaseBytes += length.Value;
        }

        return (databaseBytes, vaultFileCount, vaultBytes);
    }

    // Indexed files are counted from the database; only files the index has not seen are measured on disk.
    private static (int FileCount, long Bytes, int Missing, List<(byte[] Directory, IndexedFile File)> Measured) MeasureImageFiles(
        IEnumerable<ImageRecord> records, Dictionary<string, long> indexedLengthsByPathFingerprint, byte[] key)
    {
        long imageBytes = 0;
        int imageFileCount = 0, missing = 0;
        var measured = new List<(byte[] Directory, IndexedFile File)>();
        foreach (var path in records.Select(record => record.StoredPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            var target = DriveOperationProcessor.PathFingerprint(key, path);
            if (!indexedLengthsByPathFingerprint.TryGetValue(Convert.ToHexString(target), out var length))
            {
                var measuredLength = StorageFileSystem.FileLength(path);
                if (measuredLength is null) { missing++; continue; }
                length = measuredLength.Value;
                measured.Add((DriveOperationProcessor.PathFingerprint(key, Path.GetDirectoryName(path)!), new IndexedFile(target, length)));
            }
            imageFileCount++;
            imageBytes += length;
        }
        return (imageFileCount, imageBytes, missing, measured);
    }

    internal static async Task IndexMeasurementsIfCurrentAsync(
        IGalleryStore store, long snapshotRevision,
        IReadOnlyCollection<(byte[] Directory, IndexedFile File)> measuredFiles, byte[] key)
    {
        if (measuredFiles.Count == 0) return;
        await store.Gate.WaitAsync();
        try
        {
            if (await store.ReadStorageRevisionAsync(key) == snapshotRevision)
                await store.IndexFilesAsync(measuredFiles, key);
        }
        finally { store.Gate.Release(); }
    }

    private static bool IsStaleAtRevision(StorageOverview storageOverview, long currentRevision) =>
        storageOverview.CatalogRevision != currentRevision;

    private static List<NewDriveOperation> CreateScanPlan(
        string imageRoot, IReadOnlyCollection<ImageRecord> records, byte[] key)
    {
        var scans = new List<NewDriveOperation>
        {
            DriveOperationProcessor.Create(DriveOperationKind.ScanDirectory, imageRoot, null, key, operationIsRecursive: true)
        };
        scans.AddRange(ExternalMediaDirectories(records.Select(record => record.StoredPath), imageRoot)
            .Select(directory => DriveOperationProcessor.Create(DriveOperationKind.ScanDirectory, directory, null, key)));
        return scans;
    }

    internal static IEnumerable<string> ExternalMediaDirectories(IEnumerable<string> storedPaths, string imageRoot) =>
        storedPaths.Select(path => Path.GetDirectoryName(path)!)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(directory => !StorageFileSystem.IsPathWithin(directory, imageRoot));
}
