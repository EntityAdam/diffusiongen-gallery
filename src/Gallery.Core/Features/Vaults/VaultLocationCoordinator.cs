using System.Security.Cryptography;
using Gallery.Core.Features.Backups.Targets;
using Gallery.Core.Features.Backups;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Settings;
using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Features.Storage;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Shared.Files;
using GalleryLocation = Gallery.Core.Models.GalleryLocation;

namespace Gallery.Core.Features.Vaults;

internal sealed class VaultLocationCoordinator(
    GalleryLifetime galleryLifetime, VaultSession vaultSession, GalleryDriveQueue driveQueue, VaultAccess vaultAccess)
{
    private const string CatalogFileName = GalleryStoreFactory.CatalogFileName;
    private const string VaultHeaderFileName = VaultStore.HeaderFileName;

    private IGalleryStore galleryStore => galleryLifetime.Current.GalleryStore;
    private VaultStore vaultStore => galleryLifetime.Current.VaultStore;
    private string VaultRoot => vaultStore.Root;
    private VaultHistory? history => galleryLifetime.History;

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

    private void EnsureActive() => ObjectDisposedException.ThrowIf(galleryLifetime.IsDisposed, this);

    private async Task StopDriveAndDrainSessionAsync()
    {
        await driveQueue.StopAsync();
        await vaultSession.LockAndDrainAsync();
    }

    public Task OpenExistingVaultAsync(string directory) => SelectVaultAsync(directory, existing: true);
    public Task SelectNewVaultAsync(string directory) => SelectVaultAsync(directory, existing: false);

    private async Task SelectVaultAsync(string directory, bool existing)
    {
        var root = CatalogDirectoryRules.NormalizeVaultDirectory(directory);
        if (!existing) CatalogDirectoryRules.RequireLocalCatalogDirectory(root);
        await galleryLifetime.Gate.WaitAsync();
        try
        {
            EnsureActive();
            if (StorageFileSystem.PathEquals(root, VaultRoot) && galleryLifetime.Current.Checkout is not null)
            {
                if (!existing)
                {
                    VaultManager.RecoverInterruptedCreation(root);
                    RequireEmptyDestination(root);
                }
                await StopDriveAndDrainSessionAsync();
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
                await StopDriveAndDrainSessionAsync();
                EnsureActive();
                galleryLifetime.PublishLocation(galleryLifetime.StoreFactory.Create(root), store, nextCheckout)?.Dispose();
                nextCheckout = null; // The published location now owns the checkout, including on history-write failure.
                if (existing) history?.Save(root);
            }
            catch { nextCheckout?.Dispose(); throw; }
        }
        finally { galleryLifetime.Gate.Release(); }
    }

    public async Task CloseVaultAsync()
    {
        await galleryLifetime.Gate.WaitAsync();
        try
        {
            try { await driveQueue.StopAsync(); }
            finally
            {
                await vaultSession.LockAndDrainAsync();
                galleryLifetime.ReleaseCheckout();
            }
        }
        finally { galleryLifetime.Gate.Release(); }
    }

    public Task MoveVaultAsync(string directory) => RelocateUnlockedVaultAsync(directory, StageVaultMoveAsync,
        destination => $"The gallery was moved to '{destination}', but old-location cleanup or history persistence failed. The verified destination is intact; inspect the original location before removing any remaining files.");

    private async Task<(IGalleryStore Store, Action CleanSource)> StageVaultMoveAsync(StagedLocation location, byte[] key)
    {
        var sourceRoot = VaultRoot;
        await QueuedFileDeletion.ReconcileOutstandingDeletesAsync(galleryStore, key);
        var records = await galleryStore.ReadRecordsAsync(key);
        var staging = location.CreateStagingDirectory(".vault-stage-");
        await CopyCatalogToStageAsync(vaultStore, galleryStore, staging, key);
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
            await stagedStore.UpdateAsync(record with { StoredPath = Path.Combine(location.Destination, relative) }, key);
        }
        await ClearLocationSettingsAsync(stagedStore, key);
        return (stagedStore, () => DeleteVaultData(sourceRoot, records));
    }

    public async Task PurgeVaultAsync(string confirmation)
    {
        await galleryLifetime.Gate.WaitAsync();
        byte[]? key = null;
        try
        {
            if (confirmation != "PURGE")
                throw new InvalidOperationException("Type PURGE to confirm permanent destruction of this gallery.");
            galleryLifetime.EnsureCheckout();
            key = vaultSession.Key;
            await StopDriveAndDrainSessionAsync();
            await QueuedFileDeletion.ReconcileOutstandingDeletesAsync(galleryStore, key);
            var records = await galleryStore.ReadRecordsAsync(key);
            foreach (var record in records) ValidateMediaPath(record);
            DeleteVaultData(VaultRoot, records);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            galleryLifetime.Gate.Release();
        }
    }

    /// <summary>Relocates only the encrypted catalog and header; all encrypted media stays at its existing paths.</summary>
    public Task RelocateCatalogAsync(string directory) => RelocateUnlockedVaultAsync(directory, StageCatalogRelocationAsync,
        destination => $"The local catalog is intact at '{destination}', but old catalog cleanup or history persistence failed. Media files were not moved.");

    private async Task<(IGalleryStore Store, Action CleanSource)> StageCatalogRelocationAsync(StagedLocation location, byte[] key)
    {
        var mediaDirectory = await GallerySettings.ReadMediaDirectoryAsync(galleryLifetime.Current, key);
        var sourceRoot = VaultRoot;
        var staging = location.CreateStagingDirectory(".catalog-stage-");
        await CopyCatalogToStageAsync(vaultStore, galleryStore, staging, key);
        var stagedStore = galleryLifetime.StoreFactory.Create(staging);
        var original = await galleryStore.ComputeSnapshotFingerprintAsync(Path.Combine(sourceRoot, CatalogFileName), "", key);
        var copied = await stagedStore.ComputeSnapshotFingerprintAsync(Path.Combine(staging, CatalogFileName), "", key);
        if (!StringComparer.Ordinal.Equals(original, copied))
            throw new IOException("The copied catalog did not match the original; relocation was stopped.");
        await stagedStore.SaveSettingsAsync(new MediaStorageOptions { MediaDirectory = mediaDirectory },
            MediaStorageOptions.SettingId, MediaStorageOptions.SettingPurpose, key);
        await stagedStore.DeleteSettingsAsync(key, StorageOverview.SettingId);
        return (stagedStore, () => DeleteCatalogFiles(sourceRoot));
    }

    private async Task RelocateUnlockedVaultAsync(string directory,
        Func<StagedLocation, byte[], Task<(IGalleryStore Store, Action CleanSource)>> prepare,
        Func<string, string> publishedFailureMessage)
    {
        var destination = CatalogDirectoryRules.NormalizeVaultDirectory(directory);
        CatalogDirectoryRules.RequireLocalCatalogDirectory(destination);
        await galleryLifetime.Gate.WaitAsync();
        byte[]? key = null;
        StagedLocation? location = null;
        var lockRequestsAtStart = 0L;
        var activated = false;
        try
        {
            galleryLifetime.EnsureCheckout();
            location = AcquireStagedLocation(destination);
            lockRequestsAtStart = vaultAccess.ExplicitLockRequests;
            key = vaultSession.Key;
            await StopDriveAndDrainSessionAsync();

            var (stagedStore, cleanSource) = await prepare(location, key);
            EnsureActive();
            await location.PublishFilesAsync(stagedStore, key);
            using var sourceCheckout = ActivatePublishedLocation(location);
            activated = true;
            history?.Save(destination);
            cleanSource();
        }
        catch (Exception exception)
        {
            if (activated)
                throw new IOException(publishedFailureMessage(destination), exception);
            if (location?.FilesPublished == true)
                throw new IOException($"The verified gallery files were published to '{destination}', but the destination could not be activated. The source was not removed.", exception);
            vaultAccess.RestoreUnlockedSessionAfterFailure(key, lockRequestsAtStart);
            throw;
        }
        finally { FinishStagedLocation(location, key); }
    }

    internal async Task RestoreVaultFromTargetAsync(
        IBackupTarget target, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery, string? name, string? sourceDirectory)
    {
        target.ValidateArchiveName(latestBackupName);
        if (useRecovery) credential = RecoveryPhraseValidator.Normalize(credential);
        var destination = CatalogDirectoryRules.NormalizeVaultDirectory(destinationDirectory);
        CatalogDirectoryRules.RequireLocalCatalogDirectory(destination);
        await galleryLifetime.Gate.WaitAsync();
        StagedLocation? stagedLocation = null;
        byte[]? key = null;
        try
        {
            EnsureActive();
            stagedLocation = AcquireStagedLocation(destination, sourceDirectory);
            var staging = stagedLocation.CreateStagingDirectory(".vault-stage-");
            IGalleryStore store;
            (store, key) = await BackupRestoreStaging.StageRestoreAsync(
                galleryLifetime.StoreFactory, target, latestBackupName, staging, destination, credential, useRecovery);
            await ClearLocationSettingsAsync(store, key);
            if (name is not null) await new VaultStore(staging).RenameAsync(name);
            await StopDriveAndDrainSessionAsync();
            EnsureActive();
            await stagedLocation.PublishFilesAsync(store, key);
            ActivatePublishedLocation(stagedLocation)?.Dispose();
            history?.Save(destination);
            // Restore returns to regular sign-in; credentials and plaintext never survive the operation.
        }
        finally { FinishStagedLocation(stagedLocation, key); }
    }

    private static async Task CopyCatalogToStageAsync(
        VaultStore header, IGalleryCatalogSnapshots snapshots, string staging, byte[] key)
    {
        await header.CopyHeaderAsync(Path.Combine(staging, VaultHeaderFileName));
        await snapshots.CreateSnapshotAsync(Path.Combine(staging, CatalogFileName), key);
    }

    private StagedLocation AcquireStagedLocation(string destination, string? sourceDirectory = null)
    {
        RejectOverlappingDirectories(destination, VaultRoot);
        if (sourceDirectory is not null)
            RejectOverlappingDirectories(destination, Path.GetFullPath(sourceDirectory.Trim()));

        RequireEmptyDestination(destination);
        var checkout = galleryLifetime.AcquireLocationCheckout(destination);
        try
        {
            RequireEmptyDestination(destination);
            return new StagedLocation(destination, checkout);
        }
        catch
        {
            checkout.Dispose();
            throw;
        }
    }

    private void FinishStagedLocation(StagedLocation? stagedLocation, byte[]? key)
    {
        try
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            stagedLocation?.Dispose();
        }
        finally { galleryLifetime.Gate.Release(); }
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
        DeleteCatalogFiles(root);
    }

    private static void DeleteCatalogFiles(string root)
    {
        // Delete the header last so a partial failure leaves an identifiable vault.
        foreach (var name in new[] { CatalogFileName + "-wal", CatalogFileName + "-shm", CatalogFileName, VaultHeaderFileName })
            File.Delete(Path.Combine(root, name));
    }

    // Queued drive work and the file index describe paths at the old location, so they never travel with a copy.
    private static async Task ClearLocationSettingsAsync(IGalleryStore store, byte[] key)
    {
        await store.DeleteSettingsAsync(key, StorageOverview.SettingId, MediaStorageIndex.SettingId, MediaStorageOptions.SettingId);
        await store.ClearDriveStateAsync(key);
        var settings = await store.ReadSettingsAsync<StoredBackupSettings>(StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, key);
        if (settings is not null)
            await store.SaveSettingsAsync(settings with { LastBackupName = null, LastBackupType = null },
                StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, key);
    }

    private VaultCheckout? ActivatePublishedLocation(StagedLocation location)
    {
        var destination = location.Destination;
        var retired = galleryLifetime.PublishLocation(
            galleryLifetime.StoreFactory.Create(destination), new VaultStore(destination), location.Checkout);
        location.ReleaseCheckoutOwnership();
        return retired;
    }

    private sealed class StagedLocation(string destination, VaultCheckout destinationCheckout) : IDisposable
    {
        private VaultCheckout? checkout = destinationCheckout;
        private string? stagingDirectory;

        public string Destination => destination;
        public bool FilesPublished { get; private set; }
        public VaultCheckout? Checkout => checkout;

        public void ReleaseCheckoutOwnership() => checkout = null;

        public string CreateStagingDirectory(string prefix)
        {
            stagingDirectory = Path.Combine(destination, prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            return stagingDirectory;
        }

        public async Task PublishFilesAsync(IGalleryStore store, byte[] key)
        {
            var staging = stagingDirectory ?? throw new InvalidOperationException("Create the staging directory before publishing.");
            await store.PrepareForPublicationAsync(key);
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

            FilesPublished = true;
        }

        public void Dispose()
        {
            try { checkout?.Dispose(); }
            finally
            {
                if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }
}
