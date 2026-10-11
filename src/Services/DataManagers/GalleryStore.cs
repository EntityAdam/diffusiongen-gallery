using Microsoft.Data.Sqlite;

namespace Gallery.Services;

internal sealed class GalleryStore(string databasePath)
{
    private const string VisionSettingsId = "vision";

    private readonly string databasePath = Path.GetFullPath(databasePath);
    public SemaphoreSlim Gate { get; } = new(1);
    private readonly SemaphoreSlim schemaGate = new(1);

    private async Task<SqliteConnection> OpenAsync()
    {
        var sqliteConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        try
        {
            await sqliteConnection.OpenAsync();
            await InitializeSchemaAsync(sqliteConnection);
            return sqliteConnection;
        }
        catch
        {
            await sqliteConnection.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeSchemaAsync(SqliteConnection sqliteConnection)
    {
        await schemaGate.WaitAsync();
        try
        {
            await CreateBaseSchemaAsync(sqliteConnection);
            if (!await HasFingerprintColumnAsync(sqliteConnection))
                await AddFingerprintColumnAsync(sqliteConnection);
            await CreateFingerprintIndexAsync(sqliteConnection);
        }
        finally
        {
            schemaGate.Release();
        }
    }

    private static async Task CreateBaseSchemaAsync(SqliteConnection sqliteConnection)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateInitializeSchema(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private static async Task<bool> HasFingerprintColumnAsync(SqliteConnection sqliteConnection)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateCheckFingerprintColumn(sqliteConnection);
        return Convert.ToInt64(await sqliteCommand.ExecuteScalarAsync()) != 0;
    }

    private static async Task AddFingerprintColumnAsync(SqliteConnection sqliteConnection)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateAddFingerprintColumn(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private static async Task CreateFingerprintIndexAsync(SqliteConnection sqliteConnection)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateFingerprintIndex(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    public async Task<List<(string Id, byte[] Payload)>> ReadRecordsAsync()
    {
        await using var sqliteConnection = await OpenAsync();
        return await ReadImageRecordsAsync(sqliteConnection);
    }

    private static async Task<List<(string Id, byte[] Payload)>> ReadImageRecordsAsync(SqliteConnection sqliteConnection)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadImageRecords(sqliteConnection);
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        var imageRecords = new List<(string Id, byte[] Payload)>();
        while (await sqliteDataReader.ReadAsync())
        {
            var imageRecord = MapImageRecord(sqliteDataReader);
            imageRecords.Add((imageRecord.Id, imageRecord.Record));
        }
        return imageRecords;
    }

    private static (string Id, byte[] Record) MapImageRecord(SqliteDataReader sqliteDataReader) =>
        (sqliteDataReader.GetString(0), sqliteDataReader.GetFieldValue<byte[]>(1));

    public async Task<byte[]> ReadThumbnailAsync(string id)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadThumbnail(sqliteConnection, id);
        return (byte[]?)await sqliteCommand.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"Expected one thumbnail for image '{id}', but found 0.");
    }

    public async Task InsertAsync(string id, byte[] record, byte[] thumbnail, byte[] fingerprint)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateInsertImage(sqliteConnection, id, record, thumbnail, fingerprint);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    // Caller holds Gate so backfill and ingestion cannot interleave in this process.
    public async Task BackfillFingerprintsAsync(Func<string, byte[], byte[]> fingerprint)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        var recordsMissingFingerprints = await ReadRecordsMissingFingerprintsAsync(sqliteConnection, sqliteTransaction);
        await UpdateMissingFingerprintsAsync(sqliteConnection, sqliteTransaction, recordsMissingFingerprints, fingerprint);
        sqliteTransaction.Commit();
    }

    private static async Task<List<(string Id, byte[] Record)>> ReadRecordsMissingFingerprintsAsync(
        SqliteConnection sqliteConnection,
        SqliteTransaction sqliteTransaction)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadRecordsMissingFingerprints(sqliteConnection, sqliteTransaction);
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        var recordsMissingFingerprints = new List<(string Id, byte[] Record)>();
        while (await sqliteDataReader.ReadAsync())
            recordsMissingFingerprints.Add(MapImageRecord(sqliteDataReader));
        return recordsMissingFingerprints;
    }

    private static async Task UpdateMissingFingerprintsAsync(
        SqliteConnection sqliteConnection,
        SqliteTransaction sqliteTransaction,
        List<(string Id, byte[] Record)> recordsMissingFingerprints,
        Func<string, byte[], byte[]> fingerprint)
    {
        string id;
        byte[] record;
        foreach (var imageRecord in recordsMissingFingerprints)
        {
            id = imageRecord.Id;
            record = imageRecord.Record;
            using var sqliteCommand = GalleryStoreCommandFactory.CreateUpdateFingerprint(
                sqliteConnection, sqliteTransaction, id, fingerprint(id, record));
            await sqliteCommand.ExecuteNonQueryAsync();
        }
    }

    public async Task<bool> ContainsFingerprintAsync(byte[] fingerprint)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateContainsFingerprint(sqliteConnection, fingerprint);
        return Convert.ToInt64(await sqliteCommand.ExecuteScalarAsync()) != 0;
    }

    public async Task<byte[]> ReadRecordAsync(string id)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadRecord(sqliteConnection, id);
        return (byte[]?)await sqliteCommand.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"Expected one record for image '{id}', but found 0.");
    }

    public async Task UpdateAsync(string id, byte[] record)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateUpdateImage(sqliteConnection, id, record);
        var affectedRows = await sqliteCommand.ExecuteNonQueryAsync();
        if (affectedRows != 1)
            throw new InvalidOperationException($"Expected to update one image with id '{id}', but updated {affectedRows}.");
    }

    public async Task DeleteAsync(string id)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateDeleteImage(sqliteConnection, id);
        var affectedRows = await sqliteCommand.ExecuteNonQueryAsync();
        if (affectedRows != 1)
            throw new InvalidOperationException($"Expected to delete one image with id '{id}', but deleted {affectedRows}.");
    }

    public async Task<byte[]?> ReadSettingsAsync(string id = VisionSettingsId)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadSettings(sqliteConnection, id);
        return (byte[]?)await sqliteCommand.ExecuteScalarAsync();
    }

    public async Task SaveSettingsAsync(byte[] payload, string id = VisionSettingsId)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateSaveSettings(sqliteConnection, id, payload);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private static class GalleryStoreCommandFactory
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
}
