using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Web.Exceptions;
using Gallery.Web.Models;
using Gallery.Web.Services.DataManagers;
using Gallery.Web.Services.DataManagers.Models;
using Gallery.Web.Services.Processors;
using Gallery.Web.Services.Sessions.Leases;

namespace Gallery.Web.Services;

// Drive work (file deletes, directory index scans and interrupted-removal recovery) is recorded in the
// catalog database and played in the background, so the gallery never walks its whole media tree on the
// request path and queued work survives pause, cancellation of the player, lock and restart.
public sealed partial class GalleryService
{
    private const string MediaIndexId = "media-index";
    private const string MediaIndexPurpose = "media-index:v1";
    private const int DriveBatchSize = 64;
    private const int MaxDriveAttempts = 5;

    private readonly DriveOperationPlayer drivePlayer;

    /// <summary>Base delay before retrying a failed drive operation; doubles with each attempt.</summary>
    internal TimeSpan DriveRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Raised from a background thread whenever queued drive work progresses.</summary>
    public event Action? DriveQueueChanged;

    public WebDriveQueueStatus DriveQueueStatus => drivePlayer.Status;

    private sealed record MediaIndexMarker(DateTimeOffset ScheduledAt);

    private sealed class DriveOperationConflictException : Exception
    {
        public DriveOperationConflictException() { }
        public DriveOperationConflictException(string message) : base(message) { }
        public DriveOperationConflictException(string message, Exception innerException) : base(message, innerException) { }
    }

    private DriveOperationPlayer CreateDrivePlayer()
    {
        var player = new DriveOperationPlayer(OpenDriveSession, () => galleryStore.ReadDriveQueueCountsAsync());
        player.Changed += () => DriveQueueChanged?.Invoke();
        return player;
    }

    public Task PauseDriveOperationsAsync() => drivePlayer.PauseAsync();

    public void ResumeDriveOperations() => drivePlayer.Resume();

    /// <summary>Discards queued and failed drive work. Files those operations would have changed stay as they are.</summary>
    public async Task CancelDriveOperationsAsync()
    {
        await drivePlayer.StopAsync();
        await galleryStore.CancelUnfinishedDriveOperationsAsync();
        await drivePlayer.RefreshAsync();
    }

    public async Task RetryFailedDriveOperationsAsync()
    {
        await galleryStore.RetryFailedDriveOperationsAsync();
        drivePlayer.Start();
        await drivePlayer.RefreshAsync();
    }

    public Task WaitForDriveOperationsAsync() => drivePlayer.WhenIdleAsync();

    public Task RefreshDriveQueueStatusAsync() => drivePlayer.RefreshAsync();

    /// <summary>Discards the cached file index and schedules a fresh background scan of the media folders.</summary>
    public async Task RebuildStorageIndexAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var records = await ListWithKeyAsync(key.Bytes);
        await galleryStore.ClearMediaIndexAsync();
        await galleryStore.DeleteSettingsAsync(MediaIndexId);
        await ScheduleMediaIndexAsync(records, key.Bytes);
        drivePlayer.Start();
    }

    private Task StopDrivePlayerAsync() => drivePlayer.StopAsync();

    private IDriveOperationSession? OpenDriveSession()
    {
        KeyLease lease;
        try { lease = vaultSession.AcquireUnlockedSession().Borrow(); }
        catch (VaultLockedException) { return null; }
        return new DriveOperationSession(galleryStore, ImageRoot(vaultStore.Root), lease, DriveRetryDelay);
    }

    private static string ImageRoot(string vaultRoot) => Path.Combine(vaultRoot, "images");

    private static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Schedules the one-time scan that seeds the media index; later loads read the index instead of the drive.</summary>
    private async Task ScheduleMediaIndexAsync(IReadOnlyCollection<WebImageRecord> records, byte[] key)
    {
        if (await galleryStore.ReadSettingsAsync<MediaIndexMarker>(MediaIndexId, MediaIndexPurpose, key) is not null) return;
        var imageRoot = ImageRoot(vaultStore.Root);
        var scans = new List<NewDriveOperation> { CreateDriveOperation(DriveOperationKind.ScanDirectory, imageRoot, null, key, recursive: true) };
        scans.AddRange(records.Select(record => Path.GetDirectoryName(record.StoredPath)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(directory => !IsWithinImageRoot(directory, imageRoot))
            .Select(directory => CreateDriveOperation(DriveOperationKind.ScanDirectory, directory, null, key)));
        await galleryStore.EnqueueDriveOperationsAsync(scans);
        await galleryStore.SaveSettingsAsync(new MediaIndexMarker(DateTimeOffset.UtcNow), MediaIndexId, MediaIndexPurpose, key);
    }

    private async Task<bool> IsMediaIndexPendingAsync(byte[] key) =>
        await galleryStore.ReadSettingsAsync<MediaIndexMarker>(MediaIndexId, MediaIndexPurpose, key) is null
        || await galleryStore.CountDriveOperationsAsync(DriveOperationKind.ScanDirectory) > 0;

    private static NewDriveOperation CreateDriveOperation(string kind, string path, string? imageId, byte[] key, bool recursive = false)
    {
        var target = VaultCrypto.PathFingerprint(key, path);
        var payload = VaultCrypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(new DriveOperationPayload(path, recursive)),
            key, DriveOperationPurpose(kind, target));
        return new(kind, target, imageId, payload);
    }

    private static string DriveOperationPurpose(string kind, byte[] target) =>
        $"drive-operation:{kind}:{Convert.ToHexString(target)}";

    private static bool IsWithinImageRoot(string directory, string imageRoot)
    {
        var relative = Path.GetRelativePath(imageRoot, directory);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    /// <summary>
    /// Deletes a file queued for deletion unless its image was moved back to that path. Caller holds Gate.
    /// Returns false when the path is owned again and the operation is obsolete.
    /// </summary>
    private static async Task<bool> DeleteQueuedFileAsync(GalleryStore store, string? imageId, string path, byte[] key)
    {
        if (imageId is not null && await store.TryReadRecordAsync(imageId, key) is { } owner && PathEquals(owner.StoredPath, path))
            return false;
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }
        await store.RemoveIndexedFileAsync(VaultCrypto.PathFingerprint(key, path));
        return true;
    }

    private static bool IsRemovalStagingFile(string path)
    {
        var markerIndex = path.LastIndexOf(".removing-", StringComparison.Ordinal);
        return markerIndex >= 0 && Guid.TryParseExact(path[(markerIndex + ".removing-".Length)..], "N", out _);
    }

    private static string RemovalOriginalPath(string path) =>
        path[..path.LastIndexOf(".removing-", StringComparison.Ordinal)];

    private static async Task<bool> FilesMatchAsync(string leftPath, string rightPath)
    {
        await using var left = File.OpenRead(leftPath);
        await using var right = File.OpenRead(rightPath);
        if (left.Length != right.Length) return false;
        var leftHash = await SHA256.HashDataAsync(left);
        var rightHash = await SHA256.HashDataAsync(right);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    /// <summary>Plays due operations for one gallery store with one key lease.</summary>
    private sealed class DriveOperationSession(GalleryStore store, string imageRoot, KeyLease lease, TimeSpan retryDelay)
        : IDriveOperationSession
    {
        private byte[] Key => lease.Bytes;

        public async Task<DriveBatchReport> PlayBatchAsync(CancellationToken cancellationToken)
        {
            var operations = await store.ReadDueDriveOperationsAsync(DriveBatchSize, NowMilliseconds());
            var results = new List<DriveOperationResult>(operations.Count);
            var activity = "";
            foreach (var operation in operations)
            {
                // Pausing takes effect between operations, never part-way through one.
                if (cancellationToken.IsCancellationRequested) break;
                results.Add(await PlayAsync(operation));
                activity = Describe(operation.Kind);
            }
            await store.CompleteDriveOperationsAsync(results);
            return new(results.Count, results.Count(result => result.Outcome == DriveOperationOutcome.Done), activity);
        }

        public void Dispose() => lease.Dispose();

        private static string Describe(string kind) => kind switch
        {
            DriveOperationKind.DeleteFile => "Deleting removed files",
            DriveOperationKind.ScanDirectory => "Indexing media folders",
            DriveOperationKind.RecoverStaged => "Recovering interrupted removals",
            _ => "Processing"
        };

        private async Task<DriveOperationResult> PlayAsync(DriveOperation operation)
        {
            DriveOperationPayload payload;
            try
            {
                var plain = VaultCrypto.Decrypt(operation.Payload, Key, DriveOperationPurpose(operation.Kind, operation.Target));
                payload = JsonSerializer.Deserialize<DriveOperationPayload>(plain)
                    ?? throw new JsonException("Empty drive operation payload.");
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException)
            {
                return Failed(operation, "unreadable");
            }

            try
            {
                switch (operation.Kind)
                {
                    case DriveOperationKind.DeleteFile: await DeleteFileAsync(operation, payload.Path); break;
                    case DriveOperationKind.ScanDirectory: await ScanDirectoryAsync(payload); break;
                    case DriveOperationKind.RecoverStaged: await RecoverStagedAsync(payload.Path); break;
                    default: return Failed(operation, "unreadable");
                }
                return new(operation.Sequence, operation.Revision, DriveOperationOutcome.Done);
            }
            catch (DriveOperationConflictException)
            {
                return Failed(operation, "conflict");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var category = exception is UnauthorizedAccessException ? "access" : "io";
                var attempts = operation.Attempts + 1;
                if (attempts >= MaxDriveAttempts) return Failed(operation, category, attempts);
                var delay = retryDelay * Math.Pow(2, operation.Attempts);
                return new(operation.Sequence, operation.Revision, DriveOperationOutcome.Retry, attempts,
                    NowMilliseconds() + (long)delay.TotalMilliseconds, category);
            }
        }

        // Error text is a category only; paths and names never reach the queue table.
        private static DriveOperationResult Failed(DriveOperation operation, string category, int? attempts = null) =>
            new(operation.Sequence, operation.Revision, DriveOperationOutcome.Failed, attempts ?? operation.Attempts, 0, category);

        private async Task DeleteFileAsync(DriveOperation operation, string path)
        {
            await store.Gate.WaitAsync();
            try { await DeleteQueuedFileAsync(store, operation.ImageId, path, Key); }
            finally { store.Gate.Release(); }
        }

        // Lists one directory. A failure retries only this directory instead of aborting the whole tree walk.
        private async Task ScanDirectoryAsync(DriveOperationPayload payload)
        {
            List<FileSystemInfo> entries;
            try
            {
                var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
                entries = new DirectoryInfo(payload.Path).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (DirectoryNotFoundException) { entries = []; }

            var files = entries.OfType<FileInfo>().ToList();
            var indexed = files.Where(file => file.Extension is EncryptedPngExtension or EncryptedMp4Extension)
                .Select(file => new IndexedFile(VaultCrypto.PathFingerprint(Key, file.FullName), file.Length))
                .ToList();
            var discovered = files.Where(file => IsRemovalStagingFile(file.FullName))
                .Select(file => CreateDriveOperation(DriveOperationKind.RecoverStaged, file.FullName, null, Key))
                .ToList();
            if (payload.Recursive)
                discovered.AddRange(entries.OfType<DirectoryInfo>()
                    .Select(directory => CreateDriveOperation(DriveOperationKind.ScanDirectory, directory.FullName, null, Key, recursive: true)));
            await store.ReplaceDirectoryIndexAsync(VaultCrypto.PathFingerprint(Key, payload.Path), indexed, discovered);
        }

        // Finishes a removal interrupted by an older version that staged ciphertext before deleting the catalog row.
        private async Task RecoverStagedAsync(string stagedPath)
        {
            await store.Gate.WaitAsync();
            try
            {
                if (!File.Exists(stagedPath)) return;
                var owner = await FindOwnerAsync(stagedPath);
                if (owner is null)
                {
                    if (IsWithinImageRoot(Path.GetDirectoryName(stagedPath)!, imageRoot)) File.Delete(stagedPath);
                    return;
                }
                if (!File.Exists(owner.StoredPath))
                {
                    // Only an authentic ciphertext for this image may take its place; anything else is preserved.
                    if (!await IsAuthenticCiphertextAsync(stagedPath, owner.Id)) throw new DriveOperationConflictException();
                    File.Move(stagedPath, owner.StoredPath);
                    return;
                }
                if (!await FilesMatchAsync(stagedPath, owner.StoredPath))
                    throw new DriveOperationConflictException();
                File.Delete(stagedPath);
            }
            finally { store.Gate.Release(); }
        }

        private async Task<bool> IsAuthenticCiphertextAsync(string path, string imageId)
        {
            try
            {
                var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(path), Key, $"image:{imageId}");
                CryptographicOperations.ZeroMemory(plain);
                return true;
            }
            catch (CryptographicException) { return false; }
        }

        private async Task<WebImageRecord?> FindOwnerAsync(string stagedPath)
        {
            var originalPath = RemovalOriginalPath(stagedPath);
            var stagingIdentity = stagedPath[(stagedPath.LastIndexOf(".removing-", StringComparison.Ordinal) + ".removing-".Length)..];
            if (await store.TryReadRecordAsync(stagingIdentity, Key) is { } byId && PathEquals(byId.StoredPath, originalPath))
                return byId;
            // Legacy staging names carry a random identity; fall back to the catalog's stored paths.
            return (await store.ReadRecordsAsync(Key)).FirstOrDefault(record => PathEquals(record.StoredPath, originalPath));
        }
    }
}
