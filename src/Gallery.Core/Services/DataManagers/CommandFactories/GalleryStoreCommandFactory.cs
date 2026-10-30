using Microsoft.Data.Sqlite;

namespace Gallery.Core.Services.DataManagers.CommandFactories;

internal static class GalleryStoreCommandFactory
{
    public static SqliteCommand CreateConfigureWalJournal(SqliteConnection sqliteConnection) =>
        CreateCommand(sqliteConnection, "PRAGMA journal_mode=WAL");

    public static SqliteCommand CreateConfigureDeleteJournal(SqliteConnection sqliteConnection) =>
        CreateCommand(sqliteConnection, "PRAGMA journal_mode=DELETE");

    public static SqliteCommand CreateConfigureDurability(SqliteConnection sqliteConnection) =>
        CreateCommand(sqliteConnection, "PRAGMA synchronous=FULL");

    public static SqliteCommand CreateDeleteSettings(SqliteConnection sqliteConnection, string id)
    {
        var sqliteCommand = CreateCommand(sqliteConnection, "DELETE FROM settings WHERE id = $id");
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        return sqliteCommand;
    }

    public static SqliteCommand CreateReadStorageRevision(SqliteConnection sqliteConnection) =>
        CreateCommand(sqliteConnection, "SELECT revision FROM storage_overview_state WHERE id = 1");

    public static SqliteCommand CreateReadFingerprintSchema(SqliteConnection sqliteConnection) =>
        CreateCommand(sqliteConnection, "SELECT type, name, COALESCE(sql, '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name");

    public static SqliteCommand CreateReadFingerprintRows(SqliteConnection sqliteConnection, string table, string excludedSettingId)
    {
        var sqliteCommand = CreateCommand(sqliteConnection, CreateReadFingerprintRowsSql(table));
        if (table == "settings")
            sqliteCommand.Parameters.Add("$excluded", SqliteType.Text, excludedSettingId.Length).Value = excludedSettingId;
        return sqliteCommand;
    }

    private static string CreateReadFingerprintRowsSql(string table)
    {
        var quotedTable = QuoteIdentifier(table);
        return table == "settings"
            ? $"SELECT * FROM {quotedTable} WHERE id <> $excluded ORDER BY rowid"
            : $"SELECT * FROM {quotedTable} ORDER BY rowid";
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static SqliteCommand CreateReadVerification(SqliteConnection sqliteConnection, string id)
    {
        var sqliteCommand = CreateCommand(sqliteConnection, "SELECT record, thumbnail FROM images WHERE id = $id");
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        return sqliteCommand;
    }

    private static SqliteCommand CreateCommand(SqliteConnection sqliteConnection, string sql, SqliteTransaction? sqliteTransaction = null)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = sql;
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

    public static SqliteCommand CreateUpdateImage(SqliteConnection sqliteConnection, string id, byte[] record, SqliteTransaction? sqliteTransaction = null)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = "UPDATE images SET record = $record WHERE id = $id";
        sqliteCommand.Parameters.Add("$id", SqliteType.Text, id.Length).Value = id;
        sqliteCommand.Parameters.Add("$record", SqliteType.Blob).Value = record;
        return sqliteCommand;
    }

    public static SqliteCommand CreateDeleteImage(SqliteConnection sqliteConnection, string id, SqliteTransaction? sqliteTransaction = null)
    {
        var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
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
