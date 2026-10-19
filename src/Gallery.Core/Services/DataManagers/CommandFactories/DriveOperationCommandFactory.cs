using Gallery.Core.Services.DataManagers.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Services.DataManagers.CommandFactories;

internal static class DriveOperationCommandFactory
{
    // Re-enqueueing the same kind and target replaces the queued payload and resets a failed operation.
    public static SqliteCommand CreateEnqueue(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction, NewDriveOperation newDriveOperation)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction, """
            INSERT INTO drive_operations (kind, target, image_id, payload, state, attempts, not_before, error)
            VALUES ($kind, $target, $imageId, $payload, 'pending', 0, 0, NULL)
            ON CONFLICT(kind, target) DO UPDATE SET image_id = excluded.image_id, payload = excluded.payload,
                state = 'pending', attempts = 0, not_before = 0, error = NULL, revision = drive_operations.revision + 1
            """);
        sqliteCommand.Parameters.AddWithValue("$kind", newDriveOperation.Kind);
        sqliteCommand.Parameters.Add("$target", SqliteType.Blob).Value = newDriveOperation.Target.ToArray();
        sqliteCommand.Parameters.AddWithValue("$imageId", (object?)newDriveOperation.ImageId ?? DBNull.Value);
        sqliteCommand.Parameters.Add("$payload", SqliteType.Blob).Value = newDriveOperation.Payload.ToArray();
        return sqliteCommand;
    }

    public static SqliteCommand CreateReadDue(SqliteConnection sqliteConnection, int limit, long now)
    {
        var sqliteCommand = Create(sqliteConnection, null, """
            SELECT sequence, kind, target, image_id, payload, attempts, revision FROM drive_operations
            WHERE state = 'pending' AND not_before <= $now ORDER BY sequence LIMIT $limit
            """);
        sqliteCommand.Parameters.AddWithValue("$now", now);
        sqliteCommand.Parameters.AddWithValue("$limit", limit);
        return sqliteCommand;
    }

    public static SqliteCommand CreateReadOutstandingDeletes(SqliteConnection sqliteConnection)
    {
        var sqliteCommand = Create(sqliteConnection, null, """
            SELECT sequence, kind, target, image_id, payload, attempts, revision FROM drive_operations
            WHERE kind = $kind ORDER BY sequence
            """);
        sqliteCommand.Parameters.Add("$kind", SqliteType.Text, DriveOperationKind.DeleteFile.Length).Value = DriveOperationKind.DeleteFile;
        return sqliteCommand;
    }

    public static SqliteCommand CreateFind(SqliteConnection sqliteConnection, string kind, byte[] target)
    {
        var sqliteCommand = Create(sqliteConnection, null, """
            SELECT sequence, kind, target, image_id, payload, attempts, revision FROM drive_operations
            WHERE kind = $kind AND target = $target AND state = 'pending'
            """);
        sqliteCommand.Parameters.AddWithValue("$kind", kind);
        sqliteCommand.Parameters.Add("$target", SqliteType.Blob).Value = target;
        return sqliteCommand;
    }

    public static SqliteCommand CreateDelete(SqliteConnection sqliteConnection, SqliteTransaction? sqliteTransaction, long sequence)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction, "DELETE FROM drive_operations WHERE sequence = $sequence");
        sqliteCommand.Parameters.AddWithValue("$sequence", sequence);
        return sqliteCommand;
    }

    public static SqliteCommand CreateDeletePlayed(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction, DriveOperationResult driveOperationResult)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction,
            "DELETE FROM drive_operations WHERE sequence = $sequence AND revision = $revision");
        sqliteCommand.Parameters.AddWithValue("$sequence", driveOperationResult.Sequence);
        sqliteCommand.Parameters.AddWithValue("$revision", driveOperationResult.Revision);
        return sqliteCommand;
    }

    public static SqliteCommand CreateUpdateOutcome(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction, DriveOperationResult driveOperationResult)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction, """
            UPDATE drive_operations SET state = $state, attempts = $attempts, not_before = $notBefore, error = $error
            WHERE sequence = $sequence AND revision = $revision
            """);
        sqliteCommand.Parameters.AddWithValue("$state", driveOperationResult.Outcome == DriveOperationOutcome.Failed ? "failed" : "pending");
        sqliteCommand.Parameters.AddWithValue("$attempts", driveOperationResult.Attempts);
        sqliteCommand.Parameters.AddWithValue("$notBefore", driveOperationResult.NotBefore);
        sqliteCommand.Parameters.AddWithValue("$error", (object?)driveOperationResult.Error ?? DBNull.Value);
        sqliteCommand.Parameters.AddWithValue("$sequence", driveOperationResult.Sequence);
        sqliteCommand.Parameters.AddWithValue("$revision", driveOperationResult.Revision);
        return sqliteCommand;
    }

    // NextDue covers every pending operation, including ones already due, so the player never idles with work left.
    public static SqliteCommand CreateCounts(SqliteConnection sqliteConnection) =>
        Create(sqliteConnection, null, """
            SELECT
                (SELECT COUNT(*) FROM drive_operations WHERE state = 'pending'),
                (SELECT COUNT(*) FROM drive_operations WHERE state = 'failed'),
                (SELECT MIN(not_before) FROM drive_operations WHERE state = 'pending')
            """);

    public static SqliteCommand CreateCountKind(SqliteConnection sqliteConnection, string kind)
    {
        var sqliteCommand = Create(sqliteConnection, null, "SELECT COUNT(*) FROM drive_operations WHERE kind = $kind");
        sqliteCommand.Parameters.AddWithValue("$kind", kind);
        return sqliteCommand;
    }

    public static SqliteCommand CreateCancelUnfinished(SqliteConnection sqliteConnection) =>
        Create(sqliteConnection, null, "DELETE FROM drive_operations WHERE state IN ('pending', 'failed')");

    public static SqliteCommand CreateRetryFailed(SqliteConnection sqliteConnection) =>
        Create(sqliteConnection, null, "UPDATE drive_operations SET state = 'pending', attempts = 0, not_before = 0, error = NULL WHERE state = 'failed'");

    public static SqliteCommand CreateClear(SqliteConnection sqliteConnection) =>
        Create(sqliteConnection, null, "DELETE FROM drive_operations; DELETE FROM media_index;");

    public static SqliteCommand CreateClearIndex(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction) =>
        Create(sqliteConnection, sqliteTransaction, """
            DELETE FROM media_index;
            UPDATE storage_overview_state SET revision = revision + 1 WHERE id = 1;
            """);

    public static SqliteCommand CreateDeleteDirectoryIndex(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction, byte[] directory)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction, "DELETE FROM media_index WHERE directory = $directory");
        sqliteCommand.Parameters.Add("$directory", SqliteType.Blob).Value = directory;
        return sqliteCommand;
    }

    public static SqliteCommand CreateUpsertIndexedFile(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction,
        byte[] directory, IndexedFile indexedFile)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction, """
            INSERT INTO media_index (target, directory, length) VALUES ($target, $directory, $length)
            ON CONFLICT(target) DO UPDATE SET directory = excluded.directory, length = excluded.length
            """);
        sqliteCommand.Parameters.Add("$target", SqliteType.Blob).Value = indexedFile.Target.ToArray();
        sqliteCommand.Parameters.Add("$directory", SqliteType.Blob).Value = directory;
        sqliteCommand.Parameters.AddWithValue("$length", indexedFile.Length);
        return sqliteCommand;
    }

    public static SqliteCommand CreateDeleteIndexedFile(SqliteConnection sqliteConnection, byte[] target)
    {
        var sqliteCommand = Create(sqliteConnection, null, "DELETE FROM media_index WHERE target = $target");
        sqliteCommand.Parameters.Add("$target", SqliteType.Blob).Value = target;
        return sqliteCommand;
    }

    public static SqliteCommand CreateReadIndex(SqliteConnection sqliteConnection) =>
        Create(sqliteConnection, null, "SELECT target, length FROM media_index");

    private static SqliteCommand Create(SqliteConnection sqliteConnection, SqliteTransaction? sqliteTransaction, string sql)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = sql;
        return sqliteCommand;
    }
}
