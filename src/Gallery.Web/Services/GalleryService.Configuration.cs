using Gallery.Web.Models;
using Gallery.Web.Services.DataManagers;
using System.Security.Cryptography;

namespace Gallery.Web.Services;

public sealed partial class GalleryService
{
    public bool CatalogIsOnNetworkDrive => GalleryStore.IsNetworkDirectory(VaultRoot);

    /// <summary>Relocates only the encrypted catalog and header; all encrypted media stays at its existing paths.</summary>
    public async Task RelocateCatalogAsync(string directory)
    {
        var destination = NormalizeVaultDirectory(directory);
        RequireLocalCatalogDirectory(destination);
        await lifecycleGate.WaitAsync();
        byte[]? key = null;
        VaultCheckout? destinationCheckout = null;
        string? staging = null;
        var published = false;
        try
        {
            EnsureCheckout();
            RejectOverlappingDirectories(destination, VaultRoot);
            RequireEmptyDestination(destination);
            destinationCheckout = VaultCheckout.Acquire(destination);
            RequireEmptyDestination(destination);
            key = vaultSession.Key;
            await StopDrivePlayerAsync();
            await vaultSession.LockAndDrainAsync();
            var mediaDirectory = await MediaDirectoryWithKeyAsync(key);
            var sourceRoot = VaultRoot;
            staging = Path.Combine(destination, ".catalog-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            await vaultStore.CopyHeaderAsync(Path.Combine(staging, "vault.json"));
            await galleryStore.CreateSnapshotAsync(Path.Combine(staging, "gallery.db"));
            var stagedStore = new GalleryStore(Path.Combine(staging, "gallery.db"));
            await stagedStore.SaveSettingsAsync(new MediaStorageOptions { MediaDirectory = mediaDirectory },
                "media-storage", "media-storage:v1", key);
            await stagedStore.DeleteSettingsAsync(StorageOverviewId);
            var records = await stagedStore.ReadRecordsAsync(key);
            var sourceRecords = await galleryStore.ReadRecordsAsync(key);
            if (!CatalogRecordsMatch(records, sourceRecords))
                throw new IOException("The copied catalog did not match the original; relocation was stopped.");
            EnsureActive();
            PublishStagedVault(staging, destination);
            published = true;
            var sourceCheckout = checkout;
            checkout = destinationCheckout;
            destinationCheckout = null;
            vaultStore = new VaultStore(destination);
            galleryStore = new GalleryStore(Path.Combine(destination, "gallery.db"));
            try
            {
                history?.Save(destination);
                foreach (var name in new[] { "gallery.db-wal", "gallery.db-shm", "gallery.db", "vault.json" })
                    File.Delete(Path.Combine(sourceRoot, name));
            }
            finally { sourceCheckout?.Dispose(); }
        }
        catch (Exception exception)
        {
            if (published)
                throw new IOException($"The local catalog is intact at '{destination}', but old catalog cleanup or history persistence failed. Media files were not moved.", exception);
            if (key is not null && Volatile.Read(ref disposalStarted) == 0) vaultSession.Unlock(key);
            throw;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            destinationCheckout?.Dispose();
            try { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            finally { lifecycleGate.Release(); }
        }
    }

    private static bool CatalogRecordsMatch(List<ImageRecord> copied, List<ImageRecord> source) =>
        copied.Count == source.Count && copied.OrderBy(record => record.Id).Zip(source.OrderBy(record => record.Id))
            .All(pair => (pair.First with { Collections = [] }) == (pair.Second with { Collections = [] })
                && pair.First.Collections.SequenceEqual(pair.Second.Collections));

    public async Task<IngestionOptions> LoadIngestionOptionsAsync() =>
        await ReadEncryptedSettingAsync<IngestionOptions>("ingestion-options", "ingestion-options:v1") ?? new();

    public Task SaveIngestionOptionsAsync(IngestionOptions options)
    {
        options.Validate();
        return WriteEncryptedSettingAsync(options, "ingestion-options", "ingestion-options:v1");
    }

    public async Task<MediaStorageOptions> LoadMediaStorageOptionsAsync() =>
        await ReadEncryptedSettingAsync<MediaStorageOptions>("media-storage", "media-storage:v1") ?? new();

    public async Task SaveMediaStorageOptionsAsync(MediaStorageOptions options)
    {
        options = NormalizeMediaStorageOptions(options);
        await WriteEncryptedSettingAsync(options, "media-storage", "media-storage:v1");
    }

    private static MediaStorageOptions NormalizeMediaStorageOptions(MediaStorageOptions options)
    {
        var directory = options.MediaDirectory.Trim();
        if (directory.Length > 0)
        {
            directory = NormalizeVaultDirectory(directory);
            if (!Directory.Exists(directory))
                throw new InvalidOperationException("Choose an existing directory for encrypted media.");
        }
        return options with { MediaDirectory = directory };
    }

    private async Task<string> MediaDirectoryWithKeyAsync(byte[] key)
    {
        var options = await galleryStore.ReadSettingsAsync<MediaStorageOptions>("media-storage", "media-storage:v1", key);
        return options is { MediaDirectory.Length: > 0 } ? options.MediaDirectory : VaultRoot;
    }

    private static void RequireLocalCatalogDirectory(string directory)
    {
        if (GalleryStore.IsNetworkDirectory(directory))
            throw new InvalidOperationException(
                "Keep gallery.db on the host's local drive. Choose a local catalog directory; encrypted media may use a NAS/SMB directory.");
    }
}
