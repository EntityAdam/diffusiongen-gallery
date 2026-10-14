using Microsoft.Data.Sqlite;

namespace Gallery.Web.Services.DataManagers.CommandFactories;

internal static class GalleryStoreCommandFactory
{
    public static SqliteCommand CreateInitializeSchema(SqliteConnection sqliteConnection)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS images (
                id TEXT PRIMARY KEY,
                record BLOB NOT NULL,
                thumbnail BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                id TEXT PRIMARY KEY,
                payload BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS drive_operations (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                target BLOB NOT NULL,
                image_id TEXT,
                payload BLOB NOT NULL,
                state TEXT NOT NULL DEFAULT 'pending',
                attempts INTEGER NOT NULL DEFAULT 0,
                not_before INTEGER NOT NULL DEFAULT 0,
                error TEXT,
                revision INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS drive_operations_kind_target ON drive_operations(kind, target);
            CREATE INDEX IF NOT EXISTS drive_operations_due ON drive_operations(state, not_before, sequence);
            CREATE TABLE IF NOT EXISTS media_index (
                target BLOB PRIMARY KEY,
                directory BLOB NOT NULL,
                length INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS media_index_directory ON media_index(directory);
            """;
        return sqliteCommand;
    }

    public static SqliteCommand CreateCheckFingerprintColumn(SqliteConnection sqliteConnection)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('images') WHERE name = 'fingerprint'";
        return sqliteCommand;
    }

    public static SqliteCommand CreateAddFingerprintColumn(SqliteConnection sqliteConnection)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "ALTER TABLE images ADD COLUMN fingerprint BLOB";
        return sqliteCommand;
    }

    public static SqliteCommand CreateFingerprintIndex(SqliteConnection sqliteConnection)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS images_fingerprint ON images(fingerprint)";
        return sqliteCommand;
    }

    public static SqliteCommand CreateReadImageRecords(SqliteConnection sqliteConnection)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "SELECT id, record FROM images";
        return sqliteCommand;
    }

    public static SqliteCommand CreateReadThumbnail(SqliteConnection sqliteConnection, string id)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "SELECT thumbnail FROM images WHERE id = $id";
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        return sqliteCommand;
    }

    public static SqliteCommand CreateInsertImage(
        SqliteConnection sqliteConnection, string id, byte[] record, byte[] thumbnail, byte[] fingerprint)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "INSERT INTO images (id, record, thumbnail, fingerprint) VALUES ($id, $record, $thumbnail, $fingerprint)";
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        sqliteCommand.Parameters.Add("$record", SqliteType.Blob).Value = record;
        sqliteCommand.Parameters.Add("$thumbnail", SqliteType.Blob).Value = thumbnail;
        sqliteCommand.Parameters.Add("$fingerprint", SqliteType.Blob).Value = fingerprint;
        return sqliteCommand;
    }

    public static SqliteCommand CreateReadRecordsMissingFingerprints(
        SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = "SELECT id, record FROM images WHERE fingerprint IS NULL";
        return sqliteCommand;
    }

    public static SqliteCommand CreateUpdateFingerprint(
        SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction, string id, byte[] fingerprint)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = "UPDATE images SET fingerprint = $fingerprint WHERE id = $id AND fingerprint IS NULL";
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        sqliteCommand.Parameters.Add("$fingerprint", SqliteType.Blob).Value = fingerprint;
        return sqliteCommand;
    }

    public static SqliteCommand CreateContainsFingerprint(SqliteConnection sqliteConnection, byte[] fingerprint)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM images WHERE fingerprint = $fingerprint)";
        sqliteCommand.Parameters.Add("$fingerprint", SqliteType.Blob).Value = fingerprint;
        return sqliteCommand;
    }

    public static SqliteCommand CreateReadRecord(SqliteConnection sqliteConnection, string id)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "SELECT record FROM images WHERE id = $id";
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        return sqliteCommand;
    }

    public static SqliteCommand CreateUpdateImage(SqliteConnection sqliteConnection, string id, byte[] record)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "UPDATE images SET record = $record WHERE id = $id";
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        sqliteCommand.Parameters.Add("$record", SqliteType.Blob).Value = record;
        return sqliteCommand;
    }

    public static SqliteCommand CreateDeleteImage(SqliteConnection sqliteConnection, string id)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "DELETE FROM images WHERE id = $id";
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        return sqliteCommand;
    }

    public static SqliteCommand CreateReadSettings(SqliteConnection sqliteConnection, string id)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "SELECT payload FROM settings WHERE id = $id";
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        return sqliteCommand;
    }

    public static SqliteCommand CreateSaveSettings(SqliteConnection sqliteConnection, string id, byte[] payload)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "INSERT INTO settings (id, payload) VALUES ($id, $payload) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload";
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        sqliteCommand.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
        return sqliteCommand;
    }
}
