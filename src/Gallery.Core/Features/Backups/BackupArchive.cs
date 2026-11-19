using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Backups.Targets;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Shared.Files;

namespace Gallery.Core.Features.Backups;

internal static class BackupArchive
{
    internal const string ActivitySourceName = "Gallery.Core.Backup";
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private const string CatalogFileName = GalleryStoreFactory.CatalogFileName;
    private const string VaultHeaderFileName = VaultStore.HeaderFileName;
    private const int MaximumEncryptedManifestBytes = 16 * 1024 * 1024;
    // The fixed-format header holds only a name, key slots and salts.
    private const long MaximumVaultHeaderBytes = 1024 * 1024;

    internal sealed record MediaEntry(string StoredPath, string Sha256, string Extension);
    internal sealed record Manifest(
        int FormatVersion,
        string BackupId,
        string BackupType,
        string? ParentBackupName,
        DateTimeOffset CreatedAt,
        ImmutableDictionary<string, MediaEntry> Media,
        string? DatabaseFingerprint = null,
        string? VaultHeaderSha256 = null);

    internal static async Task<Manifest> ValidateChainAsync(
        IGalleryStoreFactory storeFactory, IBackupTarget target, string latestName, string stagingDirectory, byte[] key)
    {
        using var activity = ActivitySource.StartActivity("backup.chain.validate");
        var archiveName = latestName;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var availableMedia = new HashSet<string>(StringComparer.Ordinal);
        var referencedMedia = new HashSet<string>(StringComparer.Ordinal);
        Manifest? latestManifest = null;
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
                target.ValidateArchiveName(archiveName);
                if (!visited.Add(archiveName) || visited.Count > 10000)
                    throw new InvalidOperationException("The incremental backup chain contains a cycle or is too long. Create a new full backup.");
                if (!await target.ExistsAsync(archiveName) || !await target.ExistsAsync(ManifestName(archiveName)))
                    throw new InvalidOperationException($"Backup '{archiveName}' or its manifest is missing. Create a new full backup.");
                var manifestPath = Path.Combine(stagingDirectory, $"previous-{chainIndex++}.manifest");
                var downloadTimer = Stopwatch.StartNew();
                try { await target.DownloadAsync(ManifestName(archiveName), manifestPath); }
                finally
                {
                    downloadTimer.Stop();
                    manifestDownloadMilliseconds += downloadTimer.Elapsed.TotalMilliseconds;
                }
                manifestBytes += new FileInfo(manifestPath).Length;
                var manifest = await ReadManifestFileAsync(manifestPath, key);
                if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.BackupId)
                    || manifest.Media is null)
                    throw new InvalidOperationException("A backup manifest is invalid or unsupported. Create a new full backup.");
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
                if (manifest.BackupType == "full")
                {
                    if (manifest.ParentBackupName is not null)
                        throw new InvalidOperationException("The full backup has an invalid parent link. Create a new full backup.");
                    if (!referencedMedia.IsSubsetOf(availableMedia))
                        throw new InvalidOperationException("The backup chain is missing referenced media. Create a new full backup.");
                    return latestManifest;
                }
                if (manifest.BackupType != "incremental" || string.IsNullOrWhiteSpace(manifest.ParentBackupName))
                    throw new InvalidOperationException("The incremental backup chain does not lead to a valid full backup.");
                archiveName = manifest.ParentBackupName;
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

    internal static string ManifestName(string archiveName) => archiveName + ".manifest";

    private static async Task<(long VerifiedBytes, int MediaEntryCount)> ValidateParentArchiveAsync(
        IGalleryStoreFactory storeFactory, string archivePath, string sidecarPath, Manifest manifest, byte[] key,
        HashSet<string> availableMedia, string stagingDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var archiveLength = new FileInfo(archivePath).Length;
        RequireEntriesWithinArchive(archive, archiveLength);
        var embedded = archive.GetEntry("manifest.enc")
            ?? throw new InvalidDataException("The archive is missing its embedded manifest.");
        if (embedded.Length > archiveLength)
            throw new InvalidDataException("The embedded manifest is larger than its archive.");
        var encrypted = ReadArchiveManifest(embedded);
        var sidecar = await ReadEncryptedManifestFileAsync(sidecarPath);
        if (!CryptographicOperations.FixedTimeEquals(encrypted, sidecar))
            throw new InvalidDataException("The sidecar and embedded manifests disagree.");
        _ = ArchiveManifestProcessor.Read<Manifest>(encrypted, key);

        var header = archive.GetEntry(VaultHeaderFileName)
            ?? throw new InvalidDataException("The archive is missing 'vault.json'.");
        var verifiedBytes = await VerifyArchiveEntryAsync(header, manifest.VaultHeaderSha256);
        if (header.Length > MaximumVaultHeaderBytes) throw new InvalidDataException("The backup header is too large.");
        using var headerReader = new StreamReader(header.Open());
        var backend = VaultStore.ReadCatalogBackend(await headerReader.ReadToEndAsync());
        var database = archive.GetEntry(CatalogFileName)
            ?? throw new InvalidDataException("The archive is missing 'gallery.db'.");
        var databasePath = Path.Combine(stagingDirectory, "parent-gallery.db");
        try
        {
            verifiedBytes += await VerifyArchiveEntryAsync(database, expectedHash: null, databasePath);
            if (manifest.DatabaseFingerprint is not null
                && manifest.DatabaseFingerprint != await ReadSnapshotFingerprintAsync(
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
            verifiedBytes += await VerifyArchiveEntryAsync(entry, hash);
            mediaEntryCount++;
            availableMedia.Add(hash);
        }
        return (verifiedBytes, mediaEntryCount);
    }

    private static Task<string> ReadSnapshotFingerprintAsync(IGalleryCatalogSnapshots snapshots, string databasePath, byte[] key) =>
        snapshots.ComputeSnapshotFingerprintAsync(databasePath, StoredBackupSettings.SettingId, key);

    private static async Task<long> VerifyArchiveEntryAsync(
        ZipArchiveEntry entry, string? expectedHash, string? destinationPath = null)
    {
        await using var stream = entry.Open();
        using var hasher = expectedHash is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var destination = destinationPath is null ? null : File.Create(destinationPath);
        var buffer = new byte[81920];
        long length = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer)) != 0)
        {
            length += count;
            if (length > entry.Length)
                throw new InvalidDataException($"Archive entry '{entry.FullName}' exceeds its declared length.");
            hasher?.AppendData(buffer, 0, count);
            if (destination is not null) await destination.WriteAsync(buffer.AsMemory(0, count));
        }
        if (length != entry.Length || (hasher is not null
            && !Convert.ToHexString(hasher.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Archive entry '{entry.FullName}' failed its integrity check.");
        return length;
    }

    // Manifests written before hashes were recorded have null hashes and are always treated as changed.
    internal static bool IsUnchangedSince(
        Manifest previous, IReadOnlyDictionary<string, MediaEntry> media,
        string DatabaseFingerprint, string vaultHeaderSha256)
    {
        if (previous.DatabaseFingerprint != DatabaseFingerprint || previous.VaultHeaderSha256 != vaultHeaderSha256)
            return false;
        if (previous.Media.Count != media.Count) return false;
        foreach (var (id, entry) in media)
        {
            if (!previous.Media.TryGetValue(id, out var previousEntry)
                || previousEntry.Sha256 != entry.Sha256
                || previousEntry.Extension != entry.Extension)
                return false;
        }
        return true;
    }

    internal static byte[] ReadArchiveManifest(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return ReadArchiveManifest(archive);
    }

    private static byte[] ReadArchiveManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry("manifest.enc")
            ?? throw new InvalidOperationException("The completed backup archive is missing its manifest.");
        return ReadArchiveManifest(entry);
    }

    private static byte[] ReadArchiveManifest(ZipArchiveEntry entry)
    {
        if (entry.Length > MaximumEncryptedManifestBytes)
            throw new InvalidDataException("The embedded manifest is too large.");
        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        var chunk = new byte[81920];
        int count;
        while ((count = stream.Read(chunk)) != 0)
        {
            if (buffer.Length + count > MaximumEncryptedManifestBytes
                || buffer.Length + count > entry.Length)
                throw new InvalidDataException("The embedded manifest exceeds its supported size.");
            buffer.Write(chunk, 0, count);
        }
        if (buffer.Length != entry.Length)
            throw new InvalidDataException("The embedded manifest is incomplete.");
        return buffer.ToArray();
    }

    private static async Task<Manifest> ReadManifestFileAsync(string manifestPath, byte[] key)
    {
        var encrypted = await ReadEncryptedManifestFileAsync(manifestPath);
        return ArchiveManifestProcessor.Read<Manifest>(encrypted, key);
    }

    internal static async Task<byte[]> ReadEncryptedManifestFileAsync(string manifestPath)
    {
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumEncryptedManifestBytes)
            throw new InvalidOperationException("A backup manifest is too large. Create a new full backup.");
        using var buffer = new MemoryStream((int)stream.Length);
        var chunk = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > MaximumEncryptedManifestBytes)
                throw new InvalidOperationException("A backup manifest is too large. Create a new full backup.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    internal static async Task WriteAsync(
        string archivePath, Manifest manifest, IReadOnlyDictionary<string, string> stagedMedia, byte[] key)
    {
        var encryptedManifest = ArchiveManifestProcessor.Protect(manifest, key);
        try
        {
            if (encryptedManifest.Length > MaximumEncryptedManifestBytes)
                throw new InvalidOperationException("A backup manifest is too large to publish.");
            await using var archiveStream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true);
            AddFile(archive, VaultHeaderFileName, Path.Combine(Path.GetDirectoryName(archivePath)!, VaultHeaderFileName));
            AddFile(archive, CatalogFileName, Path.Combine(Path.GetDirectoryName(archivePath)!, CatalogFileName));
            var manifestEntry = archive.CreateEntry("manifest.enc", CompressionLevel.NoCompression);
            await using (var target = manifestEntry.Open())
                await target.WriteAsync(encryptedManifest);
            foreach (var (hash, path) in stagedMedia)
                AddFile(archive, MediaBlobEntryName(hash), path);
            archive.Dispose();
            await archiveStream.FlushAsync();
        }
        finally { CryptographicOperations.ZeroMemory(encryptedManifest); }
    }

    private static void AddFile(ZipArchive archive, string entryName, string filePath)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var source = File.OpenRead(filePath);
        using var destination = entry.Open();
        source.CopyTo(destination);
    }

    // The caller owns and clears the returned key after it finishes publishing the staged vault.
    internal static async Task<(IGalleryStore Store, byte[] Key)> StageRestoreAsync(
        IGalleryStoreFactory storeFactory, IBackupTarget target, string latestBackupName, string staging,
        string destination, string credential, bool useRecovery)
    {
        byte[]? key = null;
        try
        {
            var archivePaths = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = latestBackupName;
            Manifest? latestManifest = null;
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
                var manifest = ArchiveManifestProcessor.Read<Manifest>(ReadArchiveManifest(archive), key);
                if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.BackupId) || manifest.Media is null)
                    throw new InvalidOperationException("Unsupported or corrupt backup manifest.");
                if (latestManifest is null)
                {
                    latestManifest = manifest;
                    await ExtractRequiredEntryAsync(archive, CatalogFileName, Path.Combine(staging, CatalogFileName), archiveLength);
                    if (manifest.VaultHeaderSha256 != await FileSha256.ComputeAsync(Path.Combine(staging, VaultHeaderFileName))
                        || manifest.DatabaseFingerprint != await ReadSnapshotFingerprintAsync(
                            storeFactory.Create(staging), Path.Combine(staging, CatalogFileName), key))
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

    private static async Task RestoreMediaAsync(IGalleryStore store, Manifest latestManifest,
        IReadOnlyList<string> archivePaths, string staging, string destination, byte[] key)
    {
        var records = await store.ReadRecordsAsync(key);
        if (records.Count != latestManifest.Media.Count)
            throw new CryptographicException("The backup catalog and manifest disagree.");
        var archiveIndexByBlobName = IndexArchiveMediaEntries(archivePaths);
        var restoresByArchiveIndex = new SortedDictionary<int, List<(ImageRecord Record, MediaEntry Entry, string Relative)>>();
        foreach (var record in records)
        {
            var relative = StoredMediaProcessor.RelativePath(record);
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
                await ExtractRequiredEntryAsync(archive, MediaBlobEntryName(entry.Sha256), mediaPath, archiveLength);
                if (await FileSha256.ComputeAsync(mediaPath) != entry.Sha256)
                    throw new CryptographicException("Encrypted backup media failed its integrity check.");
                var plaintext = await StoredMediaProcessor.ReadAsync(mediaPath, record.Id, key);
                CryptographicOperations.ZeroMemory(plaintext);
                await store.UpdateAsync(record with { StoredPath = Path.Combine(destination, relative) }, key);
            }
        }
    }

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
