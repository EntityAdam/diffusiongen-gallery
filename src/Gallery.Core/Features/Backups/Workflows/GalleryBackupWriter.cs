using Gallery.Core.Features.Media.Processors;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using Gallery.Core.Features.Backups.Targets;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Shared.Files;

namespace Gallery.Core.Features.Backups;

internal sealed class GalleryBackupWriter(GalleryLifetime galleryLifetime, VaultSession vaultSession, HttpClient? httpClient) : IGalleryBackup
{
    private const string LocalDestinationType = BackupSettingsProcessor.LocalDestinationType;
    private const string AzureDestinationType = BackupSettingsProcessor.AzureDestinationType;
    private const string FullBackupType = "full";
    private const string IncrementalBackupType = "incremental";

    public async Task<BackupOptions> LoadBackupOptionsAsync()
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        var settings = await store.ReadSettingsAsync<StoredBackupSettings>(
            StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, keyLease.Bytes);
        return BackupSettingsProcessor.ToOptions(settings);
    }

    public async Task<BackupOptions> SaveBackupOptionsAsync(
        string destinationType, string localDirectory, string azureContainer,
        string azurePrefix, string azureConnectionString)
    {
        var normalized = BackupSettingsProcessor.Normalize(destinationType, localDirectory, azureContainer, azurePrefix);
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        await store.BackupGate.WaitAsync();
        try
        {
            var current = await store.ReadSettingsAsync<StoredBackupSettings>(
                StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, keyLease.Bytes);
            var candidate = BackupSettingsProcessor.Prepare(
                normalized.Type, normalized.LocalDirectory, normalized.AzureContainer, normalized.AzurePrefix, azureConnectionString, current);
            var updated = current is not null && BackupSettingsProcessor.SameDestination(current, candidate)
                ? candidate with { LastBackupName = current.LastBackupName, LastBackupType = current.LastBackupType }
                : candidate;
            await store.SaveSettingsAsync(updated, StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, keyLease.Bytes);
            return BackupSettingsProcessor.ToOptions(updated);
        }
        finally
        {
            store.BackupGate.Release();
        }
    }

    public Task<BackupRunResult> CreateFullBackupAsync() => CreateBackupAsync(BackupMode.Full);

    public Task<BackupRunResult> CreateIncrementalBackupAsync() => CreateBackupAsync(BackupMode.Incremental);

    private async Task<BackupRunResult> CreateBackupAsync(BackupMode mode)
    {
        var isIncremental = mode == BackupMode.Incremental;
        using var activity = BackupArchive.ActivitySource.StartActivity(isIncremental ? "backup.incremental" : "backup.full");
        activity?.SetTag("backup.type",
            isIncremental ? IncrementalBackupType : FullBackupType);
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var location = galleryLifetime.Current;
        var store = location.GalleryStore;
        await store.BackupGate.WaitAsync();
        string? stagingDirectory = null;
        try
        {
            var settings = await store.ReadSettingsAsync<StoredBackupSettings>(
                StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, keyLease.Bytes)
                ?? throw new InvalidOperationException("Configure a backup destination before starting a backup.");
            var headerStore = location.VaultStore;
            var target = CreateBackupTarget(settings, headerStore.Root);
            var previousName = isIncremental
                ? settings.LastBackupName ?? throw new InvalidOperationException("Create a full backup before creating an incremental backup.")
                : null;
            var (createdAt, backupId, backupType, archiveName) = PrepareArchiveName(mode);
            stagingDirectory = Path.Combine(Path.GetTempPath(), "diffusiongen-backup-" + backupId);
            var (archivePath, manifestPath) = PrepareStagingPaths(stagingDirectory, archiveName);
            BackupArchive.Manifest? previousManifest = null;
            var previousChainLength = 0;
            if (previousName is not null)
            {
                var previousChain = await BackupChainValidator.ValidateChainAsync(
                    galleryLifetime.StoreFactory, target, previousName, stagingDirectory, keyLease.Bytes);
                previousManifest = previousChain.Manifest;
                previousChainLength = previousChain.Length;
            }

            var snapshot = await CaptureSnapshotAsync(store, headerStore, stagingDirectory,
                keyLease.Bytes, mode, previousManifest, activity);
            var mediaEntriesByRecordId = snapshot.MediaEntries;
            var databaseFingerprint = snapshot.DatabaseFingerprint;
            var vaultHeaderSha256 = snapshot.VaultHeaderSha256;

            if (isIncremental && previousManifest is not null
                && BackupArchive.IsUnchangedSince(previousManifest, mediaEntriesByRecordId, databaseFingerprint, vaultHeaderSha256))
            {
                activity?.SetTag("backup.skipped", true);
                return new(backupType, previousName!, 0, mediaEntriesByRecordId.Count, 0, createdAt, WasSkipped: true);
            }
            if (isIncremental && !BackupChain.CanAppendIncremental(previousChainLength))
                throw new InvalidOperationException("The backup chain is at its maximum length. Create a new full backup.");

            var manifest = new BackupArchive.Manifest(1, backupId, backupType, previousName, createdAt,
                mediaEntriesByRecordId.ToImmutableDictionary(StringComparer.Ordinal), databaseFingerprint, vaultHeaderSha256);
            return await PublishArchiveAsync(store, target, settings, archiveName, archivePath, manifestPath,
                manifest, snapshot, keyLease.Bytes, activity);
        }
        finally
        {
            try
            {
                if (stagingDirectory is not null) Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError("Backup staging cleanup failed for '{0}': {1}", stagingDirectory, exception);
            }
            finally { store.BackupGate.Release(); }
        }
    }

    private static (DateTimeOffset CreatedAt, string BackupId, string BackupType, string ArchiveName) PrepareArchiveName(BackupMode mode)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var backupId = Guid.NewGuid().ToString("N");
        var backupType = mode == BackupMode.Incremental ? IncrementalBackupType : FullBackupType;
        return (createdAt, backupId, backupType, $"{backupType}-{createdAt:yyyyMMdd'T'HHmmssfff'Z'}-{backupId}.zip");
    }

    private static (string ArchivePath, string ManifestPath) PrepareStagingPaths(string stagingDirectory, string archiveName)
    {
        Directory.CreateDirectory(stagingDirectory);
        return (Path.Combine(stagingDirectory, archiveName), Path.Combine(stagingDirectory, "manifest.enc"));
    }

    private static async Task<BackupRunResult> PublishArchiveAsync(
        IGalleryStore store, IBackupTarget target, StoredBackupSettings settings, string archiveName,
        string archivePath, string manifestPath, BackupArchive.Manifest manifest, CapturedSnapshot snapshot,
        byte[] key, Activity? activity)
    {
        await BackupArchive.WriteAsync(archivePath, manifest, snapshot.StagedMediaPaths, key);
        await File.WriteAllBytesAsync(manifestPath, BackupArchive.ReadArchiveManifest(archivePath));
        var archiveBytes = new FileInfo(archivePath).Length;
        activity?.SetTag("backup.archive.bytes_written", archiveBytes);
        activity?.SetTag("backup.media.files_copied", snapshot.StagedMediaPaths.Count);
        await target.UploadAsync(archiveName, archivePath);
        await target.UploadAsync(BackupArchive.ManifestName(archiveName), manifestPath);

        var latestSettings = await store.ReadSettingsAsync<StoredBackupSettings>(StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, key);
        if (latestSettings is null || !BackupSettingsProcessor.SameDestination(settings, latestSettings))
            throw new InvalidOperationException("The backup completed, but the destination settings changed before its status could be saved. Verify the archive and run a new full backup if needed.");
        await store.SaveSettingsAsync(settings with
        {
            LastBackupName = archiveName,
            LastBackupType = manifest.BackupType
        }, StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, key);
        return new(manifest.BackupType, archiveName, snapshot.StagedMediaPaths.Count, snapshot.MediaEntries.Count, archiveBytes, manifest.CreatedAt);
    }

    private sealed record CapturedSnapshot(
        Dictionary<string, BackupArchive.MediaEntry> MediaEntries,
        Dictionary<string, string> StagedMediaPaths,
        string DatabaseFingerprint, string VaultHeaderSha256);

    private static async Task<CapturedSnapshot> CaptureSnapshotAsync(
        IGalleryStore store, VaultStore headerStore, string stagingDirectory, byte[] key,
        BackupMode mode, BackupArchive.Manifest? previousManifest, Activity? activity)
    {
        var mediaEntriesByRecordId = new Dictionary<string, BackupArchive.MediaEntry>(StringComparer.Ordinal);
        var stagedMediaPathsBySha256 = new Dictionary<string, string>(StringComparer.Ordinal);
        await store.Gate.WaitAsync();
        try
        {
            var records = await store.ReadRecordsAsync(key);
            activity?.SetTag("backup.library.image_count", records.Count);
            var vaultHeaderPath = Path.Combine(stagingDirectory, VaultStore.HeaderFileName);
            var databasePath = Path.Combine(stagingDirectory, GalleryStoreFactory.CatalogFileName);
            await headerStore.CopyHeaderAsync(vaultHeaderPath);
            await store.CreateSnapshotAsync(databasePath, key);
            var vaultHeaderSha256 = await FileSha256.ComputeAsync(vaultHeaderPath);
            var databaseFingerprint = await store.ComputeSnapshotFingerprintAsync(databasePath, StoredBackupSettings.SettingId, key);

            long mediaHashedBytes = 0;
            var mediaHashTimer = Stopwatch.StartNew();
            foreach (var record in records)
            {
                var sourcePath = Path.GetFullPath(record.StoredPath);
                if (!File.Exists(sourcePath))
                    throw new FileNotFoundException($"Cannot back up '{record.Name}': its encrypted media file is missing.", sourcePath);
                var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                var expectedExtension = record.IsVideo ? ".dmp4" : ".dpng";
                if (extension != expectedExtension)
                    throw new InvalidOperationException($"Cannot back up '{record.Name}': expected an encrypted {expectedExtension} file.");

                string sha256;
                await using (var source = File.OpenRead(sourcePath))
                {
                    mediaHashedBytes += source.Length;
                    sha256 = Convert.ToHexString(await SHA256.HashDataAsync(source)).ToLowerInvariant();
                }
                var mediaEntry = new BackupArchive.MediaEntry(sourcePath, sha256, extension);
                mediaEntriesByRecordId.Add(record.Id, mediaEntry);

                var unchanged = previousManifest is not null
                    && previousManifest.Media.TryGetValue(record.Id, out var previousEntry)
                    && BackupArchive.AreMediaEntriesEquivalent(previousEntry, mediaEntry);
                if (mode == BackupMode.Incremental && unchanged) continue;

                if (!stagedMediaPathsBySha256.TryGetValue(sha256, out var stagedPath))
                {
                    stagedPath = Path.Combine(stagingDirectory, "media-" + sha256 + ".blob");
                    File.Copy(sourcePath, stagedPath, overwrite: false);
                    await using var stagedFile = File.OpenRead(stagedPath);
                    var stagedHash = Convert.ToHexString(await SHA256.HashDataAsync(stagedFile)).ToLowerInvariant();
                    if (stagedHash != sha256)
                        throw new IOException($"Encrypted media changed while backing up '{record.Name}'; retry the backup.");
                    stagedMediaPathsBySha256.Add(sha256, stagedPath);
                }
                byte[] plaintext;
                try
                {
                    plaintext = await StoredMediaProcessor.ReadAsync(stagedPath, record.Id, key);
                }
                catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
                {
                    throw new InvalidOperationException(
                        $"Cannot back up '{record.Name}': its encrypted media failed authentication.", exception);
                }
                CryptographicOperations.ZeroMemory(plaintext);
            }
            mediaHashTimer.Stop();
            activity?.SetTag("backup.library.media_hashed_bytes", mediaHashedBytes);
            activity?.SetTag("backup.library.media_hash_elapsed_ms", mediaHashTimer.Elapsed.TotalMilliseconds);
            return new(mediaEntriesByRecordId, stagedMediaPathsBySha256, databaseFingerprint, vaultHeaderSha256);
        }
        finally { store.Gate.Release(); }
    }

    private enum BackupMode
    {
        Full,
        Incremental
    }

    private IBackupTarget CreateBackupTarget(StoredBackupSettings settings, string vaultRoot)
    {
        if (settings.DestinationType == LocalDestinationType)
        {
            var directory = Path.GetFullPath(settings.LocalDirectory);
            var resolvedDirectory = ResolveDirectoryAliases(directory);
            var resolvedVaultRoot = ResolveDirectoryAliases(vaultRoot);
            if (StorageFileSystem.IsPathWithin(resolvedDirectory, resolvedVaultRoot))
                throw new InvalidOperationException("The backup folder cannot be inside the gallery data folder.");
            return new BackupTargetLocal(directory);
        }
        if (settings.DestinationType == AzureDestinationType)
        {
            return new BackupTargetAzure(settings.AzureConnectionString, settings.AzureContainer, settings.AzurePrefix, httpClient);
        }
        throw new InvalidOperationException("The saved backup destination is not supported.");
    }

    private static string ResolveDirectoryAliases(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The backup folder path has no filesystem root.");
        var resolvedPath = root;
        var segments = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var existingPath = true;
        foreach (var segment in segments)
        {
            var candidate = Path.Combine(resolvedPath, segment);
            if (!existingPath)
            {
                resolvedPath = candidate;
                continue;
            }

            var directory = new DirectoryInfo(candidate);
            if (directory.LinkTarget is not null)
            {
                var target = directory.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new IOException($"Could not resolve backup directory alias '{candidate}'.");
                resolvedPath = Path.GetFullPath(target.FullName);
            }
            else
            {
                resolvedPath = candidate;
                existingPath = directory.Exists;
            }
        }
        return Path.GetFullPath(resolvedPath);
    }
}
