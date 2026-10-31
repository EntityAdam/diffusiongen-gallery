using System.Data.Common;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers.CommandFactories;
using Gallery.Core.Services.Ports.Models;

namespace Gallery.Core.Services.DataManagers.Turso;

// Queued drive operations and the cached media index. Catalog changes that imply drive work commit together
// with the queued operation, so the catalog and the queue can never disagree after a crash.
internal sealed partial class TursoGalleryStore
{
    public Task EnqueueDriveOperationsAsync(IReadOnlyCollection<NewDriveOperation> operations, byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        if (operations.Count == 0) return;
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await EnqueueAsync(sqliteConnection, sqliteTransaction, operations);
        sqliteTransaction.Commit();
    });

    public Task DeleteImageAndEnqueueAsync(string id, NewDriveOperation newDriveOperation, byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await DeleteImageAsync(sqliteConnection, sqliteTransaction, id);
        await EnqueueAsync(sqliteConnection, sqliteTransaction, [newDriveOperation]);
        sqliteTransaction.Commit();
    });

    /// <summary>Updates a record, drops a superseded queued operation and enqueues follow-up drive work atomically.</summary>
    public Task UpdateImageAndEnqueueAsync(ImageRecord imageRecord, byte[] key, NewDriveOperation newDriveOperation, long? supersededSequence = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await UpdateImageAsync(sqliteConnection, sqliteTransaction, imageRecord, key);
        if (supersededSequence is long sequence)
            await RemoveSupersededOperationAsync(sqliteConnection, sqliteTransaction, sequence);
        await EnqueueAsync(sqliteConnection, sqliteTransaction, [newDriveOperation]);
        sqliteTransaction.Commit();
    });


    private static async Task ApplyDriveResultsAsync(DbConnection sqliteConnection, DbTransaction sqliteTransaction,
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

    private static async Task DeleteImageAsync(DbConnection connection, DbTransaction transaction, string id) =>
        RequireOne(await NonQueryAsync(connection, "DELETE FROM media WHERE id=$id", transaction, ("$id", id)), id);

    private static Task UpdateImageAsync(DbConnection connection, DbTransaction transaction, ImageRecord record, byte[] key) =>
        WriteRecordAsync(connection, transaction, record, insert: false);

    private static async Task RemoveSupersededOperationAsync(DbConnection sqliteConnection, DbTransaction sqliteTransaction, long sequence)
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateDelete(sqliteConnection, sqliteTransaction, sequence);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private static async Task EnqueueAsync(DbConnection sqliteConnection, DbTransaction sqliteTransaction, IEnumerable<NewDriveOperation> operations)
    {
        foreach (var newDriveOperation in operations)
        {
            using var sqliteCommand = DriveOperationCommandFactory.CreateEnqueueOrReset(sqliteConnection, sqliteTransaction, newDriveOperation);
            await sqliteCommand.ExecuteNonQueryAsync();
        }
    }

    public Task<List<DriveOperation>> ReadDueDriveOperationsAsync(int limit, long now, byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateReadDue(sqliteConnection, limit, now);
        return await ReadOperationsAsync(sqliteCommand);
    });

    public Task<List<DriveOperation>> ReadOutstandingDeletesAsync(byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateReadOutstandingDeletes(sqliteConnection);
        return await ReadOperationsAsync(sqliteCommand);
    });

    public Task<DriveOperation?> FindPendingDriveOperationAsync(string kind, byte[] target, byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateFind(sqliteConnection, kind, target);
        return (await ReadOperationsAsync(sqliteCommand)).SingleOrDefault();
    });

    private static async Task<List<DriveOperation>> ReadOperationsAsync(DbCommand sqliteCommand)
    {
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        var operations = new List<DriveOperation>();
        while (await sqliteDataReader.ReadAsync())
            operations.Add(new(sqliteDataReader.GetInt64(0), sqliteDataReader.GetString(1), sqliteDataReader.GetFieldValue<byte[]>(2),
                sqliteDataReader.IsDBNull(3) ? null : sqliteDataReader.GetString(3), sqliteDataReader.GetFieldValue<byte[]>(4), sqliteDataReader.GetInt32(5), sqliteDataReader.GetInt64(6)));
        return operations;
    }

    public Task RemoveDriveOperationAsync(long sequence, byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateDelete(sqliteConnection, null, sequence);
        await sqliteCommand.ExecuteNonQueryAsync();
    });

    /// <summary>Records a played batch in one transaction: finished operations leave the queue, others are rescheduled.</summary>
    public Task CompleteDriveOperationsAsync(IReadOnlyCollection<DriveOperationResult> driveOperationResults, byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        if (driveOperationResults.Count == 0) return;
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await ApplyDriveResultsAsync(sqliteConnection, sqliteTransaction, driveOperationResults);
        sqliteTransaction.Commit();
    });

    public Task<DriveQueueCounts> ReadDriveQueueCountsAsync(byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateCounts(sqliteConnection);
        return await ReadQueueCountsAsync(sqliteCommand);
    });

    private static async Task<DriveQueueCounts> ReadQueueCountsAsync(DbCommand sqliteCommand)
    {
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        if (!await sqliteDataReader.ReadAsync())
            throw new InvalidOperationException("Expected one drive queue count row, but found 0.");
        return new(sqliteDataReader.GetInt32(0), sqliteDataReader.GetInt32(1), sqliteDataReader.IsDBNull(2) ? null : sqliteDataReader.GetInt64(2));
    }

    public Task<int> CountDriveOperationsAsync(string kind, byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateCountKind(sqliteConnection, kind);
        return Convert.ToInt32(await sqliteCommand.ExecuteScalarAsync());
    });

    public Task CancelUnfinishedDriveOperationsAsync(byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateCancelUnfinished(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    });

    public Task RetryFailedDriveOperationsAsync(byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateRetryFailed(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    });

    /// <summary>Drops queued work and the index; both describe paths of a gallery location that no longer applies.</summary>
    public Task ClearDriveStateAsync(byte[]? key = null) => ExecuteAsync(key, async connection =>
    {
        using var transaction = connection.BeginTransaction();
        await NonQueryAsync(connection, "DELETE FROM drive_operations", transaction);
        await NonQueryAsync(connection, "DELETE FROM media_index", transaction);
        transaction.Commit();
    });

    public Task ClearMediaIndexAsync(byte[]? key = null) => ExecuteAsync(key, async connection =>
    {
        using var transaction = connection.BeginTransaction();
        await NonQueryAsync(connection, "DELETE FROM media_index", transaction);
        await NonQueryAsync(connection, "UPDATE storage_overview_state SET revision=revision+1 WHERE id=1", transaction);
        transaction.Commit();
    });

    /// <summary>Replaces one directory's cached listing and enqueues the work it discovered, atomically.</summary>
    public Task ReplaceDirectoryIndexAsync(byte[] directory, IReadOnlyCollection<IndexedFile> files,
        IReadOnlyCollection<NewDriveOperation> discoveredDriveOperations, byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await DeleteDirectoryIndexAsync(sqliteConnection, sqliteTransaction, directory);
        await UpsertIndexedFilesAsync(sqliteConnection, sqliteTransaction, directory, files);
        await EnqueueAsync(sqliteConnection, sqliteTransaction, discoveredDriveOperations);
        sqliteTransaction.Commit();
    });

    public Task IndexFilesAsync(IReadOnlyCollection<(byte[] Directory, IndexedFile File)> files, byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        if (files.Count == 0) return;
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        await UpsertIndexedFilesAsync(sqliteConnection, sqliteTransaction, files);
        sqliteTransaction.Commit();
    });

    private static async Task DeleteDirectoryIndexAsync(DbConnection sqliteConnection, DbTransaction sqliteTransaction, byte[] directory)
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateDeleteDirectoryIndex(sqliteConnection, sqliteTransaction, directory);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private static async Task UpsertIndexedFilesAsync(DbConnection sqliteConnection, DbTransaction sqliteTransaction,
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

    private static async Task UpsertIndexedFilesAsync(DbConnection sqliteConnection, DbTransaction sqliteTransaction,
        byte[] directory, IEnumerable<IndexedFile> files)
    {
        foreach (var indexedFile in files)
        {
            using var sqliteCommand = DriveOperationCommandFactory.CreateUpsertIndexedFile(sqliteConnection, sqliteTransaction, directory, indexedFile);
            await sqliteCommand.ExecuteNonQueryAsync();
        }
    }

    public Task RemoveIndexedFileAsync(byte[] target, byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateDeleteIndexedFile(sqliteConnection, target);
        await sqliteCommand.ExecuteNonQueryAsync();
    });

    public Task<Dictionary<string, long>> ReadMediaIndexAsync(byte[]? key = null) => ExecuteAsync(key, async sqliteConnection =>
    {
        using var sqliteCommand = DriveOperationCommandFactory.CreateReadIndex(sqliteConnection);
        return await ReadIndexedLengthsAsync(sqliteCommand);
    });

    private static async Task<Dictionary<string, long>> ReadIndexedLengthsAsync(DbCommand sqliteCommand)
    {
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        var lengthsByTarget = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await sqliteDataReader.ReadAsync())
            lengthsByTarget[Convert.ToHexString(sqliteDataReader.GetFieldValue<byte[]>(0))] = sqliteDataReader.GetInt64(1);
        return lengthsByTarget;
    }


}
