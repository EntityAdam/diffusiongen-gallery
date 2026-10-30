using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers.CommandFactories;
using Gallery.Core.Services.DataManagers.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Services.DataManagers;

// Queued drive operations and the cached media index. Catalog changes that imply drive work commit together
// with the queued operation, so the catalog and the queue can never disagree after a crash.
internal sealed partial class GalleryStore
{
    public async Task EnqueueDriveOperationsAsync(IReadOnlyCollection<NewDriveOperation> operations)
    {
        if (operations.Count == 0) return;
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await EnqueueAsync(sqliteConnection, sqliteTransaction, operations);
        sqliteTransaction.Commit();
    }

    public async Task DeleteImageAndEnqueueAsync(string id, NewDriveOperation newDriveOperation)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await DeleteImageAsync(sqliteConnection, sqliteTransaction, id);
        await EnqueueAsync(sqliteConnection, sqliteTransaction, [newDriveOperation]);
        sqliteTransaction.Commit();
    }

    /// <summary>Updates a record, drops a superseded queued operation and enqueues follow-up drive work atomically.</summary>
    public async Task UpdateImageAndEnqueueAsync(ImageRecord imageRecord, byte[] key, NewDriveOperation newDriveOperation, long? supersededSequence = null)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await UpdateImageAsync(sqliteConnection, sqliteTransaction, imageRecord, key);
        if (supersededSequence is long sequence)
            await RemoveSupersededOperationAsync(sqliteConnection, sqliteTransaction, sequence);
        await EnqueueAsync(sqliteConnection, sqliteTransaction, [newDriveOperation]);
        sqliteTransaction.Commit();
    }


    private static async Task ApplyDriveResultsAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction,
        IReadOnlyCollection<DriveOperationResult> driveOperationResults)
    {
        foreach (var driveOperationResult in driveOperationResults)
        {
            using var sqliteCommand = driveOperationResult.Outcome == DriveOperationOutcome.Done
                ? DriveOperationCommandFactory.CreateDeletePlayed(sqliteConnection, sqliteTransaction, driveOperationResult)
                : DriveOperationCommandFactory.CreateUpdateOutcome(sqliteConnection, sqliteTransaction, driveOperationResult);
            await sqliteCommand.ExecuteNonQueryAsync();
        }
    }

    private static async Task DeleteImageAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction, string id)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateDeleteImage(sqliteConnection, id, sqliteTransaction);
        var affectedRows = await sqliteCommand.ExecuteNonQueryAsync();
        RequireSingleImageMutation(affectedRows, id, DeleteAction, DeletedOutcome);
    }

    private static async Task UpdateImageAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction,
        ImageRecord imageRecord, byte[] key)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateUpdateImage(sqliteConnection, imageRecord.Id, EncodeRecord(imageRecord, key), sqliteTransaction);
        var affectedRows = await sqliteCommand.ExecuteNonQueryAsync();
        RequireSingleImageMutation(affectedRows, imageRecord.Id, UpdateAction, UpdatedOutcome);
    }

    private static async Task RemoveSupersededOperationAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction, long sequence)
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateDelete(sqliteConnection, sqliteTransaction, sequence);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private static async Task EnqueueAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction, IEnumerable<NewDriveOperation> operations)
    {
        foreach (var newDriveOperation in operations)
        {
            using var sqliteCommand = DriveOperationCommandFactory.CreateEnqueueOrReset(sqliteConnection, sqliteTransaction, newDriveOperation);
            await sqliteCommand.ExecuteNonQueryAsync();
        }
    }

    public async Task<List<DriveOperation>> ReadDueDriveOperationsAsync(int limit, long now)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateReadDue(sqliteConnection, limit, now);
        return await ReadOperationsAsync(sqliteCommand);
    }

    public async Task<List<DriveOperation>> ReadOutstandingDeletesAsync()
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateReadOutstandingDeletes(sqliteConnection);
        return await ReadOperationsAsync(sqliteCommand);
    }

    public async Task<DriveOperation?> FindPendingDriveOperationAsync(string kind, byte[] target)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateFind(sqliteConnection, kind, target);
        return (await ReadOperationsAsync(sqliteCommand)).SingleOrDefault();
    }

    private static async Task<List<DriveOperation>> ReadOperationsAsync(SqliteCommand sqliteCommand)
    {
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        var operations = new List<DriveOperation>();
        while (await sqliteDataReader.ReadAsync())
            operations.Add(new(sqliteDataReader.GetInt64(0), sqliteDataReader.GetString(1), sqliteDataReader.GetFieldValue<byte[]>(2),
                sqliteDataReader.IsDBNull(3) ? null : sqliteDataReader.GetString(3), sqliteDataReader.GetFieldValue<byte[]>(4), sqliteDataReader.GetInt32(5), sqliteDataReader.GetInt64(6)));
        return operations;
    }

    public async Task RemoveDriveOperationAsync(long sequence)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateDelete(sqliteConnection, null, sequence);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    /// <summary>Records a played batch in one transaction: finished operations leave the queue, others are rescheduled.</summary>
    public async Task CompleteDriveOperationsAsync(IReadOnlyCollection<DriveOperationResult> driveOperationResults)
    {
        if (driveOperationResults.Count == 0) return;
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await ApplyDriveResultsAsync(sqliteConnection, sqliteTransaction, driveOperationResults);
        sqliteTransaction.Commit();
    }

    public async Task<DriveQueueCounts> ReadDriveQueueCountsAsync()
    {
        await using var sqliteConnection = await OpenExistingAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateCounts(sqliteConnection);
        return await ReadQueueCountsAsync(sqliteCommand);
    }

    private static async Task<DriveQueueCounts> ReadQueueCountsAsync(SqliteCommand sqliteCommand)
    {
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        if (!await sqliteDataReader.ReadAsync())
            throw new InvalidOperationException("Expected one drive queue count row, but found 0.");
        return new(sqliteDataReader.GetInt32(0), sqliteDataReader.GetInt32(1), sqliteDataReader.IsDBNull(2) ? null : sqliteDataReader.GetInt64(2));
    }

    public async Task<int> CountDriveOperationsAsync(string kind)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateCountKind(sqliteConnection, kind);
        return Convert.ToInt32(await sqliteCommand.ExecuteScalarAsync());
    }

    public async Task CancelUnfinishedDriveOperationsAsync()
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateCancelUnfinished(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    public async Task RetryFailedDriveOperationsAsync()
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateRetryFailed(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    /// <summary>Drops queued work and the index; both describe paths of a gallery location that no longer applies.</summary>
    public async Task ClearDriveStateAsync()
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateClear(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    public async Task ClearMediaIndexAsync()
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        using var sqliteCommand = DriveOperationCommandFactory.CreateClearIndex(sqliteConnection, sqliteTransaction);
        await sqliteCommand.ExecuteNonQueryAsync();
        sqliteTransaction.Commit();
    }

    /// <summary>Replaces one directory's cached listing and enqueues the work it discovered, atomically.</summary>
    public async Task ReplaceDirectoryIndexAsync(byte[] directory, IReadOnlyCollection<IndexedFile> files,
        IReadOnlyCollection<NewDriveOperation> discoveredDriveOperations)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await DeleteDirectoryIndexAsync(sqliteConnection, sqliteTransaction, directory);
        await UpsertIndexedFilesAsync(sqliteConnection, sqliteTransaction, directory, files);
        await EnqueueAsync(sqliteConnection, sqliteTransaction, discoveredDriveOperations);
        sqliteTransaction.Commit();
    }

    public async Task IndexFilesAsync(IReadOnlyCollection<(byte[] Directory, IndexedFile File)> files)
    {
        if (files.Count == 0) return;
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await UpsertIndexedFilesAsync(sqliteConnection, sqliteTransaction, files);
        sqliteTransaction.Commit();
    }

    private static async Task DeleteDirectoryIndexAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction, byte[] directory)
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateDeleteDirectoryIndex(sqliteConnection, sqliteTransaction, directory);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private static async Task UpsertIndexedFilesAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction,
        IReadOnlyCollection<(byte[] Directory, IndexedFile File)> files)
    {
        byte[] directory;
        IndexedFile indexedFile;
        foreach (var indexedEntry in files)
        {
            (directory, indexedFile) = indexedEntry;
            await UpsertIndexedFilesAsync(sqliteConnection, sqliteTransaction, directory, [indexedFile]);
        }
    }

    private static async Task UpsertIndexedFilesAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction,
        byte[] directory, IEnumerable<IndexedFile> files)
    {
        foreach (var indexedFile in files)
        {
            using var sqliteCommand = DriveOperationCommandFactory.CreateUpsertIndexedFile(sqliteConnection, sqliteTransaction, directory, indexedFile);
            await sqliteCommand.ExecuteNonQueryAsync();
        }
    }

    public async Task RemoveIndexedFileAsync(byte[] target)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateDeleteIndexedFile(sqliteConnection, target);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, long>> ReadMediaIndexAsync()
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = DriveOperationCommandFactory.CreateReadIndex(sqliteConnection);
        return await ReadIndexedLengthsAsync(sqliteCommand);
    }

    private static async Task<Dictionary<string, long>> ReadIndexedLengthsAsync(SqliteCommand sqliteCommand)
    {
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        var lengthsByTarget = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await sqliteDataReader.ReadAsync())
            lengthsByTarget[Convert.ToHexString(sqliteDataReader.GetFieldValue<byte[]>(0))] = sqliteDataReader.GetInt64(1);
        return lengthsByTarget;
    }

    public async Task<ImageRecord?> TryReadRecordAsync(string id, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadRecord(sqliteConnection, id);
        var encryptedRecord = (byte[]?)await sqliteCommand.ExecuteScalarAsync();
        return encryptedRecord is null ? null : DecodeRecord(id, encryptedRecord, key);
    }
}
