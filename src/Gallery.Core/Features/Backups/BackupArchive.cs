using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence;

namespace Gallery.Core.Features.Backups;

internal static class BackupArchive
{
    internal const string ActivitySourceName = "Gallery.Core.Backup";
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    internal const string CatalogFileName = GalleryStoreFactory.CatalogFileName;
    internal const string VaultHeaderFileName = VaultStore.HeaderFileName;
    private const int MaximumEncryptedManifestBytes = 16 * 1024 * 1024;
    // The fixed-format header holds only a name, key slots and salts.
    internal const long MaximumVaultHeaderBytes = 1024 * 1024;

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

    internal static string ManifestName(string archiveName) => archiveName + ".manifest";

    internal static Task<string> ReadSnapshotFingerprintAsync(IGalleryCatalogSnapshots snapshots, string databasePath, byte[] key) =>
        snapshots.ComputeSnapshotFingerprintAsync(databasePath, StoredBackupSettings.SettingId, key);

    internal static async Task<long> VerifyArchiveEntryAsync(
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

    internal static byte[] ReadArchiveManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry("manifest.enc")
            ?? throw new InvalidOperationException("The completed backup archive is missing its manifest.");
        return ReadArchiveManifest(entry);
    }

    internal static byte[] ReadArchiveManifest(ZipArchiveEntry entry)
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

    internal static string MediaBlobEntryName(string sha256) => $"media/{sha256}.blob";

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

    internal static async Task ExtractRequiredEntryAsync(
        ZipArchive archive, string name, string destination, long maximumBytes)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Backup is missing '{name}'.");
        if (entry.Length > maximumBytes)
            throw new InvalidDataException($"Backup entry '{name}' exceeds its supported size.");
        if (File.Exists(destination)) throw new IOException($"The restore file '{destination}' already exists.");
        await VerifyArchiveEntryAsync(entry, expectedHash: null, destination);
    }
}
