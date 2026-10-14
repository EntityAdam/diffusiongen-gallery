using Gallery.Web.Services.DataManagers.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Web.Services.DataManagers.CommandFactories;

internal static class DriveOperationCommandFactory
{
    // Re-enqueueing the same kind and target replaces the queued payload and resets a failed operation.
    public static SqliteCommand CreateEnqueue(SqliteConnection connection, SqliteTransaction transaction, NewDriveOperation operation)
    {
        var command = Create(connection, transaction, """
            INSERT INTO drive_operations (kind, target, image_id, payload, state, attempts, not_before, error)
            VALUES ($kind, $target, $imageId, $payload, 'pending', 0, 0, NULL)
            ON CONFLICT(kind, target) DO UPDATE SET image_id = excluded.image_id, payload = excluded.payload,
                state = 'pending', attempts = 0, not_before = 0, error = NULL, revision = drive_operations.revision + 1
            """);
        command.Parameters.AddWithValue("$kind", operation.Kind);
        command.Parameters.Add("$target", SqliteType.Blob).Value = operation.Target;
        command.Parameters.AddWithValue("$imageId", (object?)operation.ImageId ?? DBNull.Value);
        command.Parameters.Add("$payload", SqliteType.Blob).Value = operation.Payload;
        return command;
    }

    public static SqliteCommand CreateReadDue(SqliteConnection connection, int limit, long now)
    {
        var command = Create(connection, null, """
            SELECT sequence, kind, target, image_id, payload, attempts, revision FROM drive_operations
            WHERE state = 'pending' AND not_before <= $now ORDER BY sequence LIMIT $limit
            """);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$limit", limit);
        return command;
    }

    public static SqliteCommand CreateFind(SqliteConnection connection, string kind, byte[] target)
    {
        var command = Create(connection, null, """
            SELECT sequence, kind, target, image_id, payload, attempts, revision FROM drive_operations
            WHERE kind = $kind AND target = $target AND state = 'pending'
            """);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.Add("$target", SqliteType.Blob).Value = target;
        return command;
    }

    public static SqliteCommand CreateDelete(SqliteConnection connection, SqliteTransaction? transaction, long sequence)
    {
        var command = Create(connection, transaction, "DELETE FROM drive_operations WHERE sequence = $sequence");
        command.Parameters.AddWithValue("$sequence", sequence);
        return command;
    }

    public static SqliteCommand CreateDeletePlayed(SqliteConnection connection, SqliteTransaction transaction, DriveOperationResult result)
    {
        var command = Create(connection, transaction,
            "DELETE FROM drive_operations WHERE sequence = $sequence AND revision = $revision");
        command.Parameters.AddWithValue("$sequence", result.Sequence);
        command.Parameters.AddWithValue("$revision", result.Revision);
        return command;
    }

    public static SqliteCommand CreateUpdateOutcome(SqliteConnection connection, SqliteTransaction transaction, DriveOperationResult result)
    {
        var command = Create(connection, transaction, """
            UPDATE drive_operations SET state = $state, attempts = $attempts, not_before = $notBefore, error = $error
            WHERE sequence = $sequence AND revision = $revision
            """);
        command.Parameters.AddWithValue("$state", result.Outcome == DriveOperationOutcome.Failed ? "failed" : "pending");
        command.Parameters.AddWithValue("$attempts", result.Attempts);
        command.Parameters.AddWithValue("$notBefore", result.NotBefore);
        command.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$sequence", result.Sequence);
        command.Parameters.AddWithValue("$revision", result.Revision);
        return command;
    }

    // NextDue covers every pending operation, including ones already due, so the player never idles with work left.
    public static SqliteCommand CreateCounts(SqliteConnection connection) =>
        Create(connection, null, """
            SELECT
                (SELECT COUNT(*) FROM drive_operations WHERE state = 'pending'),
                (SELECT COUNT(*) FROM drive_operations WHERE state = 'failed'),
                (SELECT MIN(not_before) FROM drive_operations WHERE state = 'pending')
            """);

    public static SqliteCommand CreateCountKind(SqliteConnection connection, string kind)
    {
        var command = Create(connection, null, "SELECT COUNT(*) FROM drive_operations WHERE kind = $kind");
        command.Parameters.AddWithValue("$kind", kind);
        return command;
    }

    public static SqliteCommand CreateCancelUnfinished(SqliteConnection connection) =>
        Create(connection, null, "DELETE FROM drive_operations WHERE state IN ('pending', 'failed')");

    public static SqliteCommand CreateRetryFailed(SqliteConnection connection) =>
        Create(connection, null, "UPDATE drive_operations SET state = 'pending', attempts = 0, not_before = 0, error = NULL WHERE state = 'failed'");

    public static SqliteCommand CreateClear(SqliteConnection connection) =>
        Create(connection, null, "DELETE FROM drive_operations; DELETE FROM media_index;");

    public static SqliteCommand CreateClearIndex(SqliteConnection connection) =>
        Create(connection, null, "DELETE FROM media_index");

    public static SqliteCommand CreateDeleteDirectoryIndex(SqliteConnection connection, SqliteTransaction transaction, byte[] directory)
    {
        var command = Create(connection, transaction, "DELETE FROM media_index WHERE directory = $directory");
        command.Parameters.Add("$directory", SqliteType.Blob).Value = directory;
        return command;
    }

    public static SqliteCommand CreateUpsertIndexedFile(SqliteConnection connection, SqliteTransaction transaction,
        byte[] directory, IndexedFile file)
    {
        var command = Create(connection, transaction, """
            INSERT INTO media_index (target, directory, length) VALUES ($target, $directory, $length)
            ON CONFLICT(target) DO UPDATE SET directory = excluded.directory, length = excluded.length
            """);
        command.Parameters.Add("$target", SqliteType.Blob).Value = file.Target;
        command.Parameters.Add("$directory", SqliteType.Blob).Value = directory;
        command.Parameters.AddWithValue("$length", file.Length);
        return command;
    }

    public static SqliteCommand CreateDeleteIndexedFile(SqliteConnection connection, byte[] target)
    {
        var command = Create(connection, null, "DELETE FROM media_index WHERE target = $target");
        command.Parameters.Add("$target", SqliteType.Blob).Value = target;
        return command;
    }

    public static SqliteCommand CreateReadIndex(SqliteConnection connection) =>
        Create(connection, null, "SELECT target, length FROM media_index");

    private static SqliteCommand Create(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
