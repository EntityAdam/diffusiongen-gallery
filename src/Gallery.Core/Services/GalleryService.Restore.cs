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

    public Task<IReadOnlyList<string>> ListRestoreBackupsFromSasAsync(string sasUrl) =>
        CreateSasRestoreTarget(sasUrl).ListAsync();

    private static IBackupTarget CreateSasRestoreTarget(string sasUrl) => new AzureBackupTarget(sasUrl);

    private static IBackupTarget CreateRestoreTarget(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString)
    {
        if (sourceType == "local")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
            return new LocalBackupTarget(Path.GetFullPath(localDirectory.Trim()));
        }
        if (sourceType == "azure")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(azureContainer);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            return new AzureBackupTarget(connectionString.Trim(), azureContainer.Trim(), NormalizeAzurePrefix(azurePrefix));
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
        string credential, bool useRecovery = false, string? name = null) =>
        RestoreVaultFromTargetAsync(CreateSasRestoreTarget(sasUrl), latestBackupName, destinationDirectory,
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
                using var archive = ZipFile.OpenRead(archivePath);
                if (key is null)
                {
                    ExtractRequiredEntry(archive, VaultHeaderFileName, Path.Combine(staging, VaultHeaderFileName));
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
                    ExtractRequiredEntry(archive, CatalogFileName, Path.Combine(staging, CatalogFileName));
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
            foreach (var record in records)
            {
                var relative = MediaRelativePath(record);
                if (!latestManifest.Media.TryGetValue(record.Id, out var entry)
                    || entry.Extension != Path.GetExtension(relative)
                    || entry.StoredPath != record.StoredPath
                    || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
                    throw new CryptographicException("Invalid media entry in the backup manifest.");
                var mediaPath = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
                var found = false;
                foreach (var archivePath in archivePaths)
                {
                    using var archive = ZipFile.OpenRead(archivePath);
                    var blob = archive.GetEntry($"media/{entry.Sha256}.blob");
                    if (blob is null) continue;
                    blob.ExtractToFile(mediaPath, overwrite: false);
                    found = true;
                    break;
                }
                if (!found) throw new InvalidOperationException($"The full/incremental chain is missing media for '{record.Name}'.");
                if (await HashFileAsync(mediaPath) != entry.Sha256)
                    throw new CryptographicException("Encrypted backup media failed its integrity check.");
                var plaintext = await StoredMediaProcessor.ReadAsync(mediaPath, record.Id, key);
                CryptographicOperations.ZeroMemory(plaintext);
                await store.UpdateAsync(record with { StoredPath = Path.Combine(destination, relative) }, key);
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

    private static void ExtractRequiredEntry(ZipArchive archive, string name, string destination) =>
        (archive.GetEntry(name) ?? throw new InvalidOperationException($"Backup is missing '{name}'."))
            .ExtractToFile(destination, overwrite: false);
}
