using Gallery.Core.Features.Media.Processors;
using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Core.Features.Backups.Targets;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Features.Files.Processors;

namespace Gallery.Core.Features.Backups;

internal static class BackupRestoreStaging
{
    // The caller owns and clears the returned key after it finishes publishing the staged vault.
    internal static async Task<(IGalleryStore Store, byte[] Key)> StageRestoreAsync(
        IGalleryStoreFactory storeFactory, IBackupTarget target, string latestBackupName, string staging,
        string destination, string credential, VaultCredentialKind vaultCredentialKind)
    {
        Func<VaultStore, string, Task<byte[]>> unlockVault = vaultCredentialKind == VaultCredentialKind.RecoveryPhrase
            ? UnlockWithRecoveryPhraseAsync
            : UnlockWithPassphraseAsync;
        return await StageRestoreCoreAsync(
            storeFactory, target, latestBackupName, staging, destination, credential, unlockVault);
    }

    private static async Task<(IGalleryStore Store, byte[] Key)> StageRestoreCoreAsync(
        IGalleryStoreFactory storeFactory, IBackupTarget target, string latestBackupName, string staging,
        string destination, string credential, Func<VaultStore, string, Task<byte[]>> unlockVault)
    {
        var chain = new RestoreChain();
        try
        {
            await LoadChainAsync(storeFactory, target, latestBackupName, staging, credential, unlockVault, chain);
            var store = storeFactory.Create(staging);
            await RestoreMediaAsync(store, chain.LatestManifest!, chain.ArchivePaths, staging, destination, chain.Key!, chain.ArchiveIndexByBlobName);
            return (store, chain.Key!);
        }
        catch
        {
            if (chain.Key is not null) CryptographicOperations.ZeroMemory(chain.Key);
            throw;
        }
    }

    private sealed class RestoreChain
    {
        internal byte[]? Key { get; set; }
        internal BackupArchive.Manifest? LatestManifest { get; set; }
        internal List<string> ArchivePaths { get; } = [];
        internal Dictionary<string, int> ArchiveIndexByBlobName { get; } = new(StringComparer.Ordinal);
    }

    private static async Task LoadChainAsync(
        IGalleryStoreFactory storeFactory, IBackupTarget target, string latestBackupName, string staging,
        string credential, Func<VaultStore, string, Task<byte[]>> unlockVault, RestoreChain chain)
    {
        var visitedArchiveNames = new HashSet<string>(StringComparer.Ordinal);
        var current = latestBackupName;
        while (true)
        {
            BackupChain.Visit(target, current, visitedArchiveNames, "The backup chain contains a cycle or is too long.");
            var archivePath = Path.Combine(staging, $"backup-{chain.ArchivePaths.Count}.zip");
            await target.DownloadAsync(current, archivePath);
            chain.ArchivePaths.Add(archivePath);
            var archiveLength = new FileInfo(archivePath).Length;
            using var archive = ZipFile.OpenRead(archivePath);
            BackupArchive.RequireEntriesWithinArchive(archive, archiveLength);
            foreach (var entry in archive.Entries)
                if (entry.FullName.StartsWith("media/", StringComparison.Ordinal))
                    chain.ArchiveIndexByBlobName.TryAdd(entry.FullName, chain.ArchivePaths.Count - 1);
            if (chain.Key is null)
            {
                await BackupArchive.ExtractRequiredEntryAsync(archive, BackupArchive.VaultHeaderFileName, Path.Combine(staging, BackupArchive.VaultHeaderFileName),
                    BackupArchive.MaximumVaultHeaderBytes);
                var stagedVaultStore = new VaultStore(staging);
                chain.Key = await unlockVault(stagedVaultStore, credential);
            }
            var manifest = ArchiveManifestProcessor.Read<BackupArchive.Manifest>(BackupArchive.ReadArchiveManifest(archive), chain.Key);
            BackupChain.ValidateManifest(manifest, "Unsupported or corrupt backup manifest.");
            if (chain.LatestManifest is null)
            {
                chain.LatestManifest = manifest;
                await StageAndVerifyCatalogAsync(storeFactory, archive, archiveLength, manifest, staging, chain.Key);
            }
            var parentName = BackupChain.ReadParentName(manifest,
                "The full backup cannot have a parent.", "The backup chain must lead to a full backup.");
            if (parentName is null) break;
            current = parentName;
        }
    }

    private static async Task StageAndVerifyCatalogAsync(IGalleryStoreFactory storeFactory, ZipArchive archive,
        long archiveLength, BackupArchive.Manifest manifest, string staging, byte[] key)
    {
        await BackupArchive.ExtractRequiredEntryAsync(archive, BackupArchive.CatalogFileName, Path.Combine(staging, BackupArchive.CatalogFileName), archiveLength);
        if (manifest.VaultHeaderSha256 != await FileSha256.ComputeAsync(Path.Combine(staging, BackupArchive.VaultHeaderFileName))
            || manifest.DatabaseFingerprint != await BackupArchive.ReadSnapshotFingerprintAsync(
                storeFactory.Create(staging), Path.Combine(staging, BackupArchive.CatalogFileName), key))
            throw new CryptographicException("Backup header or database integrity check failed.");
    }

    private static Task<byte[]> UnlockWithRecoveryPhraseAsync(VaultStore vaultStore, string credential) =>
        vaultStore.UnlockWithRecoveryPhraseAsync(credential);

    private static Task<byte[]> UnlockWithPassphraseAsync(VaultStore vaultStore, string credential) =>
        vaultStore.UnlockWithPassphraseAsync(credential);

    private static async Task RestoreMediaAsync(IGalleryStore store, BackupArchive.Manifest latestManifest,
        IReadOnlyList<string> archivePaths, string staging, string destination, byte[] key,
        IReadOnlyDictionary<string, int> archiveIndexByBlobName)
    {
        var records = await store.ReadRecordsAsync(key);
        if (records.Count != latestManifest.Media.Count)
            throw new CryptographicException("The backup catalog and manifest disagree.");
        var restoresByArchiveIndex = BuildRestorePlan(records, latestManifest, archiveIndexByBlobName);
        await RestorePlannedMediaAsync(store, restoresByArchiveIndex, archivePaths, staging, destination, key);
    }

    private static SortedDictionary<int, List<(ImageRecord Record, BackupArchive.MediaEntry Entry, string Relative)>> BuildRestorePlan(
        IReadOnlyList<ImageRecord> records, BackupArchive.Manifest latestManifest,
        IReadOnlyDictionary<string, int> archiveIndexByBlobName)
    {
        var restoresByArchiveIndex = new SortedDictionary<int, List<(ImageRecord Record, BackupArchive.MediaEntry Entry, string Relative)>>();
        foreach (var record in records)
        {
            var relative = StoredMediaProcessor.RelativePath(record);
            if (!latestManifest.Media.TryGetValue(record.Id, out var entry))
                throw new CryptographicException("Invalid media entry in the backup manifest.");
            if (!IsValidRestoreEntry(record, entry, relative))
                throw new CryptographicException("Invalid media entry in the backup manifest.");
            if (!archiveIndexByBlobName.TryGetValue(BackupArchive.MediaBlobEntryName(entry.Sha256), out var archiveIndex))
                throw new InvalidOperationException($"The full/incremental chain is missing media for '{record.Name}'.");
            if (!restoresByArchiveIndex.TryGetValue(archiveIndex, out var restores))
                restoresByArchiveIndex.Add(archiveIndex, restores = []);
            restores.Add((record, entry, relative));
        }
        return restoresByArchiveIndex;
    }

    private static bool IsValidRestoreEntry(ImageRecord record, BackupArchive.MediaEntry? entry, string relative) =>
        entry is not null
        && entry.Extension == Path.GetExtension(relative)
        && entry.StoredPath == record.StoredPath
        && BackupArchive.IsValidSha256(entry.Sha256);

    private static async Task RestorePlannedMediaAsync(IGalleryStore store,
        SortedDictionary<int, List<(ImageRecord Record, BackupArchive.MediaEntry Entry, string Relative)>> restoresByArchiveIndex,
        IReadOnlyList<string> archivePaths, string staging, string destination, byte[] key)
    {
        foreach (var (archiveIndex, restores) in restoresByArchiveIndex)
        {
            var archivePath = archivePaths[archiveIndex];
            var archiveLength = new FileInfo(archivePath).Length;
            using var archive = ZipFile.OpenRead(archivePath);
            foreach ((ImageRecord record, BackupArchive.MediaEntry entry, string relative) in restores)
            {
                var mediaPath = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
                await BackupArchive.ExtractRequiredEntryAsync(archive, BackupArchive.MediaBlobEntryName(entry.Sha256), mediaPath, archiveLength);
                if (await FileSha256.ComputeAsync(mediaPath) != entry.Sha256)
                    throw new CryptographicException("Encrypted backup media failed its integrity check.");
                var plaintext = await StoredMediaProcessor.ReadAsync(mediaPath, record.Id, key);
                CryptographicOperations.ZeroMemory(plaintext);
                await store.UpdateAsync(record with { StoredPath = Path.Combine(destination, relative) }, key);
            }
        }
    }
}
