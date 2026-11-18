using System.Security.Cryptography;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Processors.Hashing;
using Gallery.Core.Services.Sessions;
using Gallery.Core.Services.Sessions.Locations;
using Gallery.Core.Services.Validators;

namespace Gallery.Core.Services;

internal sealed class VaultLocationCoordinator(GalleryLifetime galleryLifetime, VaultSession vaultSession, GalleryDriveQueue driveQueue)
{
    private const string CatalogFileName = GalleryStoreFactory.CatalogFileName;
    private const string VaultHeaderFileName = VaultStore.HeaderFileName;

    private long explicitLockRequests;

    private IGalleryStore galleryStore => galleryLifetime.Current.GalleryStore;
    private VaultStore vaultStore => galleryLifetime.Current.VaultStore;
    private string VaultRoot => vaultStore.Root;
    private VaultHistory? history => galleryLifetime.History;

    public void LockVault()
    {
        Interlocked.Increment(ref explicitLockRequests);
        vaultSession.Lock();
    }

    private void EnsureActive() => ObjectDisposedException.ThrowIf(galleryLifetime.IsDisposed, this);

    private async Task StopDriveAndDrainSessionAsync()
    {
        await driveQueue.StopAsync();
        await vaultSession.LockAndDrainAsync();
    }

    // Restores a session revoked for a failed location change unless a later explicit lock superseded it.
    private void RestoreUnlockedSessionAfterFailure(byte[]? key, long lockRequestsAtStart)
    {
        if (key is not null && !galleryLifetime.IsDisposed && Interlocked.Read(ref explicitLockRequests) == lockRequestsAtStart)
            vaultSession.Unlock(key);
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

    public async Task MoveVaultAsync(string directory)
    {
        var destination = CatalogDirectoryRules.NormalizeVaultDirectory(directory);
        CatalogDirectoryRules.RequireLocalCatalogDirectory(destination);
        await galleryLifetime.Gate.WaitAsync();
        byte[]? key = null;
        VaultCheckout? destinationCheckout = null;
        string? staging = null;
        var published = false;
        var lockRequestsAtStart = 0L;
        try
        {
            galleryLifetime.EnsureCheckout();
            RejectOverlappingDirectories(destination, VaultRoot);
            RequireEmptyDestination(destination);
            destinationCheckout = galleryLifetime.AcquireLocationCheckout(destination);
            RequireEmptyDestination(destination);
            lockRequestsAtStart = Interlocked.Read(ref explicitLockRequests);
            key = vaultSession.Key;
            await StopDriveAndDrainSessionAsync();
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
            await PublishStagedVaultAsync(stagedStore, key, staging, destination);
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
            finally { galleryLifetime.Gate.Release(); }
        }
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
            await GalleryDriveQueue.ReconcileOutstandingDeletesAsync(galleryStore, key);
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
    public async Task RelocateCatalogAsync(string directory)
    {
        var destination = CatalogDirectoryRules.NormalizeVaultDirectory(directory);
        CatalogDirectoryRules.RequireLocalCatalogDirectory(destination);
        await galleryLifetime.Gate.WaitAsync();
        byte[]? key = null;
        VaultCheckout? destinationCheckout = null;
        string? staging = null;
        var published = false;
        var lockRequestsAtStart = 0L;
        try
        {
            galleryLifetime.EnsureCheckout();
            RejectOverlappingDirectories(destination, VaultRoot);
            RequireEmptyDestination(destination);
            destinationCheckout = galleryLifetime.AcquireLocationCheckout(destination);
            RequireEmptyDestination(destination);
            lockRequestsAtStart = Interlocked.Read(ref explicitLockRequests);
            key = vaultSession.Key;
            await StopDriveAndDrainSessionAsync();
            var mediaOptions = await galleryStore.ReadSettingsAsync<MediaStorageOptions>(
                MediaStorageOptions.SettingId, MediaStorageOptions.SettingPurpose, key);
            var mediaDirectory = mediaOptions is { MediaDirectory.Length: > 0 } ? mediaOptions.MediaDirectory : VaultRoot;
            var sourceRoot = VaultRoot;
            staging = Path.Combine(destination, ".catalog-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            await vaultStore.CopyHeaderAsync(Path.Combine(staging, VaultHeaderFileName));
            await galleryStore.CreateSnapshotAsync(Path.Combine(staging, CatalogFileName), key);
            var stagedStore = galleryLifetime.StoreFactory.Create(staging);
            await stagedStore.SaveSettingsAsync(new MediaStorageOptions { MediaDirectory = mediaDirectory },
                MediaStorageOptions.SettingId, MediaStorageOptions.SettingPurpose, key);
            await stagedStore.DeleteSettingsAsync(key, StorageOverview.SettingId);
            var records = await stagedStore.ReadRecordsAsync(key);
            var sourceRecords = await galleryStore.ReadRecordsAsync(key);
            if (!CatalogRecordsMatch(records, sourceRecords))
                throw new IOException("The copied catalog did not match the original; relocation was stopped.");
            await PublishStagedVaultAsync(stagedStore, key, staging, destination);
            published = true;
            var sourceCheckout = galleryLifetime.PublishLocation(
                galleryLifetime.StoreFactory.Create(destination), new VaultStore(destination), destinationCheckout);
            destinationCheckout = null;
            try
            {
                history?.Save(destination);
                foreach (var name in new[]
                    { CatalogFileName + "-wal", CatalogFileName + "-shm", CatalogFileName, VaultHeaderFileName })
                    File.Delete(Path.Combine(sourceRoot, name));
            }
            finally { sourceCheckout?.Dispose(); }
        }
        catch (Exception exception)
        {
            if (published)
                throw new IOException($"The local catalog is intact at '{destination}', but old catalog cleanup or history persistence failed. Media files were not moved.", exception);
            RestoreUnlockedSessionAfterFailure(key, lockRequestsAtStart);
            throw;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            destinationCheckout?.Dispose();
            try { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            finally { galleryLifetime.Gate.Release(); }
        }
    }

    private static bool CatalogRecordsMatch(List<ImageRecord> copied, List<ImageRecord> source) =>
        copied.Count == source.Count && copied.OrderBy(record => record.Id).Zip(source.OrderBy(record => record.Id))
            .All(pair => (pair.First with { Collections = [] }) == (pair.Second with { Collections = [] })
                && pair.First.Collections.SequenceEqual(pair.Second.Collections));

    public Task<IReadOnlyList<string>> ListRestoreBackupsAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString, HttpClient? httpClient) =>
        CreateRestoreTarget(sourceType, localDirectory, azureContainer, azurePrefix, connectionString, httpClient).ListAsync();

    public Task<IReadOnlyList<string>> ListRestoreBackupsFromSasAsync(string sasUrl, string azurePrefix, HttpClient? httpClient) =>
        CreateSasRestoreTarget(sasUrl, azurePrefix, httpClient).ListAsync();

    private static IBackupTarget CreateSasRestoreTarget(string sasUrl, string azurePrefix, HttpClient? httpClient) =>
        new BackupTargetAzure(sasUrl, BackupTargetAzure.NormalizePrefix(azurePrefix), httpClient);

    private static IBackupTarget CreateRestoreTarget(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString, HttpClient? httpClient)
    {
        if (sourceType == "local")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
            return new BackupTargetLocal(Path.GetFullPath(localDirectory.Trim()));
        }
        if (sourceType == "azure")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(azureContainer);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            return new BackupTargetAzure(connectionString.Trim(), azureContainer.Trim(), BackupTargetAzure.NormalizePrefix(azurePrefix), httpClient);
        }
        throw new InvalidOperationException("Choose a local/SMB folder or Azure Blob Storage.");
    }

    public async Task RestoreVaultAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix,
        string connectionString, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery, string? name, HttpClient? httpClient)
    {
        var target = CreateRestoreTarget(sourceType, localDirectory, azureContainer, azurePrefix, connectionString, httpClient);
        await RestoreVaultFromTargetAsync(target, latestBackupName, destinationDirectory, credential, useRecovery,
            name, sourceType == "local" ? localDirectory : null);
    }

    public Task RestoreGalleryFromSasAsync(
        string sasUrl, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery, string? name, string azurePrefix, HttpClient? httpClient) =>
        RestoreVaultFromTargetAsync(CreateSasRestoreTarget(sasUrl, azurePrefix, httpClient), latestBackupName, destinationDirectory,
            credential, useRecovery, name, sourceDirectory: null);

    private async Task RestoreVaultFromTargetAsync(
        IBackupTarget target, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery, string? name, string? sourceDirectory)
    {
        target.ValidateArchiveName(latestBackupName);
        if (useRecovery) credential = RecoveryPhraseValidator.Normalize(credential);
        var destination = CatalogDirectoryRules.NormalizeVaultDirectory(destinationDirectory);
        CatalogDirectoryRules.RequireLocalCatalogDirectory(destination);
        await galleryLifetime.Gate.WaitAsync();
        VaultCheckout? destinationCheckout = null;
        byte[]? key = null;
        string? staging = null;
        try
        {
            EnsureActive();
            RejectOverlappingDirectories(destination, VaultRoot);
            if (sourceDirectory is not null)
                RejectOverlappingDirectories(destination, Path.GetFullPath(sourceDirectory.Trim()));
            RequireEmptyDestination(destination);
            destinationCheckout = galleryLifetime.AcquireLocationCheckout(destination);
            RequireEmptyDestination(destination);
            staging = Path.Combine(destination, ".vault-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            IGalleryStore store;
            (store, key) = await BackupArchive.StageRestoreAsync(
                galleryLifetime.StoreFactory, target, latestBackupName, staging, destination, credential, useRecovery);
            await ClearLocationSettingsAsync(store, key);
            if (name is not null) await new VaultStore(staging).RenameAsync(name);
            await StopDriveAndDrainSessionAsync();
            await PublishStagedVaultAsync(store, key, staging, destination);
            galleryLifetime.PublishLocation(galleryLifetime.StoreFactory.Create(destination),
                new VaultStore(destination), destinationCheckout)?.Dispose();
            destinationCheckout = null;
            history?.Save(destination);
            // Restore returns to regular sign-in; credentials and plaintext never survive the operation.
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            destinationCheckout?.Dispose();
            try { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            finally { galleryLifetime.Gate.Release(); }
        }
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

    private async Task PublishStagedVaultAsync(IGalleryStore store, byte[] key, string staging, string destination)
    {
        EnsureActive();
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
}
