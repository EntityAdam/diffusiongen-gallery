using System.Data.Common;
using System.Data;
using Gallery.Core.Features.Storage.DriveOperations;

namespace Gallery.Core.Infrastructure.Persistence.Sqlite;

internal static class DriveOperationCommandFactory
{
    public static DbCommand CreateEnqueueOrReset(DbConnection sqliteConnection, DbTransaction sqliteTransaction, NewDriveOperation newDriveOperation)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction, """
            INSERT INTO drive_operations (kind, target, image_id, payload, state, attempts, not_before, error)
            VALUES ($kind, $target, $imageId, $payload, 'pending', 0, 0, NULL)
            ON CONFLICT(kind, target) DO UPDATE SET image_id = excluded.image_id, payload = excluded.payload,
                state = 'pending', attempts = 0, not_before = 0, error = NULL, revision = drive_operations.revision + 1
            """);
        AddTextParameter(sqliteCommand, "$kind", newDriveOperation.Kind);
        sqliteCommand.AddParameter("$target", DbType.Binary).Value = newDriveOperation.Target.ToArray();
        AddTextParameter(sqliteCommand, "$imageId", newDriveOperation.ImageId);
        sqliteCommand.AddParameter("$payload", DbType.Binary).Value = newDriveOperation.Payload.ToArray();
        return sqliteCommand;
    }

    public static DbCommand CreateReadDue(DbConnection sqliteConnection, int limit, long now)
    {
        var sqliteCommand = Create(sqliteConnection, null, """
            SELECT sequence, kind, target, image_id, payload, attempts, revision FROM drive_operations
            WHERE state = 'pending' AND not_before <= $now ORDER BY sequence LIMIT $limit
            """);
        sqliteCommand.AddParameter("$now", DbType.Int64).Value = now;
        sqliteCommand.AddParameter("$limit", DbType.Int32).Value = limit;
        return sqliteCommand;
    }

    public static DbCommand CreateReadOutstandingDeletes(DbConnection sqliteConnection)
    {
        var sqliteCommand = Create(sqliteConnection, null, """
            SELECT sequence, kind, target, image_id, payload, attempts, revision FROM drive_operations
            WHERE kind = $kind ORDER BY sequence
            """);
        sqliteCommand.AddParameter("$kind", DbType.String, DriveOperationKind.DeleteFile.Length).Value = DriveOperationKind.DeleteFile;
        return sqliteCommand;
    }

    public static DbCommand CreateFind(DbConnection sqliteConnection, string kind, byte[] target)
    {
        var sqliteCommand = Create(sqliteConnection, null, """
            SELECT sequence, kind, target, image_id, payload, attempts, revision FROM drive_operations
            WHERE kind = $kind AND target = $target AND state = 'pending'
            """);
        AddTextParameter(sqliteCommand, "$kind", kind);
        sqliteCommand.AddParameter("$target", DbType.Binary).Value = target;
        return sqliteCommand;
    }

    public static DbCommand CreateDelete(DbConnection sqliteConnection, DbTransaction? sqliteTransaction, long sequence)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction, "DELETE FROM drive_operations WHERE sequence = $sequence");
        sqliteCommand.AddParameter("$sequence", DbType.Int64).Value = sequence;
        return sqliteCommand;
    }

    public static DbCommand CreateDeletePlayed(DbConnection sqliteConnection, DbTransaction sqliteTransaction, DriveOperationResult driveOperationResult)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction,
            "DELETE FROM drive_operations WHERE sequence = $sequence AND revision = $revision");
        sqliteCommand.AddParameter("$sequence", DbType.Int64).Value = driveOperationResult.Sequence;
        sqliteCommand.AddParameter("$revision", DbType.Int64).Value = driveOperationResult.Revision;
        return sqliteCommand;
    }

    public static DbCommand CreateUpdateOutcome(DbConnection sqliteConnection, DbTransaction sqliteTransaction, DriveOperationResult driveOperationResult)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction, """
            UPDATE drive_operations SET state = $state, attempts = $attempts, not_before = $notBefore, error = $error
            WHERE sequence = $sequence AND revision = $revision
            """);
        var state = driveOperationResult.Outcome == DriveOperationOutcome.Failed ? "failed" : "pending";
        AddTextParameter(sqliteCommand, "$state", state);
        sqliteCommand.AddParameter("$attempts", DbType.Int32).Value = driveOperationResult.Attempts;
        sqliteCommand.AddParameter("$notBefore", DbType.Int64).Value = driveOperationResult.NotBefore;
        AddTextParameter(sqliteCommand, "$error", driveOperationResult.Error);
        sqliteCommand.AddParameter("$sequence", DbType.Int64).Value = driveOperationResult.Sequence;
        sqliteCommand.AddParameter("$revision", DbType.Int64).Value = driveOperationResult.Revision;
        return sqliteCommand;
    }

    // NextDue covers every pending operation, including ones already due, so the player never idles with work left.
    public static DbCommand CreateCounts(DbConnection sqliteConnection) =>
        Create(sqliteConnection, null, """
            SELECT
                (SELECT COUNT(*) FROM drive_operations WHERE state = 'pending'),
                (SELECT COUNT(*) FROM drive_operations WHERE state = 'failed'),
                (SELECT MIN(not_before) FROM drive_operations WHERE state = 'pending')
            """);

    public static DbCommand CreateCountKind(DbConnection sqliteConnection, string kind)
    {
        var sqliteCommand = Create(sqliteConnection, null, "SELECT COUNT(*) FROM drive_operations WHERE kind = $kind");
        AddTextParameter(sqliteCommand, "$kind", kind);
        return sqliteCommand;
    }

    public static DbCommand CreateCancelUnfinished(DbConnection sqliteConnection) =>
        Create(sqliteConnection, null, "DELETE FROM drive_operations WHERE state IN ('pending', 'failed')");

    public static DbCommand CreateRetryFailed(DbConnection sqliteConnection) =>
        Create(sqliteConnection, null, "UPDATE drive_operations SET state = 'pending', attempts = 0, not_before = 0, error = NULL WHERE state = 'failed'");

    public static DbCommand CreateClear(DbConnection sqliteConnection) =>
        Create(sqliteConnection, null, "DELETE FROM drive_operations; DELETE FROM media_index;");

    public static DbCommand CreateClearIndex(DbConnection sqliteConnection, DbTransaction sqliteTransaction) =>
        Create(sqliteConnection, sqliteTransaction, """
            DELETE FROM media_index;
            UPDATE storage_overview_state SET revision = revision + 1 WHERE id = 1;
            """);

    public static DbCommand CreateDeleteDirectoryIndex(DbConnection sqliteConnection, DbTransaction sqliteTransaction, byte[] directory)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction, "DELETE FROM media_index WHERE directory = $directory");
        sqliteCommand.AddParameter("$directory", DbType.Binary).Value = directory;
        return sqliteCommand;
    }

    public static DbCommand CreateUpsertIndexedFile(DbConnection sqliteConnection, DbTransaction sqliteTransaction,
        byte[] directory, IndexedFile indexedFile)
    {
        var sqliteCommand = Create(sqliteConnection, sqliteTransaction, """
            INSERT INTO media_index (target, directory, length) VALUES ($target, $directory, $length)
            ON CONFLICT(target) DO UPDATE SET directory = excluded.directory, length = excluded.length
            """);
        sqliteCommand.AddParameter("$target", DbType.Binary).Value = indexedFile.Target.ToArray();
        sqliteCommand.AddParameter("$directory", DbType.Binary).Value = directory;
        sqliteCommand.AddParameter("$length", DbType.Int64).Value = indexedFile.Length;
        return sqliteCommand;
    }

    public static DbCommand CreateDeleteIndexedFile(DbConnection sqliteConnection, byte[] target)
    {
        var sqliteCommand = Create(sqliteConnection, null, "DELETE FROM media_index WHERE target = $target");
        sqliteCommand.AddParameter("$target", DbType.Binary).Value = target;
        return sqliteCommand;
    }

    public static DbCommand CreateReadIndex(DbConnection sqliteConnection) =>
        Create(sqliteConnection, null, "SELECT target, length FROM media_index");

    private static DbCommand Create(DbConnection sqliteConnection, DbTransaction? sqliteTransaction, string sql)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = sql;
        return sqliteCommand;
    }

    private static void AddTextParameter(DbCommand sqliteCommand, string name, string? value) =>
        sqliteCommand.AddParameter(name, DbType.String, Math.Max(value?.Length ?? 0, 1)).Value = (object?)value ?? DBNull.Value;
}
