using Microsoft.Data.Sqlite;

namespace Gallery.Stress.Core;

public sealed record ByteDistribution(long Sum, long P50, long P95, long Max);
public sealed record CatalogProfile(long Rows, ByteDistribution Record, ByteDistribution Thumbnail,
    long PageSize, long PageCount, long FreelistCount, long FileBytes, long WalBytes,
    long MissingFingerprints, Dictionary<string, long> DriveOperations, Dictionary<string, long> ObjectBytes, string ObjectBytesSource);

public static class CatalogProfiler
{
    public static async Task<CatalogProfile> ProfileAsync(string db, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        async Task<long> Scalar(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        }
        async Task<ByteDistribution> Distribution(string column)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT length({column}) FROM images ORDER BY length({column})";
            var lengths = new List<long>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) lengths.Add(reader.GetInt64(0));
            if (lengths.Count == 0) return new(0, 0, 0, 0);
            return new(lengths.Sum(), lengths[(int)Math.Ceiling(lengths.Count * 0.5) - 1], lengths[(int)Math.Ceiling(lengths.Count * 0.95) - 1], lengths[^1]);
        }
        var rows = await Scalar("SELECT count(*) FROM images");
        var records = await Distribution("record");
        var thumbnails = await Distribution("thumbnail");
        var operations = new Dictionary<string, long>();
        if (await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='drive_operations'") > 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT state,count(*) FROM drive_operations GROUP BY state";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) operations[reader.GetString(0)] = reader.GetInt64(1);
        }
        var objects = new Dictionary<string, long>();
        var objectSource = "dbstat";
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name,sum(pgsize) FROM dbstat GROUP BY name";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) objects[reader.GetString(0)] = reader.GetInt64(1);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 1 && e.Message.Contains("dbstat", StringComparison.OrdinalIgnoreCase))
        {
            objectSource = "length-sum-estimate (excludes B-tree and index overhead)";
            objects["images"] = records.Sum + thumbnails.Sum;
        }
        var missing = await Scalar("SELECT count(*) FROM images WHERE fingerprint IS NULL");
        return new(rows, records, thumbnails, await Scalar("PRAGMA page_size"), await Scalar("PRAGMA page_count"),
            await Scalar("PRAGMA freelist_count"), new FileInfo(db).Length, File.Exists(db + "-wal") ? new FileInfo(db + "-wal").Length : 0,
            missing, operations, objects, objectSource);
    }
}
