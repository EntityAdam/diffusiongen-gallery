using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Storage.Blobs;
using Gallery.Models;
using Gallery.Services.Processors;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    private const string BackupSettingsId = "backup-settings";
    private const string BackupSettingsPurpose = "backup-settings:v1";
    private const string BackupManifestPurpose = "backup-manifest:v1";

    private sealed record StoredBackupSettings(
        string DestinationType,
        string LocalDirectory,
        string AzureContainer,
        string AzurePrefix,
        string AzureConnectionString,
        string? LastBackupName,
        string? LastBackupType);

    private sealed record BackupMediaEntry(string StoredPath, string Sha256, string Extension);
    private sealed record BackupManifest(
        int FormatVersion,
        string BackupId,
        string BackupType,
        string? ParentBackupName,
        DateTimeOffset CreatedAt,
        Dictionary<string, BackupMediaEntry> Media,
        string? DatabaseFingerprint = null,
        string? VaultHeaderSha256 = null);

    private interface IBackupTarget
    {
        Task<bool> ExistsAsync(string name);
        Task DownloadAsync(string name, string destinationPath);
        Task UploadAsync(string name, string sourcePath);
    }

    private sealed class LocalBackupTarget(string directory) : IBackupTarget
    {
        private string PathFor(string name) => Path.Combine(directory, name);

        public Task<bool> ExistsAsync(string name) => Task.FromResult(File.Exists(PathFor(name)));

        public Task DownloadAsync(string name, string destinationPath)
        {
            File.Copy(PathFor(name), destinationPath, overwrite: false);
            return Task.CompletedTask;
        }

        public Task UploadAsync(string name, string sourcePath)
        {
            Directory.CreateDirectory(directory);
            var temporaryPath = PathFor(name + ".partial-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.Copy(sourcePath, temporaryPath, overwrite: false);
                File.Move(temporaryPath, PathFor(name), overwrite: false);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class AzureBackupTarget(BlobContainerClient container, string prefix) : IBackupTarget
    {
        private string BlobName(string name) => prefix.Length == 0 ? name : $"{prefix}/{name}";
        private BlobClient Blob(string name) => container.GetBlobClient(BlobName(name));

        public async Task<bool> ExistsAsync(string name) =>
            (await Blob(name).ExistsAsync()).Value;

        public async Task DownloadAsync(string name, string destinationPath) =>
            await Blob(name).DownloadToAsync(destinationPath);

        public async Task UploadAsync(string name, string sourcePath) =>
            await Blob(name).UploadAsync(sourcePath, overwrite: false);
    }

    public async Task<BackupOptions> LoadBackupOptionsAsync()
    {
        var settings = await ReadBackupSettingsAsync();
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
        azurePrefix = NormalizeAzurePrefix(azurePrefix);
        var current = await ReadBackupSettingsAsync();
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
            _ = BlobServiceClientFactory.Create(connectionString);
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
        await WriteEncryptedSettingAsync(updated, BackupSettingsId, BackupSettingsPurpose);
        return new(updated.DestinationType, updated.LocalDirectory, updated.AzureContainer,
            updated.AzurePrefix, updated.AzureConnectionString.Length > 0,
            updated.LastBackupName, updated.LastBackupType);
    }

    public Task<BackupRunResult> CreateFullBackupAsync() => CreateBackupAsync(incremental: false);

    public Task<BackupRunResult> CreateIncrementalBackupAsync() => CreateBackupAsync(incremental: true);

    private async Task<BackupRunResult> CreateBackupAsync(bool incremental)
    {
        var settings = await ReadBackupSettingsAsync()
            ?? throw new InvalidOperationException("Configure a backup destination before starting a backup.");
        var target = CreateBackupTarget(settings);
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
        await galleryStore.BackupGate.WaitAsync();
        try
        {
            using var key = vaultSession.AcquireUnlockedSession().Borrow();
            BackupManifest? previousManifest = null;
            if (previousName is not null)
            {
                previousManifest = await ValidateBackupChainAsync(target, previousName, stagingDirectory, key.Bytes);
            }

            await ListAsync();
            var mediaEntries = new Dictionary<string, BackupMediaEntry>(StringComparer.Ordinal);
            var stagedMedia = new Dictionary<string, string>(StringComparer.Ordinal);
            string DatabaseFingerprint;
            string vaultHeaderSha256;
            await galleryStore.Gate.WaitAsync();
            try
            {
                var records = await galleryStore.ReadRecordsAsync(key.Bytes);
                var vaultHeaderPath = Path.Combine(stagingDirectory, "vault.json");
                var databasePath = Path.Combine(stagingDirectory, "gallery.db");
                await vaultStore.CopyHeaderAsync(vaultHeaderPath);
                await galleryStore.CreateSnapshotAsync(databasePath);
                vaultHeaderSha256 = await HashFileAsync(vaultHeaderPath);
                DatabaseFingerprint = await Gallery.Services.DataManagers.GalleryStore.ComputeContentFingerprintAsync(databasePath, BackupSettingsId);

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
                        sha256 = Convert.ToHexString(await SHA256.HashDataAsync(source)).ToLowerInvariant();
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
            }
            finally { galleryStore.Gate.Release(); }

            if (incremental && previousManifest is not null
                && IsUnchangedSince(previousManifest, mediaEntries, DatabaseFingerprint, vaultHeaderSha256))
                return new(backupType, previousName!, 0, mediaEntries.Count, 0, createdAt, Skipped: true);

            var manifest = new BackupManifest(1, backupId, backupType, previousName, createdAt, mediaEntries,
                DatabaseFingerprint, vaultHeaderSha256);
            await WriteArchiveAsync(archivePath, manifest, stagedMedia, key.Bytes);
            await File.WriteAllBytesAsync(manifestPath, ReadArchiveManifest(archivePath));
            var archiveBytes = new FileInfo(archivePath).Length;
            await target.UploadAsync(archiveName, archivePath);
            await target.UploadAsync(ManifestName(archiveName), manifestPath);

            var latestSettings = await ReadBackupSettingsAsync();
            if (latestSettings is null || !SameDestination(settings, latestSettings))
                throw new InvalidOperationException("The backup completed, but the destination settings changed before its status could be saved. Verify the archive and run a new full backup if needed.");
            await WriteEncryptedSettingAsync(settings with
            {
                LastBackupName = archiveName,
                LastBackupType = backupType
            }, BackupSettingsId, BackupSettingsPurpose);
            return new(backupType, archiveName, stagedMedia.Count, mediaEntries.Count, archiveBytes, createdAt);
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            finally { galleryStore.BackupGate.Release(); }
        }
    }

    private async Task<StoredBackupSettings?> ReadBackupSettingsAsync() =>
        await ReadEncryptedSettingAsync<StoredBackupSettings>(BackupSettingsId, BackupSettingsPurpose);

    private static bool SameDestination(StoredBackupSettings left, StoredBackupSettings right) =>
        left.DestinationType == right.DestinationType
        && left.LocalDirectory == right.LocalDirectory
        && left.AzureContainer == right.AzureContainer
        && left.AzurePrefix == right.AzurePrefix
        && left.AzureConnectionString == right.AzureConnectionString;

    private IBackupTarget CreateBackupTarget(StoredBackupSettings settings)
    {
        if (settings.DestinationType == "local")
        {
            var directory = Path.GetFullPath(settings.LocalDirectory);
            var vaultRoot = Path.GetFullPath(vaultStore.Root);
            if (IsPathWithin(directory, vaultRoot))
                throw new InvalidOperationException("The backup folder cannot be inside the gallery vault.");
            return new LocalBackupTarget(directory);
        }
        if (settings.DestinationType == "azure")
        {
            var serviceClient = BlobServiceClientFactory.Create(settings.AzureConnectionString);
            var container = serviceClient.GetBlobContainerClient(settings.AzureContainer);
            return new AzureBackupTarget(container, settings.AzurePrefix);
        }
        throw new InvalidOperationException("The saved backup destination is not supported.");
    }

    private static bool IsPathWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string NormalizeAzurePrefix(string prefix)
    {
        prefix = prefix.Trim().Trim('/');
        if (prefix.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new InvalidOperationException("Azure backup prefixes cannot contain '.' or '..' path segments.");
        return prefix;
    }

    private static async Task<BackupManifest> ValidateBackupChainAsync(
        IBackupTarget target, string latestName, string stagingDirectory, byte[] key)
    {
        var archiveName = latestName;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        BackupManifest? latestManifest = null;
        var chainIndex = 0;
        while (true)
        {
            if (!visited.Add(archiveName))
                throw new InvalidOperationException("The incremental backup chain contains a cycle. Create a new full backup.");
            if (!await target.ExistsAsync(archiveName) || !await target.ExistsAsync(ManifestName(archiveName)))
                throw new InvalidOperationException($"Backup '{archiveName}' or its manifest is missing. Create a new full backup.");
            var manifestPath = Path.Combine(stagingDirectory, $"previous-{chainIndex++}.manifest");
            await target.DownloadAsync(ManifestName(archiveName), manifestPath);
            var manifest = await ReadManifestFileAsync(manifestPath, key);
            if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.BackupId)
                || manifest.Media is null)
                throw new InvalidOperationException("A backup manifest is invalid or unsupported. Create a new full backup.");
            if (latestManifest is null) latestManifest = manifest;
            if (manifest.BackupType == "full")
            {
                if (manifest.ParentBackupName is not null)
                    throw new InvalidOperationException("The full backup has an invalid parent link. Create a new full backup.");
                return latestManifest;
            }
            if (manifest.BackupType != "incremental" || string.IsNullOrWhiteSpace(manifest.ParentBackupName))
                throw new InvalidOperationException("The incremental backup chain does not lead to a valid full backup.");
            archiveName = manifest.ParentBackupName;
        }
    }

    private static string ManifestName(string archiveName) => archiveName + ".manifest";

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    // Manifests written before hashes were recorded have null hashes and are always treated as changed.
    private static bool IsUnchangedSince(
        BackupManifest previous, IReadOnlyDictionary<string, BackupMediaEntry> media,
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

    private static byte[] ReadArchiveManifest(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("manifest.enc")
            ?? throw new InvalidOperationException("The completed backup archive is missing its manifest.");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static async Task<BackupManifest> ReadManifestFileAsync(string manifestPath, byte[] key)
    {
        var encrypted = await File.ReadAllBytesAsync(manifestPath);
        var plaintext = VaultCrypto.Decrypt(encrypted, key, BackupManifestPurpose);
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(plaintext)
                ?? throw new InvalidOperationException("The previous backup manifest is invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static async Task WriteArchiveAsync(
        string archivePath, BackupManifest manifest, IReadOnlyDictionary<string, string> stagedMedia, byte[] key)
    {
        var manifestJson = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var encryptedManifest = VaultCrypto.Encrypt(manifestJson, key, BackupManifestPurpose);
        CryptographicOperations.ZeroMemory(manifestJson);
        try
        {
            await using var archiveStream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true);
            AddFile(archive, "vault.json", Path.Combine(Path.GetDirectoryName(archivePath)!, "vault.json"));
            AddFile(archive, "gallery.db", Path.Combine(Path.GetDirectoryName(archivePath)!, "gallery.db"));
            var manifestEntry = archive.CreateEntry("manifest.enc", CompressionLevel.NoCompression);
            await using (var target = manifestEntry.Open())
                await target.WriteAsync(encryptedManifest);
            foreach (var (hash, path) in stagedMedia)
                AddFile(archive, $"media/{hash}.blob", path);
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

    private static class BlobServiceClientFactory
    {
        public static BlobServiceClient Create(string connectionString)
        {
            try { return new BlobServiceClient(connectionString); }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("The Azure Storage connection string is invalid.", exception);
            }
        }
    }
}
