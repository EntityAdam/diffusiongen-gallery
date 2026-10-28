using Gallery.Core.Services.Processors.Validators;
using Gallery.Core.Services.Processors.Codecs;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Sessions.Generations.Leases;

namespace Gallery.Core.Services;

// Drive work (file deletes, directory index scans and interrupted-removal recovery) is recorded in the
// catalog database and played in the background, so the gallery never walks its whole media tree on the
// request path and queued work survives pause, cancellation of the player, lock and restart.
public sealed partial class GalleryService
{
    private const string MediaIndexId = "media-index";
    private const string MediaIndexPurpose = "media-index:v1";
    private const string RemovalStagingMarker = ".removing-";
    private const int DriveBatchSize = 64;
    private const int MaxDriveAttempts = 5;

    private readonly DriveOperationPlayer driveOperationPlayer;

    /// <summary>Base delay before retrying a failed drive operation; doubles with each attempt.</summary>
    internal TimeSpan DriveRetryDelay
    {
        get => galleryLifetime.DriveRetryDelay;
        set => galleryLifetime.DriveRetryDelay = value;
    }

    /// <summary>Raised from a background thread whenever queued drive work progresses.</summary>
    public event Action? DriveQueueChanged;

    public DriveQueueStatus DriveQueueStatus => driveOperationPlayer.Status;

    private sealed record MediaIndexMarker(DateTimeOffset ScheduledAt);

    private DriveOperationPlayer CreateDriveOperationPlayer()
    {
        var driveOperationPlayer = new DriveOperationPlayer(OpenDriveSession, ReadDriveQueueCountsAsync);
        driveOperationPlayer.Changed += () => DriveQueueChanged?.Invoke();
        return driveOperationPlayer;
    }

    public Task PauseDriveOperationsAsync() => driveOperationPlayer.PauseAsync();

    public void ResumeDriveOperations() => driveOperationPlayer.Resume();

    /// <summary>Discards queued and failed drive work. Files those operations would have changed stay as they are.</summary>
    public async Task CancelDriveOperationsAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            EnsureCheckout();
            var galleryStore = this.galleryStore;
            await driveOperationPlayer.StopAsync();
            await galleryStore.CancelUnfinishedDriveOperationsAsync();
            await driveOperationPlayer.RefreshAsync();
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task RetryFailedDriveOperationsAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            EnsureCheckout();
            var galleryStore = this.galleryStore;
            await galleryStore.RetryFailedDriveOperationsAsync();
            driveOperationPlayer.Start();
            await driveOperationPlayer.RefreshAsync();
        }
        finally { lifecycleGate.Release(); }
    }

    public Task WaitForDriveOperationsAsync() => driveOperationPlayer.WhenIdleAsync();

    public async Task RefreshDriveQueueStatusAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            EnsureActive();
            await driveOperationPlayer.RefreshAsync();
        }
        finally { lifecycleGate.Release(); }
    }

    private Task<DriveQueueCounts> ReadDriveQueueCountsAsync()
    {
        var location = galleryLifetime.Current;
        return location.VaultStore.Exists
            ? location.GalleryStore.ReadDriveQueueCountsAsync()
            : Task.FromResult(new DriveQueueCounts(0, 0, null));
    }

    /// <summary>Discards the cached file index and schedules a fresh background scan of the media folders.</summary>
    public async Task RebuildStorageIndexAsync()
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var records = await ListWithKeyAsync(keyLease.Bytes);
        await galleryStore.ClearMediaIndexAsync();
        await galleryStore.DeleteSettingsAsync(MediaIndexId);
        await ScheduleMediaIndexAsync(records, keyLease.Bytes);
        driveOperationPlayer.Start();
    }

    private Task StopDriveOperationPlayerAsync() => driveOperationPlayer.StopAsync();

    private IDriveOperationSession? OpenDriveSession()
    {
        var keyLease = vaultSession.TryBorrowUnlockedKey();
        return keyLease is null ? null
            : new DriveOperationSession(galleryStore, ImageRoot(vaultStore.Root), keyLease, DriveRetryDelay);
    }

    private static string ImageRoot(string vaultRoot) => Path.Combine(vaultRoot, "images");

    private static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Schedules the one-time scan that seeds the media index; later loads read the index instead of the drive.</summary>
    private async Task ScheduleMediaIndexAsync(IReadOnlyCollection<ImageRecord> records, byte[] key)
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
        => DriveOperationProcessor.Create(kind, path, imageId, key, recursive);

    private static string DriveOperationPurpose(string kind, ReadOnlySpan<byte> target) =>
        $"drive-operation:{kind}:{Convert.ToHexString(target)}";

    private static bool IsWithinImageRoot(string directory, string imageRoot)
    {
        var relative = Path.GetRelativePath(imageRoot, directory);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    /// <summary>
    /// Deletes a file queued for deletion unless its image was moved back to that path. Caller holds Gate.
    /// An operation whose path is owned again is obsolete and completes without deleting that path.
    /// </summary>
    private static async Task DeleteQueuedFileAsync(
        GalleryStore galleryStore, DriveOperation driveOperation, string path, byte[] key)
    {
        if (!await CanDeleteQueuedFileAsync(galleryStore, driveOperation.ImageId, path, key)) return;
        try { await DeleteObsoleteCiphertextAsync(galleryStore, driveOperation, path, key); }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
        {
            throw new DriveOperationConflictException("Queued file is not the expected ciphertext.", exception);
        }
    }

    private static async Task<bool> CanDeleteQueuedFileAsync(
        GalleryStore galleryStore, string? imageId, string path, byte[] key) =>
        imageId is null || await galleryStore.TryReadRecordAsync(imageId, key) is not { } owner
        || !PathEquals(owner.StoredPath, path);

    private static bool IsRemovalStagingFile(string path)
    {
        var markerIndex = path.LastIndexOf(RemovalStagingMarker, StringComparison.Ordinal);
        return markerIndex >= 0 && Guid.TryParseExact(path[(markerIndex + RemovalStagingMarker.Length)..], "N", out _);
    }

    private static string RemovalOriginalPath(string path) =>
        path[..path.LastIndexOf(RemovalStagingMarker, StringComparison.Ordinal)];

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
    private sealed class DriveOperationSession(
        GalleryStore galleryStore, string imageRoot, KeyLease keyLease, TimeSpan retryDelay)
        : IDriveOperationSession
    {
        private byte[] Key => keyLease.Bytes;

        public async Task<DriveBatchReport> PlayBatchAsync(CancellationToken cancellationToken)
        {
            var dueDriveOperations = await galleryStore.ReadDueDriveOperationsAsync(DriveBatchSize, NowMilliseconds());
            var driveOperationResults = new List<DriveOperationResult>(dueDriveOperations.Count);
            var activity = await PlayDueOperationsAsync(dueDriveOperations, driveOperationResults, cancellationToken);
            await galleryStore.CompleteDriveOperationsAsync(driveOperationResults);
            return new(driveOperationResults.Count,
                driveOperationResults.Count(driveOperationResult => driveOperationResult.Outcome == DriveOperationOutcome.Done),
                activity);
        }

        private async Task<string> PlayDueOperationsAsync(
            IReadOnlyList<DriveOperation> dueDriveOperations,
            List<DriveOperationResult> driveOperationResults,
            CancellationToken cancellationToken)
        {
            var activity = "";
            foreach (var driveOperation in dueDriveOperations)
            {
                if (cancellationToken.IsCancellationRequested) break;
                driveOperationResults.Add(await ExecuteAndRecordAsync(driveOperation));
                activity = Describe(driveOperation.Kind);
            }
            return activity;
        }

        public void Dispose() => keyLease.Dispose();

        private static string Describe(string kind) => kind switch
        {
            DriveOperationKind.DeleteFile => "Deleting removed files",
            DriveOperationKind.ScanDirectory => "Indexing media folders",
            DriveOperationKind.RecoverStaged => "Recovering interrupted removals",
            _ => "Processing"
        };

        private async Task<DriveOperationResult> ExecuteAndRecordAsync(DriveOperation driveOperation)
        {
            DriveOperationPayload driveOperationPayload;
            try { driveOperationPayload = ReadPayload(driveOperation); }
            catch (Exception exception) when (exception is CryptographicException or JsonException)
            { return Failed(driveOperation, "unreadable"); }

            try
            {
                await PlayAsync(driveOperation, driveOperationPayload);
                return new(driveOperation.Sequence, driveOperation.Revision, DriveOperationOutcome.Done);
            }
            catch (DriveOperationConflictException)
            {
                return Failed(driveOperation, "conflict");
            }
            catch (JsonException)
            {
                return Failed(driveOperation, "unreadable");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var category = exception is UnauthorizedAccessException ? "access" : "io";
                var attempts = driveOperation.Attempts + 1;
                if (attempts >= MaxDriveAttempts) return Failed(driveOperation, category, attempts);
                var delay = retryDelay * Math.Pow(2, driveOperation.Attempts);
                return new(driveOperation.Sequence, driveOperation.Revision, DriveOperationOutcome.Retry, attempts,
                    NowMilliseconds() + (long)delay.TotalMilliseconds, category);
            }
        }

        private DriveOperationPayload ReadPayload(DriveOperation driveOperation)
        {
            VaultEnvelopeValidator.Validate(driveOperation.Payload.AsSpan(),
                $"drive operation payload '{driveOperation.Sequence}'");
            var plain = VaultCrypto.Decrypt(driveOperation.Payload.AsSpan(), Key,
                DriveOperationPurpose(driveOperation.Kind, driveOperation.Target.AsSpan()));
            try
            {
                return JsonSerializer.Deserialize<DriveOperationPayload>(plain)
                    ?? throw new JsonException("Empty drive operation payload.");
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        private async Task PlayAsync(DriveOperation driveOperation, DriveOperationPayload driveOperationPayload)
        {
            switch (driveOperation.Kind)
            {
                case DriveOperationKind.DeleteFile:
                    await DeleteFileAsync(driveOperation, driveOperationPayload.Path);
                    break;
                case DriveOperationKind.ScanDirectory: await ScanDirectoryAsync(driveOperationPayload); break;
                case DriveOperationKind.RecoverStaged: await RecoverStagedAsync(driveOperationPayload.Path); break;
                default: throw new JsonException($"Unsupported drive operation kind '{driveOperation.Kind}'.");
            }
        }

        // Error text is a category only; paths and names never reach the queue table.
        private static DriveOperationResult Failed(
            DriveOperation driveOperation, string category, int? attempts = null) =>
            new(driveOperation.Sequence, driveOperation.Revision, DriveOperationOutcome.Failed,
                attempts ?? driveOperation.Attempts, 0, category);

        private async Task DeleteFileAsync(DriveOperation driveOperation, string path)
        {
            await galleryStore.Gate.WaitAsync();
            try { await DeleteQueuedFileAsync(galleryStore, driveOperation, path, Key); }
            finally { galleryStore.Gate.Release(); }
        }

        // Lists one directory. A failure retries only this directory instead of aborting the whole tree walk.
        private async Task ScanDirectoryAsync(DriveOperationPayload driveOperationPayload)
        {
            var directoryEntries = EnumerateDirectoryEntries(driveOperationPayload.Path);
            var indexedFiles = CreateDirectoryIndex(directoryEntries);
            var discoveredDriveOperations = CreateDiscoveredDriveOperations(directoryEntries, driveOperationPayload.Recursive);
            await galleryStore.ReplaceDirectoryIndexAsync(
                VaultCrypto.PathFingerprint(Key, driveOperationPayload.Path), indexedFiles, discoveredDriveOperations);
        }

        private static List<FileSystemInfo> EnumerateDirectoryEntries(string directoryPath)
        {
            try
            {
                var enumerationOptions = new EnumerationOptions
                {
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false
                };
                return new DirectoryInfo(directoryPath)
                    .EnumerateFileSystemInfos("*", enumerationOptions).ToList();
            }
            catch (DirectoryNotFoundException)
            {
                // A missing record directory is an expected empty scan; access failures still propagate for retry.
                return [];
            }
        }

        private List<IndexedFile> CreateDirectoryIndex(IEnumerable<FileSystemInfo> directoryEntries) =>
            directoryEntries.OfType<FileInfo>()
                .Where(file => file.Extension is EncryptedPngExtension or EncryptedMp4Extension)
                .Select(file => new IndexedFile(VaultCrypto.PathFingerprint(Key, file.FullName), file.Length))
                .ToList();

        private List<NewDriveOperation> CreateDiscoveredDriveOperations(
            IReadOnlyCollection<FileSystemInfo> directoryEntries, bool recursive)
        {
            var files = directoryEntries.OfType<FileInfo>().ToArray();
            var discoveredDriveOperations = files.Where(file => IsRemovalStagingFile(file.FullName))
                .Select(file => CreateDriveOperation(DriveOperationKind.RecoverStaged, file.FullName, null, Key))
                .ToList();
            if (recursive)
                discoveredDriveOperations.AddRange(directoryEntries.OfType<DirectoryInfo>()
                    .Select(directory => CreateDriveOperation(DriveOperationKind.ScanDirectory,
                        directory.FullName, null, Key, recursive: true)));
            return discoveredDriveOperations;
        }

        // Finishes removal recovery when ciphertext was staged before its catalog row was deleted.
        private async Task RecoverStagedAsync(string stagedFilePath)
        {
            await galleryStore.Gate.WaitAsync();
            try
            {
                if (!File.Exists(stagedFilePath)) return;
                var imageRecord = await FindOwnerAsync(stagedFilePath);
                if (imageRecord is null)
                {
                    if (IsWithinImageRoot(Path.GetDirectoryName(stagedFilePath)!, imageRoot))
                        DeleteObsoleteStagingFile(stagedFilePath);
                    return;
                }
                if (!File.Exists(imageRecord.StoredPath))
                {
                    await RestoreStagedFileAsync(stagedFilePath, imageRecord);
                    return;
                }
                if (!await FilesMatchAsync(stagedFilePath, imageRecord.StoredPath))
                    throw new DriveOperationConflictException();
                DeleteObsoleteStagingFile(stagedFilePath);
            }
            finally { galleryStore.Gate.Release(); }
        }

        private async Task RestoreStagedFileAsync(string stagedFilePath, ImageRecord imageRecord)
        {
            await EnsureAuthenticCiphertextAsync(stagedFilePath, imageRecord.Id);
            File.Move(stagedFilePath, imageRecord.StoredPath);
        }

        private static void DeleteObsoleteStagingFile(string stagedFilePath) => File.Delete(stagedFilePath);

        private async Task EnsureAuthenticCiphertextAsync(string path, string imageId)
        {
            try
            {
                var envelope = await File.ReadAllBytesAsync(path);
                VaultEnvelopeValidator.Validate(envelope, $"staged media '{imageId}'");
                var plain = VaultCrypto.Decrypt(envelope, Key, $"image:{imageId}");
                CryptographicOperations.ZeroMemory(plain);
            }
            catch (CryptographicException exception)
            {
                throw new DriveOperationConflictException("Staged media is not authentic ciphertext.",
                    GalleryLogEvent.DriveOperationConflict,
                    new ContextualData(System.Collections.Immutable.ImmutableDictionary<string, object>.Empty
                        .Add("Image.Id", imageId)), exception);
            }
        }

        private async Task<ImageRecord?> FindOwnerAsync(string stagedFilePath)
        {
            var originalPath = RemovalOriginalPath(stagedFilePath);
            var stagingIdentity = stagedFilePath[
                (stagedFilePath.LastIndexOf(RemovalStagingMarker, StringComparison.Ordinal) + RemovalStagingMarker.Length)..];
            if (await galleryStore.TryReadRecordAsync(stagingIdentity, Key) is { } imageRecord
                && PathEquals(imageRecord.StoredPath, originalPath))
                return imageRecord;
            // Legacy staging names carry a random identity; fall back to the catalog's stored paths.
            return (await galleryStore.ReadRecordsAsync(Key))
                .FirstOrDefault(imageRecord => PathEquals(imageRecord.StoredPath, originalPath));
        }
    }
}
