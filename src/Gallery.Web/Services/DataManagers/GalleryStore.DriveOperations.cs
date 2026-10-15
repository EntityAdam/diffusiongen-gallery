using Gallery.Web.Models;
using Gallery.Web.Services.DataManagers.CommandFactories;
using Gallery.Web.Services.DataManagers.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Web.Services.DataManagers;

// Queued drive operations and the cached media index. Catalog changes that imply drive work commit together
// with the queued operation, so the catalog and the queue can never disagree after a crash.
internal sealed partial class GalleryStore
{
    public async Task EnqueueDriveOperationsAsync(IReadOnlyCollection<NewDriveOperation> operations)
    {
        if (operations.Count == 0) return;
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        await EnqueueAsync(connection, transaction, operations);
        transaction.Commit();
    }

    public async Task DeleteImageAndEnqueueAsync(string id, NewDriveOperation operation)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        using (var command = GalleryStoreCommandFactory.CreateDeleteImage(connection, id))
        {
            command.Transaction = transaction;
            var affectedRows = await command.ExecuteNonQueryAsync();
            if (affectedRows != 1)
                throw new InvalidOperationException($"Expected to delete one image with id '{id}', but deleted {affectedRows}.");
        }
        await EnqueueAsync(connection, transaction, [operation]);
        transaction.Commit();
    }

    /// <summary>Updates a record, drops a superseded queued operation and enqueues follow-up drive work atomically.</summary>
    public async Task UpdateImageAndEnqueueAsync(WebImageRecord record, byte[] key, NewDriveOperation operation, long? supersededSequence = null)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        using (var command = GalleryStoreCommandFactory.CreateUpdateImage(connection, record.Id, EncodeRecord(record, key)))
        {
            command.Transaction = transaction;
            var affectedRows = await command.ExecuteNonQueryAsync();
            if (affectedRows != 1)
                throw new InvalidOperationException($"Expected to update one image with id '{record.Id}', but updated {affectedRows}.");
        }
        if (supersededSequence is long sequence)
        {
            using var command = DriveOperationCommandFactory.CreateDelete(connection, transaction, sequence);
            await command.ExecuteNonQueryAsync();
        }
        await EnqueueAsync(connection, transaction, [operation]);
        transaction.Commit();
    }

    private static async Task EnqueueAsync(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<NewDriveOperation> operations)
    {
        foreach (var operation in operations)
        {
            using var command = DriveOperationCommandFactory.CreateEnqueue(connection, transaction, operation);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task<List<DriveOperation>> ReadDueDriveOperationsAsync(int limit, long now)
    {
        await using var connection = await OpenAsync();
        using var command = DriveOperationCommandFactory.CreateReadDue(connection, limit, now);
        return await ReadOperationsAsync(command);
    }

    public async Task<DriveOperation?> FindPendingDriveOperationAsync(string kind, byte[] target)
    {
        await using var connection = await OpenAsync();
        using var command = DriveOperationCommandFactory.CreateFind(connection, kind, target);
        return (await ReadOperationsAsync(command)).SingleOrDefault();
    }

    private static async Task<List<DriveOperation>> ReadOperationsAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var operations = new List<DriveOperation>();
        while (await reader.ReadAsync())
            operations.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<byte[]>(4), reader.GetInt32(5), reader.GetInt64(6)));
        return operations;
    }

    public async Task RemoveDriveOperationAsync(long sequence)
    {
        await using var connection = await OpenAsync();
        using var command = DriveOperationCommandFactory.CreateDelete(connection, null, sequence);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Records a played batch in one transaction: finished operations leave the queue, others are rescheduled.</summary>
    public async Task CompleteDriveOperationsAsync(IReadOnlyCollection<DriveOperationResult> results)
    {
        if (results.Count == 0) return;
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        foreach (var result in results)
        {
            using var command = result.Outcome == DriveOperationOutcome.Done
                ? DriveOperationCommandFactory.CreateDeletePlayed(connection, transaction, result)
                : DriveOperationCommandFactory.CreateUpdateOutcome(connection, transaction, result);
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    public async Task<DriveQueueCounts> ReadDriveQueueCountsAsync()
    {
        await using var connection = await OpenAsync();
        using var command = DriveOperationCommandFactory.CreateCounts(connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    public async Task<int> CountDriveOperationsAsync(string kind)
    {
        await using var connection = await OpenAsync();
        using var command = DriveOperationCommandFactory.CreateCountKind(connection, kind);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public async Task CancelUnfinishedDriveOperationsAsync()
    {
        await using var connection = await OpenAsync();
        using var command = DriveOperationCommandFactory.CreateCancelUnfinished(connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task RetryFailedDriveOperationsAsync()
    {
        await using var connection = await OpenAsync();
        using var command = DriveOperationCommandFactory.CreateRetryFailed(connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Drops queued work and the index; both describe paths of a gallery location that no longer applies.</summary>
    public async Task ClearDriveStateAsync()
    {
        await using var connection = await OpenAsync();
        using var command = DriveOperationCommandFactory.CreateClear(connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ClearMediaIndexAsync()
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = DriveOperationCommandFactory.CreateClearIndex(connection, transaction);
        await command.ExecuteNonQueryAsync();
        transaction.Commit();
    }

    /// <summary>Replaces one directory's cached listing and enqueues the work it discovered, atomically.</summary>
    public async Task ReplaceDirectoryIndexAsync(byte[] directory, IReadOnlyCollection<IndexedFile> files,
        IReadOnlyCollection<NewDriveOperation> discovered)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        using (var command = DriveOperationCommandFactory.CreateDeleteDirectoryIndex(connection, transaction, directory))
            await command.ExecuteNonQueryAsync();
        await UpsertIndexedFilesAsync(connection, transaction, directory, files);
        await EnqueueAsync(connection, transaction, discovered);
        transaction.Commit();
    }

    public async Task IndexFilesAsync(IReadOnlyCollection<(byte[] Directory, IndexedFile File)> files)
    {
        if (files.Count == 0) return;
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        foreach (var (directory, file) in files)
            await UpsertIndexedFilesAsync(connection, transaction, directory, [file]);
        transaction.Commit();
    }

    private static async Task UpsertIndexedFilesAsync(SqliteConnection connection, SqliteTransaction transaction,
        byte[] directory, IEnumerable<IndexedFile> files)
    {
        foreach (var file in files)
        {
            using var command = DriveOperationCommandFactory.CreateUpsertIndexedFile(connection, transaction, directory, file);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task RemoveIndexedFileAsync(byte[] target)
    {
        await using var connection = await OpenAsync();
        using var command = DriveOperationCommandFactory.CreateDeleteIndexedFile(connection, target);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, long>> ReadMediaIndexAsync()
    {
        await using var connection = await OpenAsync();
        using var command = DriveOperationCommandFactory.CreateReadIndex(connection);
        await using var reader = await command.ExecuteReaderAsync();
        var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            lengths[Convert.ToHexString(reader.GetFieldValue<byte[]>(0))] = reader.GetInt64(1);
        return lengths;
    }

    public async Task<WebImageRecord?> TryReadRecordAsync(string id, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var command = GalleryStoreCommandFactory.CreateReadRecord(connection, id);
        var encryptedRecord = (byte[]?)await command.ExecuteScalarAsync();
        return encryptedRecord is null ? null : DecodeRecord(id, encryptedRecord, key);
    }
}
