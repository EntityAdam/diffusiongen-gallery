using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Sessions.Locations;
using Gallery.Core.Services.Validators;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<IReadOnlyList<string>> ListRestoreBackupsAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString) =>
        CreateRestoreTarget(sourceType, localDirectory, azureContainer, azurePrefix, connectionString).ListAsync();

    public Task<IReadOnlyList<string>> ListRestoreBackupsFromSasAsync(string sasUrl, string azurePrefix = "") =>
        CreateSasRestoreTarget(sasUrl, azurePrefix).ListAsync();

    private IBackupTarget CreateSasRestoreTarget(string sasUrl, string azurePrefix) =>
        new BackupTargetAzure(sasUrl, NormalizeAzurePrefix(azurePrefix), ownedHttpClient);

    private IBackupTarget CreateRestoreTarget(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString)
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
            return new BackupTargetAzure(connectionString.Trim(), azureContainer.Trim(), NormalizeAzurePrefix(azurePrefix), ownedHttpClient);
        }
        throw new InvalidOperationException("Choose a local/SMB folder or Azure Blob Storage.");
    }

    public async Task RestoreVaultAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix,
        string connectionString, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null)
    {
        var target = CreateRestoreTarget(sourceType, localDirectory, azureContainer, azurePrefix, connectionString);
        await RestoreVaultFromTargetAsync(target, latestBackupName, destinationDirectory, credential, useRecovery,
            name, sourceType == "local" ? localDirectory : null);
    }

    public Task RestoreGalleryFromSasAsync(
        string sasUrl, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null, string azurePrefix = "") =>
        RestoreVaultFromTargetAsync(CreateSasRestoreTarget(sasUrl, azurePrefix), latestBackupName, destinationDirectory,
            credential, useRecovery, name, sourceDirectory: null);

    private async Task RestoreVaultFromTargetAsync(
        IBackupTarget target, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery, string? name, string? sourceDirectory)
    {
        target.ValidateArchiveName(latestBackupName);
        if (useRecovery) credential = RecoveryPhraseValidator.Normalize(credential);
        var destination = NormalizeVaultDirectory(destinationDirectory);
        RequireLocalCatalogDirectory(destination);
        await lifecycleGate.WaitAsync();
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
            var archivePaths = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = latestBackupName;
            BackupManifest? latestManifest = null;
            while (true)
            {
                target.ValidateArchiveName(current);
                if (!visited.Add(current) || visited.Count > 10000)
                    throw new InvalidOperationException("The backup chain contains a cycle or is too long.");
                var archivePath = Path.Combine(staging, $"backup-{archivePaths.Count}.zip");
                await target.DownloadAsync(current, archivePath);
                archivePaths.Add(archivePath);
                var archiveLength = new FileInfo(archivePath).Length;
                using var archive = ZipFile.OpenRead(archivePath);
                RequireEntriesWithinArchive(archive, archiveLength);
                if (key is null)
                {
                    await ExtractRequiredEntryAsync(archive, VaultHeaderFileName, Path.Combine(staging, VaultHeaderFileName),
                        MaximumVaultHeaderBytes);
                    var stagedVaultStore = new VaultStore(staging);
                    key = useRecovery
                        ? await stagedVaultStore.UnlockWithRecoveryPhraseAsync(credential)
                        : await stagedVaultStore.UnlockWithPassphraseAsync(credential);
                }
                var manifest = ArchiveManifestProcessor.Read<BackupManifest>(ReadArchiveManifest(archivePath), key);
                if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.BackupId) || manifest.Media is null)
                    throw new InvalidOperationException("Unsupported or corrupt backup manifest.");
                if (latestManifest is null)
                {
                    latestManifest = manifest;
                    await ExtractRequiredEntryAsync(archive, CatalogFileName, Path.Combine(staging, CatalogFileName), archiveLength);
                    if (manifest.VaultHeaderSha256 != await HashFileAsync(Path.Combine(staging, VaultHeaderFileName))
                        || manifest.DatabaseFingerprint != await GalleryStore.ComputeContentFingerprintAsync(
                            Path.Combine(staging, CatalogFileName), BackupSettingsId))
                        throw new CryptographicException("Backup header or database integrity check failed.");
                }
                if (manifest.BackupType == "full")
                {
                    if (manifest.ParentBackupName is not null)
                        throw new InvalidOperationException("The full backup cannot have a parent.");
                    break;
                }
                if (manifest.BackupType != "incremental" || string.IsNullOrWhiteSpace(manifest.ParentBackupName))
                    throw new InvalidOperationException("The backup chain must lead to a full backup.");
                current = manifest.ParentBackupName;
            }

            var store = new GalleryStore(Path.Combine(staging, CatalogFileName));
            var records = await store.ReadRecordsAsync(key);
            if (records.Count != latestManifest!.Media.Count)
                throw new CryptographicException("The backup catalog and manifest disagree.");
            var archiveIndexByBlobName = IndexArchiveMediaEntries(archivePaths);
            var restoresByArchiveIndex = new SortedDictionary<int, List<(ImageRecord Record, BackupMediaEntry Entry, string Relative)>>();
            foreach (var record in records)
            {
                var relative = MediaRelativePath(record);
                if (!latestManifest.Media.TryGetValue(record.Id, out var entry)
                    || entry.Extension != Path.GetExtension(relative)
                    || entry.StoredPath != record.StoredPath
                    || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
                    throw new CryptographicException("Invalid media entry in the backup manifest.");
                if (!archiveIndexByBlobName.TryGetValue(MediaBlobEntryName(entry.Sha256), out var archiveIndex))
                    throw new InvalidOperationException($"The full/incremental chain is missing media for '{record.Name}'.");
                if (!restoresByArchiveIndex.TryGetValue(archiveIndex, out var restores))
                    restoresByArchiveIndex.Add(archiveIndex, restores = []);
                restores.Add((record, entry, relative));
            }
            // Each archive is opened once and only while its own blobs are extracted.
            foreach (var (archiveIndex, restores) in restoresByArchiveIndex)
            {
                var archivePath = archivePaths[archiveIndex];
                var archiveLength = new FileInfo(archivePath).Length;
                using var archive = ZipFile.OpenRead(archivePath);
                foreach (var (record, entry, relative) in restores)
                {
                    var mediaPath = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
                    await ExtractRequiredEntryAsync(archive, MediaBlobEntryName(entry.Sha256), mediaPath, archiveLength);
                    if (await HashFileAsync(mediaPath) != entry.Sha256)
                        throw new CryptographicException("Encrypted backup media failed its integrity check.");
                    var plaintext = await StoredMediaProcessor.ReadAsync(mediaPath, record.Id, key);
                    CryptographicOperations.ZeroMemory(plaintext);
                    await store.UpdateAsync(record with { StoredPath = Path.Combine(destination, relative) }, key);
                }
            }
            await ClearLocationSettingsAsync(store, key);
            if (name is not null) await new VaultStore(staging).RenameAsync(name);
            await StopDriveOperationPlayerAsync();
            await vaultSession.LockAndDrainAsync();
            EnsureActive();
            PublishStagedVault(staging, destination);
            galleryLifetime.PublishLocation(new GalleryStore(Path.Combine(destination, CatalogFileName)),
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
            finally { lifecycleGate.Release(); }
        }
    }

    // The fixed-format header holds only a name, key slots and salts; anything larger is not a genuine header.
    internal const long MaximumVaultHeaderBytes = 1024 * 1024;

    private static string MediaBlobEntryName(string sha256) => $"media/{sha256}.blob";

    // Each blob name maps to the first (newest) archive in the chain that contains it.
    private static Dictionary<string, int> IndexArchiveMediaEntries(IReadOnlyList<string> archivePaths)
    {
        var archiveIndexByBlobName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var archiveIndex = 0; archiveIndex < archivePaths.Count; archiveIndex++)
        {
            using var archive = ZipFile.OpenRead(archivePaths[archiveIndex]);
            foreach (var entry in archive.Entries)
                if (entry.FullName.StartsWith("media/", StringComparison.Ordinal))
                    archiveIndexByBlobName.TryAdd(entry.FullName, archiveIndex);
        }
        return archiveIndexByBlobName;
    }

    // Backups are written without compression, so no genuine entry, nor all entries together, expands beyond the
    // archive itself. Declared sizes are then enforced while each entry is copied.
    internal static void RequireEntriesWithinArchive(ZipArchive archive, long archiveLength)
    {
        long declaredBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length < 0 || entry.Length > archiveLength - declaredBytes)
                throw new InvalidDataException("The backup archive declares more content than it contains.");
            declaredBytes += entry.Length;
        }
    }

    private static async Task ExtractRequiredEntryAsync(
        ZipArchive archive, string name, string destination, long maximumBytes)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Backup is missing '{name}'.");
        if (entry.Length > maximumBytes)
            throw new InvalidDataException($"Backup entry '{name}' exceeds its supported size.");
        if (File.Exists(destination)) throw new IOException($"The restore file '{destination}' already exists.");
        await VerifyArchiveEntryAsync(entry, expectedHash: null, destination);
    }
}
