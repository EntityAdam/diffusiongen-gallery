using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Sessions.Locations;
using System.Security.Cryptography;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Processors.Hashing;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    private SemaphoreSlim lifecycleGate => galleryLifetime.Gate;
    private VaultCheckout? checkout => galleryLifetime.Current.Checkout;
    private VaultHistory? history => galleryLifetime.History;

    public static string DefaultApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionGenGallery");
    public string VaultName => vaultStore.Name;

    public IReadOnlyList<GalleryLocation> KnownGalleries
    {
        get
        {
            var roots = (history?.ReadGalleries() ?? []).Prepend(VaultRoot)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var galleries = new List<GalleryLocation>();
            foreach (var root in roots)
            {
                try
                {
                    var store = new VaultStore(root);
                    if (store.Exists && File.Exists(Path.Combine(root, CatalogFileName)))
                        galleries.Add(new(root, store.Name));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { }
            }
            return galleries;
        }
    }

    public static string DefaultVaultDirectory() =>
        Path.Combine(DefaultApplicationDirectory, "galleries", Guid.NewGuid().ToString("N"));

    private void EnsureActive() => ObjectDisposedException.ThrowIf(galleryLifetime.IsDisposed, this);
    private void EnsureCheckout()
    {
        EnsureActive();
        galleryLifetime.EnsureCheckout();
    }

    public Task OpenExistingVaultAsync(string directory) => SelectVaultAsync(directory, existing: true);
    public Task SelectNewVaultAsync(string directory) => SelectVaultAsync(directory, existing: false);

    private async Task SelectVaultAsync(string directory, bool existing)
    {
        var root = NormalizeVaultDirectory(directory);
        if (!existing) RequireLocalCatalogDirectory(root);
        await lifecycleGate.WaitAsync();
        try
        {
            EnsureActive();
            if (StorageFileSystem.PathEquals(root, VaultRoot) && checkout is not null)
            {
                if (!existing)
                {
                    VaultManager.RecoverInterruptedCreation(root);
                    RequireEmptyDestination(root);
                }
                await driveQueue.StopAsync();
                await vaultSession.LockAndDrainAsync();
                if (existing) history?.Save(root);
                return;
            }
            var store = new VaultStore(root);
            if (existing && (!store.Exists || !File.Exists(Path.Combine(root, CatalogFileName))))
                throw new InvalidOperationException("Select a gallery folder containing vault.json and gallery.db.");
            VaultCheckout? nextCheckout = galleryLifetime.AcquireLocationCheckout(root);
            try
            {
                if (!existing)
                {
                    VaultManager.RecoverInterruptedCreation(root);
                    RequireEmptyDestination(root);
                }
                await driveQueue.StopAsync();
                await vaultSession.LockAndDrainAsync();
                EnsureActive();
                galleryLifetime.PublishLocation(galleryLifetime.StoreFactory.Create(root), store, nextCheckout)?.Dispose();
                nextCheckout = null; // The published location now owns the checkout, including on history-write failure.
                if (existing) history?.Save(root);
            }
            catch { nextCheckout?.Dispose(); throw; }
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task CloseVaultAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            try { await driveQueue.StopAsync(); }
            finally
            {
                await vaultSession.LockAndDrainAsync();
                galleryLifetime.ReleaseCheckout();
            }
        }
        finally { lifecycleGate.Release(); }
    }

    public Task RenameVaultAsync(string name) => vaultManager.RenameVaultAsync(name);

    public async Task MoveVaultAsync(string directory)
    {
        var destination = NormalizeVaultDirectory(directory);
        RequireLocalCatalogDirectory(destination);
        await lifecycleGate.WaitAsync();
        byte[]? key = null;
        VaultCheckout? destinationCheckout = null;
        string? staging = null;
        var published = false;
        var lockRequestsAtStart = 0L;
        try
        {
            EnsureCheckout();
            RejectOverlappingDirectories(destination, VaultRoot);
            RequireEmptyDestination(destination);
            destinationCheckout = galleryLifetime.AcquireLocationCheckout(destination);
            RequireEmptyDestination(destination);
            lockRequestsAtStart = ReadExplicitLockRequests();
            key = vaultSession.Key;
            await driveQueue.StopAsync();
            await vaultSession.LockAndDrainAsync();
            var sourceRoot = VaultRoot;
            await GalleryDriveQueue.ReconcileOutstandingDeletesAsync(galleryStore, key);
            var records = await galleryStore.ReadRecordsAsync(key);
            staging = Path.Combine(destination, ".vault-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            await vaultStore.CopyHeaderAsync(Path.Combine(staging, VaultHeaderFileName));
            await galleryStore.CreateSnapshotAsync(Path.Combine(staging, CatalogFileName), key);
            var stagedStore = galleryLifetime.StoreFactory.Create(staging);
            foreach (var record in records)
            {
                ValidateMediaPath(record);
                var relative = StoredMediaProcessor.RelativePath(record);
                var target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(record.StoredPath, target, overwrite: false);
                if (await FileSha256.ComputeAsync(record.StoredPath) != await FileSha256.ComputeAsync(target))
                    throw new IOException($"Media verification failed for '{record.Name}'. The source gallery was not removed.");
                await stagedStore.UpdateAsync(record with { StoredPath = Path.Combine(destination, relative) }, key);
            }
            await ClearLocationSettingsAsync(stagedStore, key);
            EnsureActive();
            await stagedStore.PrepareForPublicationAsync(key);
            PublishStagedVault(staging, destination);
            published = true;
            var sourceCheckout = galleryLifetime.PublishLocation(
                galleryLifetime.StoreFactory.Create(destination), new VaultStore(destination), destinationCheckout);
            destinationCheckout = null;
            try
            {
                history?.Save(destination);
                DeleteVaultData(sourceRoot, records);
            }
            finally { sourceCheckout?.Dispose(); }
        }
        catch (Exception exception)
        {
            // Until publication, the original vault remains intact and can be unlocked again.
            if (published)
                throw new IOException($"The gallery was moved to '{destination}', but old-location cleanup or history persistence failed. The verified destination is intact; inspect the original location before removing any remaining files.", exception);
            RestoreUnlockedSessionAfterFailure(key, lockRequestsAtStart);
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

    public async Task PurgeVaultAsync(string confirmation)
    {
        await lifecycleGate.WaitAsync();
        byte[]? key = null;
        try
        {
            if (confirmation != "PURGE")
                throw new InvalidOperationException("Type PURGE to confirm permanent destruction of this gallery.");
            EnsureCheckout();
            key = vaultSession.Key;
            await driveQueue.StopAsync();
            await vaultSession.LockAndDrainAsync();
            await GalleryDriveQueue.ReconcileOutstandingDeletesAsync(galleryStore, key);
            var records = await galleryStore.ReadRecordsAsync(key);
            foreach (var record in records) ValidateMediaPath(record);
            DeleteVaultData(VaultRoot, records);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            lifecycleGate.Release();
        }
    }

    private static string NormalizeVaultDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
        if (StorageFileSystem.PathEquals(root, Path.GetPathRoot(root)!))
            throw new InvalidOperationException("A filesystem or share root cannot be a gallery. Select a dedicated folder.");
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Gallery directories cannot use symbolic links or directory junctions.");
        return root;
    }

    private static void RejectOverlappingDirectories(string destination, string source)
    {
        if (StorageFileSystem.IsPathWithin(destination, source) || StorageFileSystem.IsPathWithin(source, destination))
            throw new InvalidOperationException($"Destination '{destination}' overlaps the current gallery folder '{source}'; choose a separate destination.");
    }

    private static void RequireEmptyDestination(string directory)
    {
        if (File.Exists(directory) || Directory.Exists(directory) &&
            Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) != ".vault-checkout"))
            throw new InvalidOperationException($"Destination '{directory}' is not empty; choose a new or empty destination folder. Existing files will not be overwritten.");
    }

    private static void ValidateMediaPath(ImageRecord record)
    {
        var expected = record.IsVideo ? ".dmp4" : ".dpng";
        if (!string.Equals(Path.GetExtension(record.StoredPath), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected encrypted media path for '{record.Name}'.");
        if (File.Exists(record.StoredPath) && File.GetAttributes(record.StoredPath).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Encrypted media cannot be a symbolic link.");
    }

    private static void DeleteVaultData(string root, IEnumerable<ImageRecord> records)
    {
        var media = records.Select(record => record.StoredPath).ToHashSet(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var imagesDirectory = Path.Combine(root, "images");
        if (Directory.Exists(imagesDirectory))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var path in Directory.EnumerateFiles(imagesDirectory, "*", options))
                if (Path.GetExtension(path) is ".dpng" or ".dmp4"
                    || path.Contains(GalleryDriveQueue.RemovalStagingMarker, StringComparison.Ordinal))
                    media.Add(path);
        }
        foreach (var path in media) File.Delete(path);
        if (Directory.Exists(imagesDirectory))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var directory in Directory.EnumerateDirectories(imagesDirectory, "*", options).OrderByDescending(path => path.Length))
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            if (!Directory.EnumerateFileSystemEntries(imagesDirectory).Any()) Directory.Delete(imagesDirectory);
        }
        // Delete the header last; a partial failure remains an identifiable, checked-out vault.
        foreach (var name in new[] { CatalogFileName + "-wal", CatalogFileName + "-shm", CatalogFileName, VaultHeaderFileName })
            File.Delete(Path.Combine(root, name));
    }

    private static void PublishStagedVault(string staging, string destination)
    {
        var moved = new List<string>();
        try
        {
            foreach (var name in new[] { "images", CatalogFileName, VaultHeaderFileName })
            {
                var source = Path.Combine(staging, name);
                var target = Path.Combine(destination, name);
                if (Directory.Exists(source)) Directory.Move(source, target);
                else if (File.Exists(source)) File.Move(source, target);
                else continue;
                moved.Add(name);
            }
        }
        catch
        {
            foreach (var name in moved.AsEnumerable().Reverse())
            {
                var source = Path.Combine(destination, name);
                var target = Path.Combine(staging, name);
                if (Directory.Exists(source)) Directory.Move(source, target);
                else File.Move(source, target);
            }
            throw;
        }
    }

    // Queued drive work and the file index describe paths at the old location, so they never travel with a copy.
    private static async Task ClearLocationSettingsAsync(IGalleryStore store, byte[] key)
    {
        await store.DeleteSettingsAsync(key, StorageOverviewId, MediaStorageIndex.SettingId, "media-storage");
        await store.ClearDriveStateAsync(key);
        var settings = await store.ReadSettingsAsync<StoredBackupSettings>(StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, key);
        if (settings is not null)
            await store.SaveSettingsAsync(settings with { LastBackupName = null, LastBackupType = null },
                StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, key);
    }
}
