using Gallery.Core.Features.Media.Processors;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Features.Diagnostics.Models;
using Gallery.Core.Features.Files.Processors;

namespace Gallery.Core.Features.Storage.DriveOperations;

/// <summary>
/// GalleryScope owns this queue and disposes it to prevent new playback and request cancellation.
/// The player serializes lifecycle and status transitions; retry delay reads and writes are atomic.
/// Changed callbacks may overlap on background or caller threads. Queued work is durable across
/// pause, cancellation, lock and restart.
/// </summary>
internal sealed class GalleryDriveQueue : IDisposable
{
    internal const string RemovalStagingMarker = ".removing-";
    private const int DriveBatchSize = 64;
    private const int MaxDriveAttempts = 5;

    private readonly GalleryLifetime galleryLifetime;
    private readonly VaultSession vaultSession;
    private readonly DriveOperationPlayer driveOperationPlayer;
    private long retryDelayTicks = TimeSpan.FromSeconds(2).Ticks;

    public GalleryDriveQueue(GalleryLifetime galleryLifetime, VaultSession vaultSession)
    {
        this.galleryLifetime = galleryLifetime;
        this.vaultSession = vaultSession;
        driveOperationPlayer = new DriveOperationPlayer(OpenDriveSession, ReadDriveQueueCountsAsync);
        driveOperationPlayer.Changed += () => Changed?.Invoke();
    }

    /// <summary>Base delay before retrying a failed drive operation; doubles with each attempt.</summary>
    internal TimeSpan DriveRetryDelay
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref retryDelayTicks));
        set => Volatile.Write(ref retryDelayTicks, value.Ticks);
    }

    /// <summary>Raised when queued drive work progresses or its status is refreshed.</summary>
    public event Action? Changed;

    public DriveQueueStatus DriveQueueStatus => driveOperationPlayer.Status;

    public void Start() => driveOperationPlayer.Start();

    public Task StartAfterObservingPreviousFailureAsync() =>
        driveOperationPlayer.StartAfterObservingPreviousFailureAsync();

    internal DriveFailureSnapshot? CaptureFailure() => driveOperationPlayer.CaptureFailure();

    internal Task StartAfterCommitAsync(DriveFailureSnapshot? previousFailure) =>
        driveOperationPlayer.StartAfterCommitAsync(previousFailure);

    public Task StopAsync() => driveOperationPlayer.StopAsync();

    public void Dispose() => driveOperationPlayer.Dispose();

    public Task PauseDriveOperationsAsync() => driveOperationPlayer.PauseAsync();

    public void ResumeDriveOperations() => driveOperationPlayer.Resume();

    /// <summary>Discards queued and failed drive work. Files those operations would have changed stay as they are.</summary>
    public async Task CancelDriveOperationsAsync()
    {
        await galleryLifetime.Gate.WaitAsync();
        try
        {
            using var keyLease = BorrowUnlockedKeyBeforeCheckout();
            galleryLifetime.EnsureCheckout();
            var galleryStore = galleryLifetime.Current.GalleryStore;
            await driveOperationPlayer.RunWhileStartAdmissionBlockedAsync(
                () => galleryStore.CancelUnfinishedDriveOperationsAsync(keyLease.Bytes),
                driveOperationPlayer.RefreshAsync);
        }
        finally { galleryLifetime.Gate.Release(); }
    }

    public async Task RetryFailedDriveOperationsAsync()
    {
        await galleryLifetime.Gate.WaitAsync();
        try
        {
            using var keyLease = BorrowUnlockedKeyBeforeCheckout();
            galleryLifetime.EnsureCheckout();
            var galleryStore = galleryLifetime.Current.GalleryStore;
            await galleryStore.RetryFailedDriveOperationsAsync(keyLease.Bytes);
            driveOperationPlayer.Start();
            await driveOperationPlayer.RefreshAsync();
        }
        finally { galleryLifetime.Gate.Release(); }
    }

    public Task WaitForDriveOperationsAsync() => driveOperationPlayer.WhenIdleAsync();

    public async Task RefreshDriveQueueStatusAsync()
    {
        await galleryLifetime.Gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(galleryLifetime.IsDisposed, this);
            await driveOperationPlayer.RefreshAsync();
        }
        finally { galleryLifetime.Gate.Release(); }
    }

    private async Task<DriveQueueCounts> ReadDriveQueueCountsAsync()
    {
        KeyLease? keyLease;
        try { keyLease = vaultSession.BorrowUnlockedKey(LeaseActivityPolicy.PreserveInactivityDeadline); }
        catch (VaultLockedException) { keyLease = null; }
        using (keyLease)
        {
            var location = galleryLifetime.Current;
            return location.VaultStore.Exists && keyLease is not null
                ? await location.GalleryStore.ReadDriveQueueCountsAsync(keyLease.Bytes)
                : new DriveQueueCounts(0, 0, null);
        }
    }

    private IDriveOperationSession? OpenDriveSession()
    {
        KeyLease keyLease;
        try { keyLease = vaultSession.BorrowUnlockedKey(LeaseActivityPolicy.RefreshInactivityDeadline); }
        catch (VaultLockedException) { return null; }
        var location = galleryLifetime.Current;
        return new DriveOperationSession(location.GalleryStore, Path.Combine(location.VaultStore.Root, "images"), keyLease, DriveRetryDelay);
    }

    private KeyLease BorrowUnlockedKeyBeforeCheckout()
    {
        try { return vaultSession.AcquireUnlockedSession().Borrow(); }
        catch (VaultLockedException)
        {
            if (galleryLifetime.Current.Checkout is null)
            {
                using var checkoutProbe = VaultCheckout.Acquire(galleryLifetime.Current.VaultStore.Root);
            }
            throw;
        }
    }

    private static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
        IGalleryStore galleryStore, string imageRoot, KeyLease keyLease, TimeSpan retryDelay)
        : IDriveOperationSession
    {
        private const string UnreadableFailureCategory = "unreadable";

        private byte[] Key => keyLease.Bytes;

        public async Task<DriveBatchReport> PlayBatchAsync(CancellationToken cancellationToken)
        {
            var dueDriveOperations = await galleryStore.ReadDueDriveOperationsAsync(DriveBatchSize, NowMilliseconds(), Key);
            var driveOperationResults = new List<DriveOperationResult>(dueDriveOperations.Count);
            var activity = await PlayDueOperationsAsync(dueDriveOperations, driveOperationResults, cancellationToken);
            await galleryStore.CompleteDriveOperationsAsync(driveOperationResults, Key);
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
                driveOperationResults.Add(await ApplyDriveOperationAndClassifyOutcomeAsync(driveOperation));
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

        private async Task<DriveOperationResult> ApplyDriveOperationAndClassifyOutcomeAsync(DriveOperation driveOperation)
        {
            DriveOperationPayload driveOperationPayload;
            try { driveOperationPayload = ReadPayload(driveOperation); }
            catch (Exception exception) when (exception is CryptographicException or JsonException)
            { return Failed(driveOperation, UnreadableFailureCategory); }

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
                return Failed(driveOperation, UnreadableFailureCategory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var category = exception is UnauthorizedAccessException ? "access" : "io";
                return RetryTransientFailure(driveOperation, category, retryDelay);
            }
        }

        private static DriveOperationResult RetryTransientFailure(
            DriveOperation driveOperation, string category, TimeSpan baseRetryDelay)
        {
            var attempts = driveOperation.Attempts + 1;
            if (attempts >= MaxDriveAttempts) return Failed(driveOperation, category, attempts);
            var delay = baseRetryDelay * Math.Pow(2, driveOperation.Attempts);
            return new(driveOperation.Sequence, driveOperation.Revision, DriveOperationOutcome.Retry, attempts,
                NowMilliseconds() + (long)delay.TotalMilliseconds, category);
        }

        private DriveOperationPayload ReadPayload(DriveOperation driveOperation)
        {
            VaultEnvelopeValidator.Validate(driveOperation.Payload.AsSpan(),
                $"drive operation payload '{driveOperation.Sequence}'");
            var plain = VaultCrypto.Decrypt(driveOperation.Payload.AsSpan(), Key,
                DriveOperationProcessor.PayloadPurpose(driveOperation.Kind, driveOperation.Target.AsSpan()));
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
            try
            {
                if (await QueuedFileDeletion.IsUnownedAsync(galleryStore, driveOperation.ImageId, path, Key))
                    await QueuedFileDeletion.DeleteUnownedFileAsync(galleryStore, driveOperation, path, Key);
            }
            finally { galleryStore.Gate.Release(); }
        }

        // Lists one directory. A failure retries only this directory instead of aborting the whole tree walk.
        private async Task ScanDirectoryAsync(DriveOperationPayload driveOperationPayload)
        {
            var directoryEntries = EnumerateDirectoryEntries(driveOperationPayload.Path);
            var indexedFiles = CreateDirectoryIndex(directoryEntries);
            var discoveredDriveOperations = CreateRecoveryDriveOperations(directoryEntries);
            if (driveOperationPayload.Recursive)
                discoveredDriveOperations.AddRange(CreateSubdirectoryScanOperations(directoryEntries));
            await galleryStore.ReplaceDirectoryIndexAsync(
                VaultCrypto.PathFingerprint(Key, driveOperationPayload.Path), indexedFiles, discoveredDriveOperations, Key);
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
                .Where(file => file.Extension is StoredMediaProcessor.EncryptedPngExtension or StoredMediaProcessor.EncryptedMp4Extension)
                .Select(file => new IndexedFile(VaultCrypto.PathFingerprint(Key, file.FullName), file.Length))
                .ToList();

        private List<NewDriveOperation> CreateRecoveryDriveOperations(IEnumerable<FileSystemInfo> directoryEntries) =>
            directoryEntries.OfType<FileInfo>().Where(file => IsRemovalStagingFile(file.FullName))
                .Select(file => DriveOperationProcessor.Create(DriveOperationKind.RecoverStaged, file.FullName, null, Key))
                .ToList();

        private List<NewDriveOperation> CreateSubdirectoryScanOperations(IEnumerable<FileSystemInfo> directoryEntries) =>
            directoryEntries.OfType<DirectoryInfo>()
                .Select(directory => DriveOperationProcessor.Create(DriveOperationKind.ScanDirectory,
                    directory.FullName, null, Key, operationIsRecursive: true))
                .ToList();

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
                    if (StorageFileSystem.IsPathWithin(Path.GetDirectoryName(stagedFilePath)!, imageRoot))
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
                    LogEventGallery.DriveOperationConflict,
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
                && StorageFileSystem.PathEquals(imageRecord.StoredPath, originalPath))
                return imageRecord;
            // Legacy staging names carry a random identity; fall back to the catalog's stored paths.
            return (await galleryStore.ReadRecordsAsync(Key))
                .FirstOrDefault(imageRecord => StorageFileSystem.PathEquals(imageRecord.StoredPath, originalPath));
        }
    }
}
