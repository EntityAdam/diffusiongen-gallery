using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Gallery.Migrations.Turso;

namespace Gallery.Core.Services.DataManagers.Turso;

internal sealed partial class TursoGalleryStore
{
    public Task CreateSnapshotAsync(string destinationPath, byte[]? key = null) => ExecuteAsync(key, async connection =>
    {
        // All adapter operations hold databaseGate; the vault checkout excludes other application processes.
        // Copy only after a successful checkpoint and release of the native driver's exclusive file handle.
        await CheckpointAsync(connection);
        await connection.CloseAsync();
        File.Copy(databasePath, destinationPath, overwrite: false);
    });

    public Task PrepareForPublicationAsync(byte[]? key = null) => ExecuteAsync(key, CheckpointAsync);

    private static async Task CheckpointAsync(DbConnection connection)
    {
        var started = Stopwatch.GetTimestamp();
        using var command = Command(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetInt64(0) != 0)
            throw new IOException("The Turso catalog could not be checkpointed for publication.");
        CatalogMetrics.Checkpoints.Add(1);
        CatalogMetrics.CheckpointDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public Task<string> ComputeSnapshotFingerprintAsync(string snapshotPath, string excludedSettingId, byte[]? key = null) =>
        ExecuteAsync(key, connection => ComputeFingerprintAsync(connection, excludedSettingId), path: snapshotPath, upgrade: false);

    private static async Task<string> ComputeFingerprintAsync(DbConnection connection, string excludedSettingId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddFingerprintValue(hash, "turso-catalog:v1");
        var tables = new List<string>();
        using (var command = Command(connection,
            // Journaling schema upgrades must not change legacy logical backup fingerprints.
            "SELECT type,name,COALESCE(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND name<>$journal AND tbl_name<>$journal ORDER BY type,name",
            null, ("$journal", TursoGalleryDatabase.JournalTableName)))
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                AddFingerprintValue(hash, reader.GetString(0));
                AddFingerprintValue(hash, reader.GetString(1));
                AddFingerprintValue(hash, reader.GetString(2));
                if (reader.GetString(0) == "table") tables.Add(reader.GetString(1));
            }
        }
        foreach (var table in tables)
        {
            var quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
            using var command = Command(connection, "SELECT * FROM " + quoted +
                (table == "settings" ? " WHERE id<>$excluded" : "") + " ORDER BY rowid", null,
                table == "settings" ? [("$excluded", excludedSettingId)] : []);
            AddFingerprintValue(hash, "table:" + table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                AddFingerprintValue(hash, "row");
                for (var i = 0; i < reader.FieldCount; i++) AddFingerprintValue(hash, reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AddFingerprintValue(IncrementalHash hash, object? value)
    {
        var (tag, bytes) = value switch
        {
            null => ((byte)0, Array.Empty<byte>()),
            byte[] blob => ((byte)1, blob),
            string text => ((byte)2, Encoding.UTF8.GetBytes(text)),
            long number => ((byte)3, BitConverter.GetBytes(number)),
            double number => ((byte)4, BitConverter.GetBytes(number)),
            _ => throw new InvalidOperationException("Unsupported Turso fingerprint value type.")
        };
        hash.AppendData([tag]);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
    }
}
