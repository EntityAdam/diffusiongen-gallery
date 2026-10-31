using System.Collections.Immutable;
using System.Data.Common;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers.CommandFactories;
using Gallery.Core.Services.Ports.Models;
using Gallery.Core.Services.DataManagers.Processors;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Processors.Codecs;
using Turso;
using Turso.Raw.Public;

namespace Gallery.Core.Services.DataManagers.Turso;

/// <summary>Opt-in local Turso adapter. Typed catalog values are encrypted by the engine, not by record codecs.</summary>
internal sealed partial class TursoGalleryStore(string databasePath, long walCheckpointBytes = 64L * 1024 * 1024) : IGalleryStore
{
    private readonly string databasePath = Path.GetFullPath(databasePath);
    // The native driver holds an exclusive file handle. All use, checkpointing and snapshot copying
    // share this gate. A retained handle belongs to explicit operations whose key leases outlive it.
    private readonly SemaphoreSlim databaseGate = new(1, 1);
    private DbConnection? retainedConnection;
    private byte[]? operationKeyHash;
    private int activeOperations;
    private long nextWalMaintenanceAttempt;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public SemaphoreSlim BackupGate { get; } = new(1, 1);

    public async ValueTask<IAsyncDisposable> OpenOperationAsync(byte[] key)
    {
        ValidateKey(key);
        await databaseGate.WaitAsync();
        try
        {
            CheckOperationKey(key);
            operationKeyHash ??= SHA256.HashData(key);
            activeOperations++;
            return new CatalogOperation(this);
        }
        finally { databaseGate.Release(); }
    }

    private sealed class CatalogOperation(TursoGalleryStore owner) : IAsyncDisposable
    {
        private TursoGalleryStore? owner = owner;
        public async ValueTask DisposeAsync()
        {
            var captured = Interlocked.Exchange(ref owner, null);
            if (captured is null) return;
            await captured.databaseGate.WaitAsync();
            try
            {
                if (--captured.activeOperations != 0) return;
                try { await captured.CloseRetainedConnectionAsync(); }
                finally
                {
                    CryptographicOperations.ZeroMemory(captured.operationKeyHash!);
                    captured.operationKeyHash = null;
                }
            }
            finally { captured.databaseGate.Release(); }
        }
    }

    private static void ValidateKey(byte[]? key)
    {
        if (key is null || key.Length != 32)
            throw new InvalidOperationException("The Turso catalog requires a leased vault key.");
    }

    private void CheckOperationKey(byte[] key)
    {
        if (operationKeyHash is null) return;
        var hash = SHA256.HashData(key);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(operationKeyHash, hash))
                throw new CatalogStorageException("The catalog key differs from the active operation.", new CryptographicException());
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    private async ValueTask CloseRetainedConnectionAsync()
    {
        var connection = retainedConnection;
        retainedConnection = null;
        if (connection is not null) await connection.DisposeAsync();
    }

    private async Task<T> ExecuteAsync<T>(byte[]? key, Func<DbConnection, Task<T>> operation,
        bool initialize = false, string? path = null)
    {
        ValidateKey(key);
        var waitStarted = Stopwatch.GetTimestamp();
        await databaseGate.WaitAsync();
        CatalogMetrics.GateWaitDuration.Record(Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds);
        DbConnection? transient = null;
        try
        {
            CheckOperationKey(key!);
            var catalogPath = path is null ? databasePath : Path.GetFullPath(path);
            var primary = string.Equals(catalogPath, databasePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            var reuse = primary && activeOperations > 0;
            var connection = reuse ? retainedConnection : null;
            if (connection is null)
            {
                connection = await OpenConnectionAsync(catalogPath, key!, initialize);
                if (reuse) retainedConnection = connection;
                else transient = connection;
                if (primary) await MaintainWalAsync(connection);
            }
            var result = await operation(connection);
            // Operations dispose their readers and transactions before returning. Snapshot closes the
            // connection before copying; its next constituent call must reopen instead of reusing it.
            if (connection.State == ConnectionState.Open)
            {
                if (primary) await MaintainWalAsync(connection);
            }
            else if (reuse) await CloseRetainedConnectionAsync();
            return result;
        }
        catch (TursoException error)
        {
            await CloseRetainedConnectionAsync();
            throw new CatalogStorageException("The experimental Turso catalog operation failed.", error);
        }
        catch
        {
            await CloseRetainedConnectionAsync();
            throw;
        }
        finally
        {
            try { if (transient is not null) await transient.DisposeAsync(); }
            finally { databaseGate.Release(); }
        }
    }

    private static async Task<DbConnection> OpenConnectionAsync(string catalogPath, byte[] key, bool initialize)
    {
        if (!initialize && !File.Exists(catalogPath)) throw new FileNotFoundException("The catalog is missing.", catalogPath);
        if (File.Exists(catalogPath) && new FileInfo(catalogPath).Length > 0)
        {
            using var file = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var magic = new byte[5];
            if (file.Read(magic) != magic.Length || !magic.AsSpan().SequenceEqual("Turso"u8))
                throw new InvalidOperationException("The catalog is not a Turso encrypted database. Existing SQLite vaults cannot be converted by selecting an adapter.");
        }
        byte[] catalogKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32,
            info: "DiffusionGenGallery/catalog/turso/v1"u8.ToArray());
        try
        {
            var builder = new DbConnectionStringBuilder
            {
                ["Data Source"] = catalogPath, ["Pooling"] = false,
                ["Encryption Cipher"] = "aes256gcm", ["Encryption Key"] = Convert.ToHexString(catalogKey)
            };
            // Use the published ADO.NET API directly: 0.8.3's Sqlite facade rejects encryption keywords.
            var connection = new TursoConnection(builder.ConnectionString);
            try
            {
                var started = Stopwatch.GetTimestamp();
                await connection.OpenAsync();
                CatalogMetrics.ConnectionOpens.Add(1);
                CatalogMetrics.ConnectionDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                await NonQueryAsync(connection, "PRAGMA foreign_keys=ON");
                await NonQueryAsync(connection, "PRAGMA temp_store=MEMORY");
                await NonQueryAsync(connection, "PRAGMA synchronous=FULL");
                if (initialize) await InitializeSchemaAsync(connection);
                else if (Convert.ToInt64(await ScalarAsync(connection, "SELECT version FROM catalog_format WHERE id=1")) != 1)
                    throw new InvalidOperationException("Unsupported Turso catalog version.");
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(catalogKey); }
    }

    private async Task MaintainWalAsync(DbConnection connection)
    {
        if (Environment.TickCount64 < nextWalMaintenanceAttempt) return;
        try
        {
            var wal = new FileInfo(databasePath + "-wal");
            var bytes = wal.Exists ? wal.Length : 0;
            CatalogMetrics.WalBytes.Record(bytes);
            // The threshold can be exceeded by one complete transaction. Never checkpoint inside a
            // transaction or loop waiting for readers. Publication uses a mandatory checkpoint instead.
            if (bytes >= walCheckpointBytes) await CheckpointAsync(connection);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TursoException)
        {
            // A transaction may already be committed. Maintenance failure must never report that
            // import as failed (callers would clean up its now-catalogued media). Retry on a later call.
            CatalogMetrics.CheckpointFailures.Add(1);
            nextWalMaintenanceAttempt = Environment.TickCount64 + 30_000;
        }
    }

    private Task ExecuteAsync(byte[]? key, Func<DbConnection, Task> operation, bool initialize = false) =>
        ExecuteAsync(key, async connection => { await operation(connection); return true; }, initialize);

    private static DbCommand Command(DbConnection connection, string sql, DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.AddParameter(name, value ?? DBNull.Value);
        return command;
    }

    private static async Task<int> NonQueryAsync(DbConnection connection, string sql, DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, transaction, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql, DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, transaction, parameters);
        return await command.ExecuteScalarAsync();
    }

    public Task InitializeAsync(byte[]? key = null) => ExecuteAsync(key, _ => Task.CompletedTask, initialize: true);
    public Task<long> ReadStorageRevisionAsync(byte[]? key = null) => ExecuteAsync(key, ReadRevisionAsync);
    private static async Task<long> ReadRevisionAsync(DbConnection connection) =>
        Convert.ToInt64(await ScalarAsync(connection, "SELECT revision FROM storage_overview_state WHERE id=1"));

    public Task<long> CountRecordsAsync(byte[] key, CancellationToken token = default) => ExecuteAsync(key, async connection =>
    {
        token.ThrowIfCancellationRequested();
        using var command = Command(connection, "SELECT COUNT(*) FROM media");
        using var cancellation = token.Register(command.Cancel);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    });

    public Task<List<ImageRecord>> ReadRecordsAsync(byte[] key) =>
        ExecuteAsync(key, connection => ReadRecordsAsync(connection, "ORDER BY m.rowid", default));

    public Task<List<CatalogImportSource>> ReadImportSourcesAsync(byte[] key) => ExecuteAsync(key, async connection =>
    {
        var sources = new List<CatalogImportSource>();
        using var command = Command(connection, "SELECT id,source_path,source_sha256,original_bytes FROM media");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            sources.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        return sources;
    });

    public Task<ImageRecord> ReadRecordAsync(string id, byte[] key) => ExecuteAsync(key, async connection =>
        await FindRecordAsync(connection, id) ?? throw new InvalidOperationException($"Expected media item '{id}', but found 0."));

    public Task<ImageRecord?> TryReadRecordAsync(string id, byte[] key) => ExecuteAsync(key, connection => FindRecordAsync(connection, id));

    private static async Task<ImageRecord?> FindRecordAsync(DbConnection connection, string id, DbTransaction? transaction = null)
    {
        var records = await ReadRecordsAsync(connection, "WHERE m.id=$id", default, transaction, ("$id", id));
        return records.SingleOrDefault();
    }

    public Task InsertAsync(ImageRecord imageRecord, ImageThumbnail imageThumbnail, byte[] key) => ExecuteAsync(key, async connection =>
    {
        using var transaction = connection.BeginTransaction();
        await WriteRecordAsync(connection, transaction, imageRecord, insert: true);
        await NonQueryAsync(connection, "INSERT INTO media_thumbnails VALUES($id,$bytes)", transaction,
            ("$id", imageRecord.Id), ("$bytes", imageThumbnail.Content.ToArray()));
        transaction.Commit();
    });

    public async Task<ImportCommitResult> CommitImportsAsync(IReadOnlyList<ImportCatalogEntry> importCatalogEntries, byte[] key)
    {
        await Gate.WaitAsync();
        try
        {
            return await ExecuteAsync(key, async connection =>
            {
                using var transaction = connection.BeginTransaction();
                var imported = ImmutableArray.CreateBuilder<string>();
                var duplicates = ImmutableArray.CreateBuilder<string>();
                foreach (var entry in importCatalogEntries)
                {
                    if (await ContainsHashAsync(connection, entry.Record.Sha256, transaction))
                    {
                        duplicates.Add(entry.Record.Id);
                        continue;
                    }
                    await WriteRecordAsync(connection, transaction, entry.Record, insert: true);
                    var thumbnail = VaultCrypto.Decrypt(entry.EncryptedThumbnail.AsSpan(), key, $"thumbnail:{entry.Record.Id}");
                    try
                    {
                        await NonQueryAsync(connection, "INSERT INTO media_thumbnails VALUES($id,$bytes)", transaction,
                            ("$id", entry.Record.Id), ("$bytes", thumbnail));
                    }
                    finally { CryptographicOperations.ZeroMemory(thumbnail); }
                    imported.Add(entry.Record.Id);
                }
                transaction.Commit();
                return new ImportCommitResult(imported.ToImmutable(), duplicates.ToImmutable());
            });
        }
        catch (CatalogStorageException error)
        {
            throw new ImportCatalogWriteException("The prepared import batch could not be committed to the gallery catalog.", error);
        }
        finally { Gate.Release(); }
    }

    public Task UpdateAsync(ImageRecord imageRecord, byte[] key, bool preserveBrowseIndex = true) => ExecuteAsync(key, async connection =>
    {
        using var transaction = connection.BeginTransaction();
        await WriteRecordAsync(connection, transaction, imageRecord, insert: false);
        transaction.Commit();
    });

    public Task DeleteAsync(string id, byte[]? key = null) => ExecuteAsync(key, async connection =>
    {
        RequireOne(await NonQueryAsync(connection, "DELETE FROM media WHERE id=$id", null, ("$id", id)), id);
    });

    private static void RequireOne(int count, string id)
    {
        if (count != 1) throw new InvalidOperationException($"Expected one media mutation for '{id}', but found {count}.");
    }

    public Task<bool> ContainsFingerprintAsync(string contentSha256, byte[] key) =>
        ExecuteAsync(key, connection => ContainsHashAsync(connection, contentSha256));

    private static async Task<bool> ContainsHashAsync(DbConnection connection, string sourceSha256, DbTransaction? transaction = null) =>
        Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM media WHERE source_identity=$hash", transaction,
            ("$hash", NormalizeSourceHash(sourceSha256)))) != 0;

    private static string NormalizeSourceHash(string hash) => Convert.ToHexString(Validators.SourceFingerprintValidator.Decode(hash));
    public Task BackfillFingerprintsAsync(byte[] key) => ExecuteAsync(key, _ => Task.CompletedTask);

    public Task<ImageThumbnail> ReadThumbnailAsync(string id, byte[] key) => ExecuteAsync(key, async connection =>
        new ImageThumbnail(await ReadThumbnailBytesAsync(connection, id)));

    private static async Task<byte[]> ReadThumbnailBytesAsync(DbConnection connection, string id, DbTransaction? transaction = null) =>
        (byte[]?)await ScalarAsync(connection, "SELECT bytes FROM media_thumbnails WHERE media_id=$id", transaction, ("$id", id))
        ?? throw new InvalidOperationException($"Expected thumbnail for '{id}', but found 0.");

    public Task<(ImageRecord Record, ImageThumbnail Thumbnail)> ReadVerificationAsync(string id, byte[] key) => ExecuteAsync(key, async connection =>
    {
        using var transaction = connection.BeginTransaction();
        var record = await FindRecordAsync(connection, id, transaction)
            ?? throw new InvalidOperationException($"Expected media item '{id}', but found 0.");
        var thumbnail = await ReadThumbnailBytesAsync(connection, id, transaction);
        transaction.Commit();
        return (record, new ImageThumbnail(thumbnail));
    });

    public Task<GalleryCatalog?> ReadInitialPageAsync(int pageSize, bool markedOnly, byte[] key, CancellationToken token) =>
        ExecuteAsync<GalleryCatalog?>(key, async connection =>
        {
            token.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            var revision = await ReadRevisionAsync(connection);
            var count = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM media", transaction));
            var complete = count <= pageSize;
            var records = await ReadRecordsAsync(connection,
                (complete ? "" : "WHERE m.is_marked_for_deletion=$marked ") + "ORDER BY m.imported_ticks DESC,m.rowid LIMIT $limit",
                token, transaction, complete
                    ? [("$limit", pageSize)]
                    : [("$marked", markedOnly ? 1L : 0L), ("$limit", pageSize)]);
            transaction.Commit();
            // This is an indexed query, not a hit in the legacy encrypted first-page cache.
            return new GalleryCatalog(records, revision, count, complete, true, false);
        });

    public Task<GalleryCatalog> ReadCatalogSnapshotAsync(byte[] key, CancellationToken token) => ExecuteAsync(key, async connection =>
    {
        using var transaction = connection.BeginTransaction();
        var revision = await ReadRevisionAsync(connection);
        var records = await ReadRecordsAsync(connection, "ORDER BY m.rowid", token, transaction);
        transaction.Commit();
        return new GalleryCatalog(records, revision, records.Count, true, true, false);
    });

    public Task<List<ImageRecord>> ReadBrowseRecordsAsync(IEnumerable<string> ids, byte[] key, CancellationToken token)
    {
        var captured = ids.Distinct(StringComparer.Ordinal).ToArray();
        return ExecuteAsync(key, async connection =>
        {
            using var transaction = connection.BeginTransaction();
            var records = new List<ImageRecord>(captured.Length);
            foreach (var id in captured)
            {
                token.ThrowIfCancellationRequested();
                if (await FindRecordAsync(connection, id, transaction) is { } record) records.Add(record);
            }
            transaction.Commit();
            return records;
        });
    }

    public Task<bool> SaveBrowseIndexAsync(List<ImageRecord> records, long revision, byte[] key, CancellationToken token) =>
        ExecuteAsync(key, async connection => { token.ThrowIfCancellationRequested(); return await ReadRevisionAsync(connection) == revision; });

    public Task<T?> ReadSettingsAsync<T>(string id, string purpose, byte[] key) where T : class => ExecuteAsync(key, async connection =>
    {
        var text = (string?)await ScalarAsync(connection, "SELECT payload FROM settings WHERE id=$id", null, ("$id", id));
        return text is null ? null : JsonSerializer.Deserialize<T>(text);
    });

    public Task SaveSettingsAsync<T>(T value, string id, string purpose, byte[] key) => ExecuteAsync(key, async connection =>
    {
        await NonQueryAsync(connection,
            "INSERT INTO settings(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload", null,
            ("$id", id), ("$payload", JsonSerializer.Serialize(value)));
    });

    public Task DeleteSettingsAsync(byte[] key, params string[] ids) => ExecuteAsync(key, async connection =>
    {
        using var transaction = connection.BeginTransaction();
        foreach (var id in ids) await NonQueryAsync(connection, "DELETE FROM settings WHERE id=$id", transaction, ("$id", id));
        transaction.Commit();
    });

    public async Task<List<SavedView>> LoadViewsAsync(byte[] key) =>
        await ReadSettingsAsync<List<SavedView>>("saved-views", "saved-views:v1", key) ?? [];
    public Task SaveViewAsync(string name, GalleryFilter galleryFilter, byte[] key) =>
        ChangeViewsAsync(views => SavedViewProcessor.Replace(views, name, galleryFilter), key);
    public Task DeleteViewAsync(string name, byte[] key) => ChangeViewsAsync(views => SavedViewProcessor.Remove(views, name), key);
    private async Task ChangeViewsAsync(Action<List<SavedView>> change, byte[] key)
    {
        await Gate.WaitAsync();
        try { var views = await LoadViewsAsync(key); change(views); await SaveSettingsAsync(views, "saved-views", "saved-views:v1", key); }
        finally { Gate.Release(); }
    }
}
