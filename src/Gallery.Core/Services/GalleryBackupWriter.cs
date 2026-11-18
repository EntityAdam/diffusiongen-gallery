using Gallery.Core.Services.DataManagers;
using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Diagnostics;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Processors.Hashing;
using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Sessions;

namespace Gallery.Core.Services;

internal sealed class GalleryBackupWriter(GalleryLifetime galleryLifetime, VaultSession vaultSession, HttpClient? httpClient) : IGalleryBackup
{
    public async Task<BackupOptions> LoadBackupOptionsAsync()
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        var settings = await store.ReadSettingsAsync<StoredBackupSettings>(
            StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, keyLease.Bytes);
        return settings is null
            ? new("local", "", "", "", false, null, null)
            : new(settings.DestinationType, settings.LocalDirectory, settings.AzureContainer,
                settings.AzurePrefix, settings.AzureConnectionString.Length > 0,
                settings.LastBackupName, settings.LastBackupType);
    }

    public async Task<BackupOptions> SaveBackupOptionsAsync(
        string destinationType, string localDirectory, string azureContainer,
        string azurePrefix, string azureConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationType);
        var type = destinationType.Trim().ToLowerInvariant();
        if (type is not ("local" or "azure"))
            throw new InvalidOperationException("Choose either a local/NAS folder or Azure Blob Storage.");

        localDirectory = localDirectory.Trim();
        azureContainer = azureContainer.Trim();
        azurePrefix = BackupTargetAzure.NormalizePrefix(azurePrefix);
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        var current = await store.ReadSettingsAsync<StoredBackupSettings>(
            StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, keyLease.Bytes);
        var connectionString = azureConnectionString.Trim();
        if (type == "local")
        {
            if (localDirectory.Length == 0)
                throw new InvalidOperationException("Enter a local folder or SMB network share path.");
            localDirectory = Path.GetFullPath(localDirectory);
        }
        else
        {
            if (azureContainer.Length == 0)
                throw new InvalidOperationException("Enter the Azure Blob container name.");
            if (connectionString.Length == 0 && current?.DestinationType == "azure")
                connectionString = current.AzureConnectionString;
            if (connectionString.Length == 0)
                throw new InvalidOperationException("Enter the Azure Storage connection string.");
            if (azureContainer.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
                throw new InvalidOperationException("Azure container names may contain only lowercase letters, digits, and hyphens.");
            if (azureContainer != azureContainer.ToLowerInvariant() || azureContainer.Length is < 3 or > 63)
                throw new InvalidOperationException("Azure container names must be 3 to 63 lowercase letters, digits, or hyphens.");
            BackupTargetAzure.ValidateConnectionString(connectionString);
        }

        var sameDestination = current is not null
            && current.DestinationType == type
            && current.LocalDirectory == localDirectory
            && current.AzureContainer == azureContainer
            && current.AzurePrefix == azurePrefix
            && current.AzureConnectionString == connectionString;
        var updated = new StoredBackupSettings(type, localDirectory, azureContainer, azurePrefix,
            connectionString, sameDestination ? current!.LastBackupName : null,
            sameDestination ? current!.LastBackupType : null);
        await store.SaveSettingsAsync(updated, StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, keyLease.Bytes);
        return new(updated.DestinationType, updated.LocalDirectory, updated.AzureContainer,
            updated.AzurePrefix, updated.AzureConnectionString.Length > 0,
            updated.LastBackupName, updated.LastBackupType);
    }

    public Task<BackupRunResult> CreateFullBackupAsync() => CreateBackupAsync(incremental: false);

    public Task<BackupRunResult> CreateIncrementalBackupAsync() => CreateBackupAsync(incremental: true);

    private async Task<BackupRunResult> CreateBackupAsync(bool incremental)
    {
        using var activity = BackupArchive.ActivitySource.StartActivity(incremental ? "backup.incremental" : "backup.full");
        activity?.SetTag("backup.type", incremental ? "incremental" : "full");
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var location = galleryLifetime.Current;
        var store = location.GalleryStore;
        var headerStore = location.VaultStore;
        var settings = await store.ReadSettingsAsync<StoredBackupSettings>(StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, keyLease.Bytes)
            ?? throw new InvalidOperationException("Configure a backup destination before starting a backup.");
        var target = CreateBackupTarget(settings, headerStore.Root);
        var previousName = incremental
            ? settings.LastBackupName ?? throw new InvalidOperationException("Create a full backup before creating an incremental backup.")
            : null;
        var createdAt = DateTimeOffset.UtcNow;
        var backupId = Guid.NewGuid().ToString("N");
        var backupType = incremental ? "incremental" : "full";
        var archiveName = $"{backupType}-{createdAt:yyyyMMdd'T'HHmmssfff'Z'}-{backupId}.zip";
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "diffusiongen-backup-" + backupId);
        Directory.CreateDirectory(stagingDirectory);
        var archivePath = Path.Combine(stagingDirectory, archiveName);
        var manifestPath = Path.Combine(stagingDirectory, "manifest.enc");
        await store.BackupGate.WaitAsync();
        try
        {
            BackupArchive.Manifest? previousManifest = null;
            if (previousName is not null)
            {
                previousManifest = await BackupArchive.ValidateChainAsync(galleryLifetime.StoreFactory, target, previousName, stagingDirectory, keyLease.Bytes);
            }

            var mediaEntries = new Dictionary<string, BackupArchive.MediaEntry>(StringComparer.Ordinal);
            var stagedMedia = new Dictionary<string, string>(StringComparer.Ordinal);
            string DatabaseFingerprint;
            string vaultHeaderSha256;
            await store.Gate.WaitAsync();
            try
            {
                var records = await store.ReadRecordsAsync(keyLease.Bytes);
                activity?.SetTag("backup.library.image_count", records.Count);
                var vaultHeaderPath = Path.Combine(stagingDirectory, VaultStore.HeaderFileName);
                var databasePath = Path.Combine(stagingDirectory, GalleryStoreFactory.CatalogFileName);
                await headerStore.CopyHeaderAsync(vaultHeaderPath);
                await store.CreateSnapshotAsync(databasePath, keyLease.Bytes);
                vaultHeaderSha256 = await FileSha256.ComputeAsync(vaultHeaderPath);
                DatabaseFingerprint = await store.ComputeSnapshotFingerprintAsync(databasePath, StoredBackupSettings.SettingId, keyLease.Bytes);

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
                    mediaEntries.Add(record.Id, new(sourcePath, sha256, extension));

                    var unchanged = previousManifest?.Media.TryGetValue(record.Id, out var previousEntry) == true
                        && previousEntry.Sha256 == sha256
                        && previousEntry.Extension == extension;
                    if (incremental && unchanged) continue;

                    if (!stagedMedia.ContainsKey(sha256))
                    {
                        var stagedPath = Path.Combine(stagingDirectory, "media-" + sha256 + ".blob");
                        File.Copy(sourcePath, stagedPath, overwrite: false);
                        await using var stagedFile = File.OpenRead(stagedPath);
                        var stagedHash = Convert.ToHexString(await SHA256.HashDataAsync(stagedFile)).ToLowerInvariant();
                        if (stagedHash != sha256)
                            throw new IOException($"Encrypted media changed while backing up '{record.Name}'; retry the backup.");
                        stagedMedia.Add(sha256, stagedPath);
                    }
                }
                mediaHashTimer.Stop();
                activity?.SetTag("backup.library.media_hashed_bytes", mediaHashedBytes);
                activity?.SetTag("backup.library.media_hash_elapsed_ms", mediaHashTimer.Elapsed.TotalMilliseconds);
            }
            finally { store.Gate.Release(); }

            if (incremental && previousManifest is not null
                && BackupArchive.IsUnchangedSince(previousManifest, mediaEntries, DatabaseFingerprint, vaultHeaderSha256))
            {
                activity?.SetTag("backup.skipped", true);
                return new(backupType, previousName!, 0, mediaEntries.Count, 0, createdAt, WasSkipped: true);
            }

            var manifest = new BackupArchive.Manifest(1, backupId, backupType, previousName, createdAt, mediaEntries.ToImmutableDictionary(StringComparer.Ordinal),
                DatabaseFingerprint, vaultHeaderSha256);
            await BackupArchive.WriteAsync(archivePath, manifest, stagedMedia, keyLease.Bytes);
            await File.WriteAllBytesAsync(manifestPath, BackupArchive.ReadArchiveManifest(archivePath));
            var archiveBytes = new FileInfo(archivePath).Length;
            activity?.SetTag("backup.archive.bytes_written", archiveBytes);
            activity?.SetTag("backup.media.files_copied", stagedMedia.Count);
            await target.UploadAsync(archiveName, archivePath);
            await target.UploadAsync(BackupArchive.ManifestName(archiveName), manifestPath);

            var latestSettings = await store.ReadSettingsAsync<StoredBackupSettings>(StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, keyLease.Bytes);
            if (latestSettings is null || !SameDestination(settings, latestSettings))
                throw new InvalidOperationException("The backup completed, but the destination settings changed before its status could be saved. Verify the archive and run a new full backup if needed.");
            await store.SaveSettingsAsync(settings with
            {
                LastBackupName = archiveName,
                LastBackupType = backupType
            }, StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, keyLease.Bytes);
            return new(backupType, archiveName, stagedMedia.Count, mediaEntries.Count, archiveBytes, createdAt);
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            finally { store.BackupGate.Release(); }
        }
    }

    private static bool SameDestination(StoredBackupSettings left, StoredBackupSettings right) =>
        left.DestinationType == right.DestinationType
        && left.LocalDirectory == right.LocalDirectory
        && left.AzureContainer == right.AzureContainer
        && left.AzurePrefix == right.AzurePrefix
        && left.AzureConnectionString == right.AzureConnectionString;

    private IBackupTarget CreateBackupTarget(StoredBackupSettings settings, string vaultRoot)
    {
        if (settings.DestinationType == "local")
        {
            var directory = Path.GetFullPath(settings.LocalDirectory);
            if (StorageFileSystem.IsPathWithin(directory, vaultRoot))
                throw new InvalidOperationException("The backup folder cannot be inside the gallery data folder.");
            return new BackupTargetLocal(directory);
        }
        if (settings.DestinationType == "azure")
        {
            return new BackupTargetAzure(settings.AzureConnectionString, settings.AzureContainer, settings.AzurePrefix, httpClient);
        }
        throw new InvalidOperationException("The saved backup destination is not supported.");
    }
}
