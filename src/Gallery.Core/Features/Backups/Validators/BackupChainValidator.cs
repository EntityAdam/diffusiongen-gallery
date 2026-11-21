using System.Data.Common;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Backups.Targets;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Backups;

internal static class BackupChainValidator
{
    internal static async Task<(BackupArchive.Manifest Manifest, int Length)> ValidateChainAsync(
        IGalleryStoreFactory storeFactory, IBackupTarget target, string latestName, string stagingDirectory, byte[] key)
    {
        using var activity = BackupArchive.ActivitySource.StartActivity("backup.chain.validate");
        var archiveName = latestName;
        var visitedArchiveNames = new HashSet<string>(StringComparer.Ordinal);
        var availableMediaHashes = new HashSet<string>(StringComparer.Ordinal);
        var referencedMediaHashes = new HashSet<string>(StringComparer.Ordinal);
        BackupArchive.Manifest? latestManifest = null;
        var chainLength = 0;
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
                chainLength = BackupChain.Visit(
                    target, archiveName, visitedArchiveNames,
                    "The incremental backup chain contains a cycle or is too long. Create a new full backup.");
                if (!await target.ExistsAsync(archiveName) || !await target.ExistsAsync(BackupArchive.ManifestName(archiveName)))
                    throw new InvalidOperationException($"Backup '{archiveName}' or its manifest is missing. Create a new full backup.");
                var manifestPath = Path.Combine(stagingDirectory, $"previous-{chainIndex++}.manifest");
                await DownloadTimedAsync(target, BackupArchive.ManifestName(archiveName), manifestPath,
                    elapsed => manifestDownloadMilliseconds += elapsed);
                manifestBytes += new FileInfo(manifestPath).Length;
                var manifest = await ReadManifestFileAsync(manifestPath, key);
                BackupChain.ValidateManifest(manifest, "A backup manifest is invalid or unsupported. Create a new full backup.");
                var archivePath = Path.Combine(stagingDirectory, $"previous-{chainIndex}.zip");
                await DownloadTimedAsync(target, archiveName, archivePath,
                    elapsed => archiveDownloadMilliseconds += elapsed);
                archiveCount++;
                archiveBytes += new FileInfo(archivePath).Length;
                try
                {
                    var validationTimer = Stopwatch.StartNew();
                    try
                    {
                        var validation = await ValidateParentArchiveAsync(
                            storeFactory, archivePath, manifestPath, manifest, key, availableMediaHashes, stagingDirectory);
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
                CollectReferences(manifest, referencedMediaHashes);
                if (latestManifest is null) latestManifest = manifest;
                var parentName = BackupChain.ReadParentName(manifest,
                    "The full backup has an invalid parent link. Create a new full backup.",
                    "The incremental backup chain does not lead to a valid full backup.");
                if (parentName is null)
                {
                    if (!referencedMediaHashes.IsSubsetOf(availableMediaHashes))
                        throw new InvalidOperationException("The backup chain is missing referenced media. Create a new full backup.");
                    return (latestManifest, chainLength);
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

    private static async Task DownloadTimedAsync(IBackupTarget target, string name, string path, Action<double> recordElapsed)
    {
        var timer = Stopwatch.StartNew();
        try { await target.DownloadAsync(name, path); }
        finally
        {
            timer.Stop();
            recordElapsed(timer.Elapsed.TotalMilliseconds);
        }
    }

    private static void CollectReferences(BackupArchive.Manifest manifest, HashSet<string> referencedMediaHashes)
    {
        foreach (var entry in manifest.Media.Values)
        {
            if (entry is null || !BackupArchive.IsValidSha256(entry.Sha256))
                throw new InvalidOperationException("A backup manifest contains an invalid media reference. Create a new full backup.");
            referencedMediaHashes.Add(entry.Sha256);
        }
    }

    private static async Task<(long VerifiedBytes, int MediaEntryCount)> ValidateParentArchiveAsync(
        IGalleryStoreFactory storeFactory, string archivePath, string sidecarPath, BackupArchive.Manifest manifest, byte[] key,
        HashSet<string> availableMediaHashes, string stagingDirectory)
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

        var (backend, verifiedBytes) = await ValidateHeaderAsync(archive, manifest);
        verifiedBytes += await ValidateDatabaseAsync(storeFactory, archive, manifest, key, backend, stagingDirectory);
        var media = await ValidateMediaAsync(archive, availableMediaHashes);
        return (verifiedBytes + media.VerifiedBytes, media.MediaEntryCount);
    }

    private static async Task<(CatalogBackend Backend, long VerifiedBytes)> ValidateHeaderAsync(
        ZipArchive archive, BackupArchive.Manifest manifest)
    {
        var header = archive.GetEntry(BackupArchive.VaultHeaderFileName)
            ?? throw new InvalidDataException("The archive is missing 'vault.json'.");
        var verifiedBytes = await BackupArchive.VerifyArchiveEntryAsync(header, manifest.VaultHeaderSha256);
        if (header.Length > BackupArchive.MaximumVaultHeaderBytes) throw new InvalidDataException("The backup header is too large.");
        using var headerReader = new StreamReader(header.Open());
        var backend = VaultStore.ReadCatalogBackend(await headerReader.ReadToEndAsync());
        return (backend, verifiedBytes);
    }

    private static async Task<long> ValidateDatabaseAsync(
        IGalleryStoreFactory storeFactory, ZipArchive archive, BackupArchive.Manifest manifest,
        byte[] key, CatalogBackend backend, string stagingDirectory)
    {
        var database = archive.GetEntry(BackupArchive.CatalogFileName)
            ?? throw new InvalidDataException("The archive is missing 'gallery.db'.");
        var databasePath = Path.Combine(stagingDirectory, "parent-gallery.db");
        try
        {
            var verifiedBytes = await BackupArchive.VerifyArchiveEntryAsync(database, expectedHash: null, databasePath);
            if (manifest.DatabaseFingerprint is not null
                && manifest.DatabaseFingerprint != await BackupArchive.ReadSnapshotFingerprintAsync(
                    storeFactory.CreateForDatabase(databasePath, backend), databasePath, key))
                throw new InvalidDataException("The archive database disagrees with its manifest.");
            return verifiedBytes;
        }
        finally { File.Delete(databasePath); }
    }

    private static async Task<(long VerifiedBytes, int MediaEntryCount)> ValidateMediaAsync(
        ZipArchive archive, HashSet<string> availableMediaHashes)
    {
        long verifiedBytes = 0;
        var mediaEntryCount = 0;
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("media/", StringComparison.Ordinal)))
        {
            if (!IsMediaBlobFileName(entry.FullName))
                throw new InvalidDataException("The archive has an invalid media entry.");
            var hash = entry.FullName["media/".Length..^".blob".Length];
            verifiedBytes += await BackupArchive.VerifyArchiveEntryAsync(entry, hash);
            mediaEntryCount++;
            availableMediaHashes.Add(hash);
        }
        return (verifiedBytes, mediaEntryCount);
    }

    private static bool IsMediaBlobFileName(string name) =>
        name.Length == "media/".Length + 64 + ".blob".Length
        && name.StartsWith("media/", StringComparison.Ordinal)
        && name.EndsWith(".blob", StringComparison.Ordinal)
        && BackupArchive.IsValidSha256(name["media/".Length..^".blob".Length]);

    private static async Task<BackupArchive.Manifest> ReadManifestFileAsync(string manifestPath, byte[] key)
    {
        var encrypted = await BackupArchive.ReadEncryptedManifestFileAsync(manifestPath);
        return ArchiveManifestProcessor.Read<BackupArchive.Manifest>(encrypted, key);
    }
}
