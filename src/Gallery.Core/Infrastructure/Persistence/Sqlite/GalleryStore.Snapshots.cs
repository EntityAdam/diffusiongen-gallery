using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Infrastructure.Persistence.Sqlite;

internal sealed partial class GalleryStore
{
    public Task PrepareForPublicationAsync(byte[]? key = null) => Task.CompletedTask;

    public Task<string> ComputeSnapshotFingerprintAsync(string snapshotPath, string excludedSettingId, byte[]? key = null) =>
        ComputeContentFingerprintAsync(snapshotPath, excludedSettingId);

    public async Task CreateSnapshotAsync(string destinationPath, byte[]? key = null)
    {
        await using var source = await OpenAsync();
        await using var destination = await OpenConnectionAsync(destinationPath, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
    }

    // Hashes the schema and every row of a snapshot in a deterministic order. The file bytes cannot be
    // used directly because writing backup status into the settings table changes the database on every run.
    public static async Task<string> ComputeContentFingerprintAsync(string snapshotPath, string excludedSettingId)
    {
        await using var sqliteConnection = await OpenSnapshotAsync(snapshotPath);
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var tables = await AppendSchemaFingerprintAsync(sqliteConnection, incrementalHash);
        await AppendTableFingerprintsAsync(sqliteConnection, incrementalHash, tables, excludedSettingId);
        return CompleteFingerprint(incrementalHash);
    }

    private static string CompleteFingerprint(IncrementalHash incrementalHash) =>
        Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLowerInvariant();

    private static Task<SqliteConnection> OpenSnapshotAsync(string snapshotPath) =>
        OpenConnectionAsync(snapshotPath, SqliteOpenMode.ReadOnly);

    private static async Task<List<string>> AppendSchemaFingerprintAsync(SqliteConnection sqliteConnection, IncrementalHash incrementalHash)
    {
        var tables = new List<string>();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadFingerprintSchema(sqliteConnection);
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        while (await sqliteDataReader.ReadAsync())
        {
            AppendFingerprintValue(incrementalHash, sqliteDataReader.GetString(0));
            AppendFingerprintValue(incrementalHash, sqliteDataReader.GetString(1));
            AppendFingerprintValue(incrementalHash, sqliteDataReader.GetString(2));
            if (sqliteDataReader.GetString(0) == "table") tables.Add(sqliteDataReader.GetString(1));
        }
        return tables;
    }

    private static async Task AppendTableFingerprintsAsync(SqliteConnection sqliteConnection, IncrementalHash incrementalHash,
        IEnumerable<string> tables, string excludedSettingId)
    {
        foreach (var table in tables)
            await AppendTableFingerprintAsync(sqliteConnection, incrementalHash, table, excludedSettingId);
    }

    private static async Task AppendTableFingerprintAsync(SqliteConnection sqliteConnection, IncrementalHash incrementalHash,
        string table, string excludedSettingId)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadFingerprintRows(sqliteConnection, table, excludedSettingId);
        AppendFingerprintValue(incrementalHash, "table:" + table);
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        while (await sqliteDataReader.ReadAsync())
            AppendRowFingerprint(incrementalHash, sqliteDataReader);
    }

    private static void AppendRowFingerprint(IncrementalHash incrementalHash, SqliteDataReader sqliteDataReader)
    {
        AppendFingerprintValue(incrementalHash, "row");
        for (var column = 0; column < sqliteDataReader.FieldCount; column++)
            AppendFingerprintValue(incrementalHash, sqliteDataReader.IsDBNull(column) ? null : sqliteDataReader.GetValue(column));
    }

    private static void AppendFingerprintValue(IncrementalHash incrementalHash, object? value)
    {
        byte tag;
        byte[] bytes;
        (tag, bytes) = value switch
        {
            null => ((byte)0, Array.Empty<byte>()),
            byte[] blob => ((byte)1, blob),
            string text => ((byte)2, System.Text.Encoding.UTF8.GetBytes(text)),
            long integer => ((byte)3, BitConverter.GetBytes(integer)),
            double real => ((byte)4, BitConverter.GetBytes(real)),
            _ => ((byte)5, System.Text.Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""))
        };
        incrementalHash.AppendData([tag]);
        incrementalHash.AppendData(BitConverter.GetBytes(bytes.Length));
        incrementalHash.AppendData(bytes);
    }
}
