using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Core.Features.Backups.Targets;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Shared.Files;

namespace Gallery.Core.Features.Backups;

internal static class BackupRestoreStaging
{
    // The caller owns and clears the returned key after it finishes publishing the staged vault.
    internal static async Task<(IGalleryStore Store, byte[] Key)> StageRestoreAsync(
        IGalleryStoreFactory storeFactory, IBackupTarget target, string latestBackupName, string staging,
        string destination, string credential, bool useRecovery)
    {
        byte[]? key = null;
        try
        {
            var archivePaths = new List<string>();
            var chain = new BackupChain(target);
            var current = latestBackupName;
            BackupArchive.Manifest? latestManifest = null;
            while (true)
            {
                chain.Visit(current, "The backup chain contains a cycle or is too long.");
                var archivePath = Path.Combine(staging, $"backup-{archivePaths.Count}.zip");
                await target.DownloadAsync(current, archivePath);
                archivePaths.Add(archivePath);
                var archiveLength = new FileInfo(archivePath).Length;
                using var archive = ZipFile.OpenRead(archivePath);
                BackupArchive.RequireEntriesWithinArchive(archive, archiveLength);
                if (key is null)
                {
                    await BackupArchive.ExtractRequiredEntryAsync(archive, BackupArchive.VaultHeaderFileName, Path.Combine(staging, BackupArchive.VaultHeaderFileName),
                        BackupArchive.MaximumVaultHeaderBytes);
                    var stagedVaultStore = new VaultStore(staging);
                    key = useRecovery
                        ? await stagedVaultStore.UnlockWithRecoveryPhraseAsync(credential)
                        : await stagedVaultStore.UnlockWithPassphraseAsync(credential);
                }
                var manifest = ArchiveManifestProcessor.Read<BackupArchive.Manifest>(BackupArchive.ReadArchiveManifest(archive), key);
                BackupChain.ValidateManifest(manifest, "Unsupported or corrupt backup manifest.");
                if (latestManifest is null)
                {
                    latestManifest = manifest;
                    await BackupArchive.ExtractRequiredEntryAsync(archive, BackupArchive.CatalogFileName, Path.Combine(staging, BackupArchive.CatalogFileName), archiveLength);
                    if (manifest.VaultHeaderSha256 != await FileSha256.ComputeAsync(Path.Combine(staging, BackupArchive.VaultHeaderFileName))
                        || manifest.DatabaseFingerprint != await BackupArchive.ReadSnapshotFingerprintAsync(
                            storeFactory.Create(staging), Path.Combine(staging, BackupArchive.CatalogFileName), key))
                        throw new CryptographicException("Backup header or database integrity check failed.");
                }
                var parentName = BackupChain.ReadParentName(manifest,
                    "The full backup cannot have a parent.", "The backup chain must lead to a full backup.");
                if (parentName is null) break;
                current = parentName;
            }

            var store = storeFactory.Create(staging);
            await RestoreMediaAsync(store, latestManifest!, archivePaths, staging, destination, key);
            return (store, key);
        }
        catch
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private static async Task RestoreMediaAsync(IGalleryStore store, BackupArchive.Manifest latestManifest,
        IReadOnlyList<string> archivePaths, string staging, string destination, byte[] key)
    {
        var records = await store.ReadRecordsAsync(key);
        if (records.Count != latestManifest.Media.Count)
            throw new CryptographicException("The backup catalog and manifest disagree.");
        var archiveIndexByBlobName = IndexArchiveMediaEntries(archivePaths);
        var restoresByArchiveIndex = new SortedDictionary<int, List<(ImageRecord Record, BackupArchive.MediaEntry Entry, string Relative)>>();
        foreach (var record in records)
        {
            var relative = StoredMediaProcessor.RelativePath(record);
            if (!latestManifest.Media.TryGetValue(record.Id, out var entry)
                || entry.Extension != Path.GetExtension(relative)
                || entry.StoredPath != record.StoredPath
                || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
                throw new CryptographicException("Invalid media entry in the backup manifest.");
            if (!archiveIndexByBlobName.TryGetValue(BackupArchive.MediaBlobEntryName(entry.Sha256), out var archiveIndex))
                throw new InvalidOperationException($"The full/incremental chain is missing media for '{record.Name}'.");
            if (!restoresByArchiveIndex.TryGetValue(archiveIndex, out var restores))
                restoresByArchiveIndex.Add(archiveIndex, restores = []);
            restores.Add((record, entry, relative));
        }
        // Open each archive once during extraction, for all of its requested blobs.
        foreach (var (archiveIndex, restores) in restoresByArchiveIndex)
        {
            var archivePath = archivePaths[archiveIndex];
            var archiveLength = new FileInfo(archivePath).Length;
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var (record, entry, relative) in restores)
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
}
