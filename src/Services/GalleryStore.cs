using Microsoft.Data.Sqlite;

namespace Gallery.Services;

public sealed class GalleryStore(VaultStore vault)
{
    public SemaphoreSlim Gate { get; } = new(1);
    private readonly SemaphoreSlim schemaGate = new(1);
    private bool schemaReady;

    private async Task<SqliteConnection> OpenAsync()
    {
        if (!vault.Exists) throw new InvalidOperationException("Create a vault first.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(vault.Root, "gallery.db"),
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync();
            await schemaGate.WaitAsync();
            try
            {
                if (!schemaReady)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
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
                    await command.ExecuteNonQueryAsync();
                    command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('images') WHERE name = 'fingerprint'";
                    if (Convert.ToInt64(await command.ExecuteScalarAsync()) == 0)
                    {
                        command.CommandText = "ALTER TABLE images ADD COLUMN fingerprint BLOB";
                        await command.ExecuteNonQueryAsync();
                    }
                    command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS images_fingerprint ON images(fingerprint)";
                    await command.ExecuteNonQueryAsync();
                    schemaReady = true;
                }
            }
            finally { schemaGate.Release(); }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<List<(string Id, byte[] Payload)>> ReadRecordsAsync()
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, record FROM images";
        await using var reader = await command.ExecuteReaderAsync();
        var records = new List<(string, byte[])>();
        while (await reader.ReadAsync()) records.Add((reader.GetString(0), (byte[])reader[1]));
        return records;
    }

    public async Task<byte[]> ReadThumbnailAsync(string id)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT thumbnail FROM images WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return (byte[]?)await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Image not found.");
    }

    public async Task InsertAsync(string id, byte[] record, byte[] thumbnail, byte[] fingerprint)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO images (id, record, thumbnail, fingerprint) VALUES ($id, $record, $thumbnail, $fingerprint)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$record", record);
        command.Parameters.AddWithValue("$thumbnail", thumbnail);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await command.ExecuteNonQueryAsync();
    }

    // Caller holds Gate so backfill and ingestion cannot interleave in this process.
    public async Task BackfillFingerprintsAsync(Func<string, byte[], byte[]> fingerprint)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, record FROM images WHERE fingerprint IS NULL";
        var missing = new List<(string Id, byte[] Record)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) missing.Add((reader.GetString(0), (byte[])reader[1]));
        }
        foreach (var (id, record) in missing)
        {
            command.Parameters.Clear();
            command.CommandText = "UPDATE images SET fingerprint = $fingerprint WHERE id = $id AND fingerprint IS NULL";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$fingerprint", fingerprint(id, record));
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    public async Task<bool> ContainsFingerprintAsync(byte[] fingerprint)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM images WHERE fingerprint = $fingerprint)";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) != 0;
    }

    public async Task<byte[]> ReadRecordAsync(string id)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record FROM images WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return (byte[]?)await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Image not found.");
    }

    public async Task UpdateAsync(string id, byte[] record)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE images SET record = $record WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$record", record);
        if (await command.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Image not found.");
    }

    public async Task DeleteAsync(string id)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM images WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Image not found.");
    }

    public async Task<byte[]?> ReadSettingsAsync(string id = "vision")
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM settings WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return (byte[]?)await command.ExecuteScalarAsync();
    }

    public async Task SaveSettingsAsync(byte[] payload, string id = "vision")
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (id, payload) VALUES ($id, $payload) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync();
    }
}
