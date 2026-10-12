using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

// Corruption and encryption tests inspect storage independently of the domain mapping they verify.
internal sealed class RawGalleryStore(string databasePath)
{
    public Task<byte[]> ReadRecordAsync(string id) => ReadRequiredAsync("images", "record", id);
    public Task<byte[]> ReadThumbnailAsync(string id) => ReadRequiredAsync("images", "thumbnail", id);
    public Task<byte[]?> ReadSettingsAsync(string id) => ReadAsync("settings", "payload", id);

    private async Task<byte[]> ReadRequiredAsync(string table, string column, string id) =>
        await ReadAsync(table, column, id) ?? throw new InvalidOperationException($"Missing {column} for '{id}'.");

    private async Task<byte[]?> ReadAsync(string table, string column, string id)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return (byte[]?)await command.ExecuteScalarAsync();
    }

    public async Task UpdateAsync(string id, byte[] payload)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE images SET record = $payload WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertAsync(string id, byte[] record, byte[] thumbnail, byte[] fingerprint)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO images (id, record, thumbnail, fingerprint) VALUES ($id, $record, $thumbnail, $fingerprint)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$record", record);
        command.Parameters.AddWithValue("$thumbnail", thumbnail);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await command.ExecuteNonQueryAsync();
    }
}
