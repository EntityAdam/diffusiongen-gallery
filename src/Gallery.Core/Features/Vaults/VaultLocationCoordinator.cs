using Gallery.Core.Features.Media.Processors;
using System.Security.Cryptography;
using Gallery.Core.Features.Backups.Targets;
using Gallery.Core.Features.Backups;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Settings;
using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Features.Storage;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults.Publication;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Shared.Files;
using GalleryLocation = Gallery.Core.Models.GalleryLocation;

namespace Gallery.Core.Features.Vaults;

internal sealed class VaultLocationCoordinator(
    GalleryLifetime galleryLifetime, VaultSession vaultSession, GalleryDriveQueue driveQueue, VaultSessionLockPolicy sessionLockPolicy)
{
    private enum VaultSelectionMode { ExistingVault, NewVault }

    private const string CatalogFileName = GalleryStoreFactory.CatalogFileName;
    private const string VaultHeaderFileName = VaultStore.HeaderFileName;
    private const string VaultStagingDirectoryPrefix = ".vault-stage-";

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

    public Task OpenExistingVaultAsync(string directory) => SelectVaultAsync(directory, VaultSelectionMode.ExistingVault);
    public Task SelectNewVaultAsync(string directory) => SelectVaultAsync(directory, VaultSelectionMode.NewVault);

    private async Task SelectVaultAsync(string directory, VaultSelectionMode selectionMode)
    {
        var root = CatalogDirectoryRules.NormalizeVaultDirectory(directory);
        if (selectionMode == VaultSelectionMode.NewVault) CatalogDirectoryRules.RequireLocalCatalogDirectory(root);
        await galleryLifetime.Gate.WaitAsync();
        try
        {
            EnsureActive();
            if (StorageFileSystem.PathEquals(root, VaultRoot) && galleryLifetime.Current.Checkout is not null)
            {
                if (selectionMode == VaultSelectionMode.NewVault)
                {
                    VaultCreationPublication.RecoverInterruptedCreation(root);
                    RequireEmptyDestination(root);
                }
                await StopDriveAndDrainSessionAsync();
                if (selectionMode == VaultSelectionMode.ExistingVault) history?.Save(root);
                return;
            }
            var store = new VaultStore(root);
            if (selectionMode == VaultSelectionMode.ExistingVault
                && (!store.Exists || !File.Exists(Path.Combine(root, CatalogFileName))))
                throw new InvalidOperationException("Select a gallery folder containing vault.json and gallery.db.");
            VaultCheckout? nextCheckout = galleryLifetime.AcquireLocationCheckout(root);
            try
            {
                if (selectionMode == VaultSelectionMode.NewVault)
                {
                    VaultCreationPublication.RecoverInterruptedCreation(root);
                    RequireEmptyDestination(root);
                }
                await StopDriveAndDrainSessionAsync();
                EnsureActive();
                galleryLifetime.PublishLocation(galleryLifetime.StoreFactory.Create(root), store, nextCheckout)?.Dispose();
                nextCheckout = null; // The published location now owns the checkout, including on history-write failure.
                if (selectionMode == VaultSelectionMode.ExistingVault) history?.Save(root);
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

    private async Task<(IGalleryStore StagedStore, string StagingDirectory, Action CleanSource)> StageVaultMoveAsync(
        StagedLocation location, byte[] key, Action<string> trackStagingDirectory)
    {
        var sourceRoot = VaultRoot;
        await QueuedFileDeletion.ReconcileOutstandingDeletesAsync(galleryStore, key);
        var records = await galleryStore.ReadRecordsAsync(key);
        var staging = CreateStagingDirectory(location.Destination, VaultStagingDirectoryPrefix, trackStagingDirectory);
        await CopyCatalogToStageAsync(vaultStore, galleryStore, staging, key);
        var stagedStore = galleryLifetime.StoreFactory.Create(staging);
        await CopyVaultMediaToStageAsync(records, staging, location.Destination, stagedStore, key);
        await ClearLocationSettingsAsync(stagedStore, key);
        return (stagedStore, staging, () => DeleteVaultData(sourceRoot, records));
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
            var records = await ReadValidatedVaultRecordsAsync(key);
            DeleteVaultData(VaultRoot, records);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            galleryLifetime.Gate.Release();
        }
    }

    private async Task<List<ImageRecord>> ReadValidatedVaultRecordsAsync(byte[] key)
    {
        await QueuedFileDeletion.ReconcileOutstandingDeletesAsync(galleryStore, key);
        var records = await galleryStore.ReadRecordsAsync(key);
        foreach (var record in records) ValidateMediaPath(record);
        return records;
    }

    /// <summary>Relocates only the encrypted catalog and header; all encrypted media stays at its existing paths.</summary>
    public Task RelocateCatalogAsync(string directory) => RelocateUnlockedVaultAsync(directory, StageCatalogRelocationAsync,
        destination => $"The local catalog is intact at '{destination}', but old catalog cleanup or history persistence failed. Media files were not moved.");

    private async Task<(IGalleryStore StagedStore, string StagingDirectory, Action CleanSource)> StageCatalogRelocationAsync(
        StagedLocation location, byte[] key, Action<string> trackStagingDirectory)
    {
        var mediaDirectory = await GallerySettings.ReadMediaDirectoryAsync(galleryLifetime.Current, key);
        var sourceRoot = VaultRoot;
        var staging = CreateStagingDirectory(location.Destination, ".catalog-stage-", trackStagingDirectory);
        await CopyCatalogToStageAsync(vaultStore, galleryStore, staging, key);
        var stagedStore = galleryLifetime.StoreFactory.Create(staging);
        await stagedStore.SaveSettingsAsync(new MediaStorageOptions { MediaDirectory = mediaDirectory },
            MediaStorageOptions.SettingId, MediaStorageOptions.SettingPurpose, key);
        await stagedStore.DeleteSettingsAsync(key, StorageOverview.SettingId);
        var copiedRecords = await stagedStore.ReadRecordsAsync(key);
        var sourceRecords = await galleryStore.ReadRecordsAsync(key);
        if (!CatalogRecordsMatch(copiedRecords, sourceRecords))
            throw new IOException("The copied catalog did not match the original; relocation was stopped.");
        return (stagedStore, staging, () => DeleteCatalogFiles(sourceRoot));
    }

    private async Task RelocateUnlockedVaultAsync(string directory,
        Func<StagedLocation, byte[], Action<string>,
            Task<(IGalleryStore StagedStore, string StagingDirectory, Action CleanSource)>> prepare,
        Func<string, string> publishedFailureMessage)
    {
        var destination = CatalogDirectoryRules.NormalizeVaultDirectory(directory);
        CatalogDirectoryRules.RequireLocalCatalogDirectory(destination);
        await galleryLifetime.Gate.WaitAsync();
        byte[]? key = null;
        StagedLocation? location = null;
        string? stagingDirectory = null;
        var preserveStagingDirectory = false;
        var lockRequestsAtStart = 0L;
        var locationWasActivated = false;
        try
        {
            galleryLifetime.EnsureCheckout();
            location = AcquireStagedLocation(destination);
            lockRequestsAtStart = sessionLockPolicy.ExplicitLockRequests;
            key = vaultSession.Key;
            await StopDriveAndDrainSessionAsync();

            IGalleryStore stagedStore;
            string staging;
            Action cleanSource;
            (stagedStore, staging, cleanSource) =
                await prepare(location.Value, key, path => stagingDirectory = path);
            EnsureActive();
            await PublishStagedFilesAsync(staging, destination, stagedStore, key);
            location = location.Value with { FilesPublished = true };
            var (activatedLocation, sourceCheckout) = ActivatePublishedLocation(location.Value);
            location = activatedLocation;
            using var retiredSourceCheckout = sourceCheckout;
            locationWasActivated = true;
            history?.Save(destination);
            cleanSource();
        }
        catch (Exception exception)
        {
            preserveStagingDirectory = exception is StagedFilePublicationException;
            if (locationWasActivated)
                throw new IOException(publishedFailureMessage(destination), exception);
            if (location?.FilesPublished == true)
                throw new IOException($"The verified gallery files were published to '{destination}', but the destination could not be activated. The source was not removed.", exception);
            sessionLockPolicy.RestoreUnlockedSessionAfterFailure(key, lockRequestsAtStart);
            throw;
        }
        finally { FinishStagedLocation(location, stagingDirectory, key, preserveStagingDirectory); }
    }

    internal static bool CatalogRecordsMatch(List<ImageRecord> copied, List<ImageRecord> source)
    {
        if (copied.Count != source.Count) return false;
        var orderedCopied = copied.OrderBy(record => record.Id).ToArray();
        var orderedSource = source.OrderBy(record => record.Id).ToArray();
        for (var index = 0; index < orderedCopied.Length; index++)
        {
            var copiedRecord = orderedCopied[index];
            var sourceRecord = orderedSource[index];
            if (copiedRecord.Id != sourceRecord.Id
                || (copiedRecord with
                {
                    Collections = [],
                    LegacyCollections = [],
                    VisionFacts = null
                }) != (sourceRecord with { Collections = [], LegacyCollections = [], VisionFacts = null })
                || !ArraysMatch(copiedRecord.Collections, sourceRecord.Collections)
                || !ArraysMatch(copiedRecord.LegacyCollections, sourceRecord.LegacyCollections)
                || !VisionFactsMatch(copiedRecord.VisionFacts, sourceRecord.VisionFacts))
                return false;
        }
        return true;
    }

    private static bool VisionFactsMatch(VisionFacts? copied, VisionFacts? source)
    {
        if (copied is null || source is null) return copied is null && source is null;
        if (copied.RunId != source.RunId || copied.SourceContentHash != source.SourceContentHash
            || !ArraysMatch(copied.Relationships, source.Relationships)
            || copied.Entities.IsDefault != source.Entities.IsDefault)
            return false;
        if (copied.Entities.IsDefault) return true;
        if (copied.Entities.Length != source.Entities.Length) return false;

        for (var index = 0; index < copied.Entities.Length; index++)
        {
            var copiedEntity = copied.Entities[index];
            var sourceEntity = source.Entities[index];
            if (copiedEntity.Id != sourceEntity.Id || copiedEntity.Kind != sourceEntity.Kind
                || copiedEntity.Description != sourceEntity.Description
                || !ArraysMatch(copiedEntity.Attributes, sourceEntity.Attributes))
                return false;
        }
        return true;
    }

    private static bool ArraysMatch<T>(System.Collections.Immutable.ImmutableArray<T> copied,
        System.Collections.Immutable.ImmutableArray<T> source) where T : IEquatable<T> =>
        copied.IsDefault == source.IsDefault
        && (copied.IsDefault || copied.AsSpan().SequenceEqual(source.AsSpan()));

    internal async Task RestoreVaultFromTargetAsync(
        IBackupTarget target, string latestBackupName, string destinationDirectory,
        string credential, VaultCredentialKind vaultCredentialKind, string? name, string? sourceDirectory)
    {
        target.ValidateArchiveName(latestBackupName);
        if (vaultCredentialKind == VaultCredentialKind.RecoveryPhrase)
            credential = RecoveryPhraseValidator.Normalize(credential);
        var destination = CatalogDirectoryRules.NormalizeVaultDirectory(destinationDirectory);
        CatalogDirectoryRules.RequireLocalCatalogDirectory(destination);
        await galleryLifetime.Gate.WaitAsync();
        StagedLocation? stagedLocation = null;
        string? stagingDirectory = null;
        var preserveStagingDirectory = false;
        byte[]? key = null;
        try
        {
            EnsureActive();
            stagedLocation = AcquireStagedLocation(destination, sourceDirectory);
            var staging = CreateStagingDirectory(destination, VaultStagingDirectoryPrefix,
                path => stagingDirectory = path);
            IGalleryStore store;
            (store, key) = await BackupRestoreStaging.StageRestoreAsync(
                galleryLifetime.StoreFactory, target, latestBackupName, staging, destination, credential, vaultCredentialKind);
            await ClearLocationSettingsAsync(store, key);
            if (name is not null) await new VaultStore(staging).RenameVaultAsync(name);
            await StopDriveAndDrainSessionAsync();
            EnsureActive();
            await PublishStagedFilesAsync(staging, destination, store, key);
            stagedLocation = stagedLocation.Value with { FilesPublished = true };
            var (activatedLocation, sourceCheckout) = ActivatePublishedLocation(stagedLocation.Value);
            stagedLocation = activatedLocation;
            sourceCheckout?.Dispose();
            history?.Save(destination);
            // Restore returns to regular sign-in; credentials and plaintext never survive the operation.
        }
        catch (StagedFilePublicationException)
        {
            preserveStagingDirectory = true;
            throw;
        }
        finally { FinishStagedLocation(stagedLocation, stagingDirectory, key, preserveStagingDirectory); }
    }

    private static async Task CopyCatalogToStageAsync(
        VaultStore vaultStore, IGalleryCatalogSnapshots snapshots, string staging, byte[] key)
    {
        await vaultStore.CopyHeaderAsync(Path.Combine(staging, VaultHeaderFileName));
        await snapshots.CreateSnapshotAsync(Path.Combine(staging, CatalogFileName), key);
    }

    private static async Task CopyVaultMediaToStageAsync(IReadOnlyList<ImageRecord> records, string staging,
        string destination, IGalleryStore stagedStore, byte[] key)
    {
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
            return new StagedLocation(destination, checkout, FilesPublished: false);
        }
        catch
        {
            checkout.Dispose();
            throw;
        }
    }

    private void FinishStagedLocation(StagedLocation? stagedLocation, string? stagingDirectory, byte[]? key,
        bool preserveStagingDirectory)
    {
        try
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            try { stagedLocation?.Checkout?.Dispose(); }
            finally
            {
                if (!preserveStagingDirectory) DeleteStagingDirectory(stagingDirectory);
            }
        }
        finally { galleryLifetime.Gate.Release(); }
    }

    internal static void DeleteStagingDirectory(string? stagingDirectory)
    {
        if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, recursive: true);
    }

    private static string CreateStagingDirectory(string destination, string prefix, Action<string> trackStagingDirectory)
    {
        var stagingDirectory = Path.Combine(destination, prefix + Guid.NewGuid().ToString("N"));
        trackStagingDirectory(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);
        return stagingDirectory;
    }

    private static async Task PublishStagedFilesAsync(string staging, string destination, IGalleryStore store, byte[] key)
    {
        await store.PrepareForPublicationAsync(key);
        PublishStagedFiles(staging, destination);
    }

    internal static void PublishStagedFiles(string staging, string destination,
        Action<string, string>? moveFileOrDirectory = null)
    {
        moveFileOrDirectory ??= MoveFileOrDirectory;
        var moved = new List<string>();
        try
        {
            foreach (var name in new[] { "images", CatalogFileName, VaultHeaderFileName })
            {
                var source = Path.Combine(staging, name);
                var target = Path.Combine(destination, name);
                if (!Directory.Exists(source) && !File.Exists(source)) continue;
                moveFileOrDirectory(source, target);
                moved.Add(name);
            }
        }
        catch (Exception publicationFailure)
        {
            var rollbackFailures = RollbackPublishedFiles(staging, destination, moved, moveFileOrDirectory);
            if (rollbackFailures.Count > 0)
                throw new StagedFilePublicationException(staging, publicationFailure, rollbackFailures);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(publicationFailure).Throw();
            throw;
        }
    }

    private static void MoveFileOrDirectory(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static List<Exception> RollbackPublishedFiles(string staging, string destination, IEnumerable<string> moved,
        Action<string, string> moveFileOrDirectory)
    {
        var failures = new List<Exception>();
        foreach (var name in moved.Reverse())
        {
            var source = Path.Combine(destination, name);
            var target = Path.Combine(staging, name);
            try { moveFileOrDirectory(source, target); }
            catch (Exception exception) { failures.Add(exception); }
        }
        return failures;
    }

    private sealed class StagedFilePublicationException(
        string stagingDirectory, Exception publicationFailure, IReadOnlyList<Exception> rollbackFailures)
        : IOException($"Publishing staged files partially failed and rollback was incomplete. Staged data was retained at '{stagingDirectory}'.",
            new AggregateException("Publication and rollback failures.", [publicationFailure, .. rollbackFailures]));

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

    private (StagedLocation Location, VaultCheckout? RetiredCheckout) ActivatePublishedLocation(StagedLocation location)
    {
        var destination = location.Destination;
        var retired = galleryLifetime.PublishLocation(
            galleryLifetime.StoreFactory.Create(destination), new VaultStore(destination), location.Checkout);
        return (location with { Checkout = null }, retired);
    }

    private readonly record struct StagedLocation(string Destination, VaultCheckout? Checkout, bool FilesPublished);
}
