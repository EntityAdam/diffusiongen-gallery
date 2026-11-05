using Gallery.Core.Models;
using Gallery.Core.Services.Ports.Models;

namespace Gallery.Core.Services.Ports;

/// <summary>
/// Catalog persistence and atomic catalog/drive-work boundaries. No database-provider types cross this port.
/// Callers supply their generation-bound leased key, including for operations the SQLite adapter can do while locked.
/// </summary>
internal interface IGalleryStore
{
    SemaphoreSlim Gate { get; }
    SemaphoreSlim BackupGate { get; }
    // The caller must keep its vault-key lease alive until this operation is disposed.
    // Adapters may reuse resources across its constituent catalog calls.
    ValueTask<IAsyncDisposable> OpenOperationAsync(byte[] key) => ValueTask.FromResult<IAsyncDisposable>(EmptyCatalogOperation.Instance);
    Task InitializeAsync(byte[] key);
    Task PrepareForPublicationAsync(byte[] key);
    Task DeleteSettingsAsync(byte[] key, params string[] ids);
    Task<string> ComputeSnapshotFingerprintAsync(string snapshotPath, string excludedSettingId, byte[] key);
    Task<long> ReadStorageRevisionAsync(byte[] key);
    Task CreateSnapshotAsync(string destinationPath, byte[] key);
    Task<long> CountRecordsAsync(byte[] key, CancellationToken token = default);
    Task<List<ImageRecord>> ReadRecordsAsync(byte[] key);
    async Task<List<CatalogImportSource>> ReadImportSourcesAsync(byte[] key) => (await ReadRecordsAsync(key))
        .Select(record => new CatalogImportSource(record.Id, record.SourcePath, record.Sha256, record.OriginalBytes)).ToList();
    Task<ImageThumbnail> ReadThumbnailAsync(string id, byte[] key);
    Task InsertAsync(ImageRecord imageRecord, ImageThumbnail imageThumbnail, byte[] key);
    Task<ImportCommitResult> CommitImportsAsync(IReadOnlyList<ImportCatalogEntry> importCatalogEntries, byte[] key);
    Task<(ImageRecord Record, ImageThumbnail Thumbnail)> ReadVerificationAsync(string id, byte[] key);
    Task BackfillFingerprintsAsync(byte[] key);
    Task<bool> ContainsFingerprintAsync(string contentSha256, byte[] key);
    Task<ImageRecord> ReadRecordAsync(string id, byte[] key);
    Task UpdateAsync(ImageRecord imageRecord, byte[] key, bool preserveBrowseIndex = true);
    Task DeleteAsync(string id, byte[] key);
    Task<T?> ReadSettingsAsync<T>(string id, string purpose, byte[] key) where T : class;
    Task SaveSettingsAsync<T>(T value, string id, string purpose, byte[] key);
    Task<List<SavedView>> LoadViewsAsync(byte[] key);
    Task SaveViewAsync(string name, GalleryFilter galleryFilter, byte[] key);
    Task DeleteViewAsync(string name, byte[] key);
    Task<GalleryCatalog?> ReadInitialPageAsync(int pageSize, bool markedOnly, byte[] key, CancellationToken token);
    Task<GalleryCatalog> ReadCatalogSnapshotAsync(byte[] key, CancellationToken token);
    Task<List<ImageRecord>> ReadBrowseRecordsAsync(IEnumerable<string> ids, byte[] key, CancellationToken token);
    Task<bool> SaveBrowseIndexAsync(List<ImageRecord> records, long revision, byte[] key, CancellationToken token);
    Task EnqueueDriveOperationsAsync(IReadOnlyCollection<NewDriveOperation> operations, byte[] key);
    Task DeleteImageAndEnqueueAsync(string id, NewDriveOperation newDriveOperation, byte[] key);
    Task UpdateImageAndEnqueueAsync(ImageRecord imageRecord, byte[] key, NewDriveOperation newDriveOperation, long? supersededSequence = null);
    Task<List<DriveOperation>> ReadDueDriveOperationsAsync(int limit, long now, byte[] key);
    Task<List<DriveOperation>> ReadOutstandingDeletesAsync(byte[] key);
    Task<DriveOperation?> FindPendingDriveOperationAsync(string kind, byte[] target, byte[] key);
    Task RemoveDriveOperationAsync(long sequence, byte[] key);
    Task CompleteDriveOperationsAsync(IReadOnlyCollection<DriveOperationResult> driveOperationResults, byte[] key);
    Task<DriveQueueCounts> ReadDriveQueueCountsAsync(byte[] key);
    Task<int> CountDriveOperationsAsync(string kind, byte[] key);
    Task CancelUnfinishedDriveOperationsAsync(byte[] key);
    Task RetryFailedDriveOperationsAsync(byte[] key);
    Task ClearDriveStateAsync(byte[] key);
    Task ClearMediaIndexAsync(byte[] key);
    Task ReplaceDirectoryIndexAsync(byte[] directory, IReadOnlyCollection<IndexedFile> files,
        IReadOnlyCollection<NewDriveOperation> discoveredDriveOperations, byte[] key);
    Task IndexFilesAsync(IReadOnlyCollection<(byte[] Directory, IndexedFile File)> files, byte[] key);
    Task RemoveIndexedFileAsync(byte[] target, byte[] key);
    Task<Dictionary<string, long>> ReadMediaIndexAsync(byte[] key);
    Task<ImageRecord?> TryReadRecordAsync(string id, byte[] key);
}

internal sealed class EmptyCatalogOperation : IAsyncDisposable
{
    internal static readonly EmptyCatalogOperation Instance = new();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
