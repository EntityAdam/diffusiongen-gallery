using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Shared.Diagnostics;
using Gallery.Core.Shared.Files;

namespace Gallery.Core.Features.Storage.DriveOperations;

// Drive work (file deletes, directory index scans and interrupted-removal recovery) is recorded in the
// catalog database and played in the background, so the gallery never walks its whole media tree on the
// request path and queued work survives pause, cancellation of the player, lock and restart.
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

    /// <summary>Raised from a background thread whenever queued drive work progresses.</summary>
    public event Action? Changed;

    public DriveQueueStatus DriveQueueStatus => driveOperationPlayer.Status;

    public void Start() => driveOperationPlayer.Start();

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
            galleryLifetime.EnsureCheckout();
            var galleryStore = galleryLifetime.Current.GalleryStore;
            await driveOperationPlayer.StopAsync();
            using var key = vaultSession.AcquireUnlockedSession().Borrow();
            await galleryStore.CancelUnfinishedDriveOperationsAsync(key.Bytes);
            await driveOperationPlayer.RefreshAsync();
        }
        finally { galleryLifetime.Gate.Release(); }
    }

    public async Task RetryFailedDriveOperationsAsync()
    {
        await galleryLifetime.Gate.WaitAsync();
        try
        {
            galleryLifetime.EnsureCheckout();
            var galleryStore = galleryLifetime.Current.GalleryStore;
            using var key = vaultSession.AcquireUnlockedSession().Borrow();
            await galleryStore.RetryFailedDriveOperationsAsync(key.Bytes);
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
        using var key = vaultSession.TryBorrowUnlockedKey(recordActivity: false);
        var location = galleryLifetime.Current;
        return location.VaultStore.Exists && key is not null
            ? await location.GalleryStore.ReadDriveQueueCountsAsync(key.Bytes)
            : new DriveQueueCounts(0, 0, null);
    }

    private IDriveOperationSession? OpenDriveSession()
    {
        var keyLease = vaultSession.TryBorrowUnlockedKey();
        if (keyLease is null) return null;
        var location = galleryLifetime.Current;
        return new DriveOperationSession(location.GalleryStore, Path.Combine(location.VaultStore.Root, "images"), keyLease, DriveRetryDelay);
    }

    private static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal static async Task ReconcileOutstandingDeletesAsync(IGalleryStore galleryStore, byte[] key)
    {
        await galleryStore.Gate.WaitAsync();
        try
        {
            var ownedPaths = (await galleryStore.ReadRecordsAsync(key))
                .Select(record => Path.GetFullPath(record.StoredPath))
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var operation in await galleryStore.ReadOutstandingDeletesAsync(key))
            {
                var payload = DriveOperationProcessor.Read(operation, key);
                var path = Path.GetFullPath(payload.Path);
                if (!DriveOperationProcessor.PathFingerprint(key, path).AsSpan().SequenceEqual(operation.Target.AsSpan()))
                    throw new InvalidOperationException("Queued deletion path does not match its authenticated target.");
                if (!ownedPaths.Contains(path))
                    await DeleteObsoleteCiphertextAsync(galleryStore, operation, path, key);
                await galleryStore.CompleteDriveOperationsAsync(
                    [new DriveOperationResult(operation.Sequence, operation.Revision, DriveOperationOutcome.Done)], key);
            }
        }
        finally { galleryStore.Gate.Release(); }
    }

    private static async Task DeleteObsoleteCiphertextAsync(
        IGalleryStore store, DriveOperation operation, string path, byte[] key)
    {
        if (Directory.Exists(path)) throw new IOException("Queued ciphertext deletion points to a directory.");
        if (File.Exists(path))
        {
            if (operation.ImageId is null || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Queued ciphertext ownership cannot be verified.");
            await StoredMediaProcessor.VerifyCiphertextAsync(path, operation.ImageId, key);
            try { File.Delete(path); }
            catch (DirectoryNotFoundException) { }
        }
        await store.RemoveIndexedFileAsync(DriveOperationProcessor.PathFingerprint(key, path), key);
    }

    /// <summary>
    /// Deletes a file queued for deletion unless its image was moved back to that path. Caller holds Gate.
    /// An operation whose path is owned again is obsolete and completes without deleting that path.
    /// </summary>
    internal static async Task DeleteQueuedFileAsync(
        IGalleryStore galleryStore, DriveOperation driveOperation, string path, byte[] key)
    {
        if (!await CanDeleteQueuedFileAsync(galleryStore, driveOperation.ImageId, path, key)) return;
        try { await DeleteObsoleteCiphertextAsync(galleryStore, driveOperation, path, key); }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
        {
            throw new DriveOperationConflictException("Queued file is not the expected ciphertext.", exception);
        }
    }

    internal static async Task<bool> CanDeleteQueuedFileAsync(
        IGalleryStore galleryStore, string? imageId, string path, byte[] key) =>
        imageId is null || await galleryStore.TryReadRecordAsync(imageId, key) is not { } owner
        || !StorageFileSystem.PathEquals(owner.StoredPath, path);

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

        private List<NewDriveOperation> CreateDiscoveredDriveOperations(
            IReadOnlyCollection<FileSystemInfo> directoryEntries, bool recursive)
        {
            var files = directoryEntries.OfType<FileInfo>().ToArray();
            var discoveredDriveOperations = files.Where(file => IsRemovalStagingFile(file.FullName))
                .Select(file => DriveOperationProcessor.Create(DriveOperationKind.RecoverStaged, file.FullName, null, Key))
                .ToList();
            if (recursive)
                discoveredDriveOperations.AddRange(directoryEntries.OfType<DirectoryInfo>()
                    .Select(directory => DriveOperationProcessor.Create(DriveOperationKind.ScanDirectory,
                        directory.FullName, null, Key, operationIsRecursive: true)));
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
                && StorageFileSystem.PathEquals(imageRecord.StoredPath, originalPath))
                return imageRecord;
            // Legacy staging names carry a random identity; fall back to the catalog's stored paths.
            return (await galleryStore.ReadRecordsAsync(Key))
                .FirstOrDefault(imageRecord => StorageFileSystem.PathEquals(imageRecord.StoredPath, originalPath));
        }
    }
}
