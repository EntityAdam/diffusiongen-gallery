using Gallery.Core.Models;
using Gallery.Core.Services.Ports.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Sessions;

namespace Gallery.Core.Services;

internal sealed class MediaStorageIndex(GalleryLifetime galleryLifetime, VaultSession vaultSession, GalleryDriveQueue driveQueue)
{
    internal const string SettingId = "media-index";
    private const string SettingPurpose = "media-index:v1";

    private sealed record MediaIndexMarker(DateTimeOffset ScheduledAt);

    internal async Task RebuildAsync()
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        var records = await store.ReadRecordsAsync(keyLease.Bytes);
        await store.ClearMediaIndexAsync(keyLease.Bytes);
        await store.DeleteSettingsAsync(keyLease.Bytes, SettingId);
        await ScheduleAsync(records, keyLease.Bytes);
        driveQueue.Start();
    }

    /// <summary>Schedules the one-time scan that seeds the media index; later loads read the index instead of the drive.</summary>
    internal async Task ScheduleAsync(IReadOnlyCollection<ImageRecord> records, byte[] key)
    {
        var activity = System.Diagnostics.Activity.Current is { OperationName: "gallery.load" } load ? load : null;
        activity?.SetTag("gallery.load.media_index_scheduled", false);
        var location = galleryLifetime.Current;
        var galleryStore = location.GalleryStore;
        if (await galleryStore.ReadSettingsAsync<MediaIndexMarker>(SettingId, SettingPurpose, key) is not null) return;
        var imageRoot = Path.Combine(location.VaultStore.Root, "images");
        var scans = new List<NewDriveOperation> { DriveOperationProcessor.Create(DriveOperationKind.ScanDirectory, imageRoot, null, key, operationIsRecursive: true) };
        scans.AddRange(ExternalMediaDirectories(records.Select(record => record.StoredPath), imageRoot)
            .Select(directory => DriveOperationProcessor.Create(DriveOperationKind.ScanDirectory, directory, null, key)));
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

    internal static IEnumerable<string> ExternalMediaDirectories(IEnumerable<string> storedPaths, string imageRoot) =>
        storedPaths.Select(path => Path.GetDirectoryName(path)!)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(directory => !StorageFileSystem.IsPathWithin(directory, imageRoot));
}
