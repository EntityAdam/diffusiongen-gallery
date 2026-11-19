using System.Data.Common;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Backups.Targets;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence;

namespace Gallery.Core.Features.Backups;

internal static class BackupChainValidator
{
    internal static async Task<BackupArchive.Manifest> ValidateChainAsync(
        IGalleryStoreFactory storeFactory, IBackupTarget target, string latestName, string stagingDirectory, byte[] key)
    {
        using var activity = BackupArchive.ActivitySource.StartActivity("backup.chain.validate");
        var archiveName = latestName;
        var chain = new BackupChain(target);
        var availableMedia = new HashSet<string>(StringComparer.Ordinal);
        var referencedMedia = new HashSet<string>(StringComparer.Ordinal);
        BackupArchive.Manifest? latestManifest = null;
        var chainIndex = 0;
        var archiveCount = 0;
        long archiveBytes = 0;
        long manifestBytes = 0;
        double archiveDownloadMilliseconds = 0;
        double manifestDownloadMilliseconds = 0;
        double archiveValidationMilliseconds = 0;
        long archiveContentVerifiedBytes = 0;
        var archiveMediaEntryCount = 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                chain.Visit(archiveName, "The incremental backup chain contains a cycle or is too long. Create a new full backup.");
                if (!await target.ExistsAsync(archiveName) || !await target.ExistsAsync(BackupArchive.ManifestName(archiveName)))
                    throw new InvalidOperationException($"Backup '{archiveName}' or its manifest is missing. Create a new full backup.");
                var manifestPath = Path.Combine(stagingDirectory, $"previous-{chainIndex++}.manifest");
                var downloadTimer = Stopwatch.StartNew();
                try { await target.DownloadAsync(BackupArchive.ManifestName(archiveName), manifestPath); }
                finally
                {
                    downloadTimer.Stop();
                    manifestDownloadMilliseconds += downloadTimer.Elapsed.TotalMilliseconds;
                }
                manifestBytes += new FileInfo(manifestPath).Length;
                var manifest = await ReadManifestFileAsync(manifestPath, key);
                BackupChain.ValidateManifest(manifest, "A backup manifest is invalid or unsupported. Create a new full backup.");
                var archivePath = Path.Combine(stagingDirectory, $"previous-{chainIndex}.zip");
                downloadTimer.Restart();
                try { await target.DownloadAsync(archiveName, archivePath); }
                finally
                {
                    downloadTimer.Stop();
                    archiveDownloadMilliseconds += downloadTimer.Elapsed.TotalMilliseconds;
                }
                archiveCount++;
                archiveBytes += new FileInfo(archivePath).Length;
                try
                {
                    var validationTimer = Stopwatch.StartNew();
                    try
                    {
                        var validation = await ValidateParentArchiveAsync(
                            storeFactory, archivePath, manifestPath, manifest, key, availableMedia, stagingDirectory);
                        archiveContentVerifiedBytes += validation.VerifiedBytes;
                        archiveMediaEntryCount += validation.MediaEntryCount;
                    }
                    finally
                    {
                        validationTimer.Stop();
                        archiveValidationMilliseconds += validationTimer.Elapsed.TotalMilliseconds;
                    }
                }
                catch (Exception error) when (error is InvalidDataException or IOException or CryptographicException
                    or InvalidOperationException or JsonException or DbException or CatalogStorageException)
                {
                    throw new InvalidOperationException(
                        $"Backup '{archiveName}' is damaged or disagrees with its manifest. Create a new full backup.", error);
                }
                finally { File.Delete(archivePath); }
                foreach (var entry in manifest.Media.Values)
                {
                    if (entry is null || entry.Sha256 is null || entry.Sha256.Length != 64
                        || entry.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
                        throw new InvalidOperationException("A backup manifest contains an invalid media reference. Create a new full backup.");
                    referencedMedia.Add(entry.Sha256);
                }
                if (latestManifest is null) latestManifest = manifest;
                var parentName = BackupChain.ReadParentName(manifest,
                    "The full backup has an invalid parent link. Create a new full backup.",
                    "The incremental backup chain does not lead to a valid full backup.");
                if (parentName is null)
                {
                    if (!referencedMedia.IsSubsetOf(availableMedia))
                        throw new InvalidOperationException("The backup chain is missing referenced media. Create a new full backup.");
                    return latestManifest;
                }
                archiveName = parentName;
            }
        }
        finally
        {
            stopwatch.Stop();
            activity?.SetTag("backup.chain.archive_count", archiveCount);
            activity?.SetTag("backup.chain.archive_download_bytes", archiveBytes);
            activity?.SetTag("backup.chain.manifest_download_bytes", manifestBytes);
            activity?.SetTag("backup.chain.total_download_bytes", archiveBytes + manifestBytes);
            activity?.SetTag("backup.chain.archive_download_elapsed_ms", archiveDownloadMilliseconds);
            activity?.SetTag("backup.chain.manifest_download_elapsed_ms", manifestDownloadMilliseconds);
            activity?.SetTag("backup.chain.total_download_elapsed_ms",
                archiveDownloadMilliseconds + manifestDownloadMilliseconds);
            activity?.SetTag("backup.chain.archive_validation_elapsed_ms", archiveValidationMilliseconds);
            activity?.SetTag("backup.chain.content_verified_bytes", archiveContentVerifiedBytes);
            activity?.SetTag("backup.chain.media_entry_count", archiveMediaEntryCount);
            activity?.SetTag("backup.chain.validation_elapsed_ms", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task<(long VerifiedBytes, int MediaEntryCount)> ValidateParentArchiveAsync(
        IGalleryStoreFactory storeFactory, string archivePath, string sidecarPath, BackupArchive.Manifest manifest, byte[] key,
        HashSet<string> availableMedia, string stagingDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var archiveLength = new FileInfo(archivePath).Length;
        BackupArchive.RequireEntriesWithinArchive(archive, archiveLength);
        var embedded = archive.GetEntry("manifest.enc")
            ?? throw new InvalidDataException("The archive is missing its embedded manifest.");
        if (embedded.Length > archiveLength)
            throw new InvalidDataException("The embedded manifest is larger than its archive.");
        var encrypted = BackupArchive.ReadArchiveManifest(embedded);
        var sidecar = await BackupArchive.ReadEncryptedManifestFileAsync(sidecarPath);
        if (!CryptographicOperations.FixedTimeEquals(encrypted, sidecar))
            throw new InvalidDataException("The sidecar and embedded manifests disagree.");
        _ = ArchiveManifestProcessor.Read<BackupArchive.Manifest>(encrypted, key);

        var header = archive.GetEntry(BackupArchive.VaultHeaderFileName)
            ?? throw new InvalidDataException("The archive is missing 'vault.json'.");
        var verifiedBytes = await BackupArchive.VerifyArchiveEntryAsync(header, manifest.VaultHeaderSha256);
        if (header.Length > BackupArchive.MaximumVaultHeaderBytes) throw new InvalidDataException("The backup header is too large.");
        using var headerReader = new StreamReader(header.Open());
        var backend = VaultStore.ReadCatalogBackend(await headerReader.ReadToEndAsync());
        var database = archive.GetEntry(BackupArchive.CatalogFileName)
            ?? throw new InvalidDataException("The archive is missing 'gallery.db'.");
        var databasePath = Path.Combine(stagingDirectory, "parent-gallery.db");
        try
        {
            verifiedBytes += await BackupArchive.VerifyArchiveEntryAsync(database, expectedHash: null, databasePath);
            if (manifest.DatabaseFingerprint is not null
                && manifest.DatabaseFingerprint != await BackupArchive.ReadSnapshotFingerprintAsync(
                    storeFactory.CreateForDatabase(databasePath, backend), databasePath, key))
                throw new InvalidDataException("The archive database disagrees with its manifest.");
        }
        finally { File.Delete(databasePath); }
        var mediaEntryCount = 0;
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("media/", StringComparison.Ordinal)))
        {
            var filename = entry.FullName["media/".Length..];
            if (filename.Length != 69 || !filename.EndsWith(".blob", StringComparison.Ordinal)
                || filename[..64].Any(character => !char.IsAsciiHexDigit(character)))
                throw new InvalidDataException("The archive has an invalid media entry.");
            var hash = filename[..64];
            verifiedBytes += await BackupArchive.VerifyArchiveEntryAsync(entry, hash);
            mediaEntryCount++;
            availableMedia.Add(hash);
        }
        return (verifiedBytes, mediaEntryCount);
    }

    private static async Task<BackupArchive.Manifest> ReadManifestFileAsync(string manifestPath, byte[] key)
    {
        var encrypted = await BackupArchive.ReadEncryptedManifestFileAsync(manifestPath);
        return ArchiveManifestProcessor.Read<BackupArchive.Manifest>(encrypted, key);
    }
}
