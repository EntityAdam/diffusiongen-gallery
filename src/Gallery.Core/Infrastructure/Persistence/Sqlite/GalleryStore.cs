using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Models;
using Gallery.Core.Shared.Diagnostics;
using Gallery.Migrations;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Infrastructure.Persistence.Sqlite;

internal sealed partial class GalleryStore(string databasePath) : IGalleryStore
{
    private const string GalleryLoadOperationName = "gallery.load";
    private const string InvalidMediaRecordMessage = "Invalid media record.";
    private const string MediaRecordIdentityMismatchMessage = "Media record identity mismatch.";

    private volatile bool catalogInitialized;

    private readonly string databasePath = Path.GetFullPath(databasePath);
    public SemaphoreSlim Gate { get; } = new(1);
    public SemaphoreSlim BackupGate { get; } = new(1);

    public Task InitializeAsync() => InitializeAsync(null);

    public async Task InitializeAsync(byte[]? key)
    {
        await using var sqliteConnection = await OpenAsync();
    }

    public Task DeleteSettingsAsync(byte[] key, params string[] ids) => DeleteSettingsAsync(ids);

    public async Task DeleteSettingsAsync(params string[] ids)
    {
        await using var sqliteConnection = await OpenAsync();
        await DeleteSettingsAsync(sqliteConnection, ids);
    }

    private static async Task DeleteSettingsAsync(SqliteConnection sqliteConnection, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            using var sqliteCommand = GalleryStoreCommandFactory.CreateDeleteSettings(sqliteConnection, id);
            await sqliteCommand.ExecuteNonQueryAsync();
        }
    }

    public async Task<long> ReadStorageRevisionAsync(byte[]? key = null)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadStorageRevision(sqliteConnection);
        return await ReadRequiredStorageRevisionAsync(sqliteCommand, CancellationToken.None);
    }

    private static async Task<long> ReadRequiredStorageRevisionAsync(SqliteCommand sqliteCommand, CancellationToken token)
    {
        var result = await sqliteCommand.ExecuteScalarAsync(token);
        if (result is null or DBNull)
        {
            var exception = new InvalidOperationException("Expected one storage revision row for singleton id 1, but found 0.");
            exception.Data["CommandIdentity"] = "GalleryStoreCommandFactory.CreateReadStorageRevision";
            exception.Data["ObservedRowCount"] = 0;
            throw exception;
        }
        return Convert.ToInt64(result);
    }

    private Task<SqliteConnection> OpenAsync() => OpenAsync(SqliteOpenMode.ReadWriteCreate);

    private Task<SqliteConnection> OpenExistingAsync() => OpenAsync(SqliteOpenMode.ReadWrite);

    private async Task<SqliteConnection> OpenAsync(SqliteOpenMode sqliteOpenMode)
    {
        var openStarted = CatalogMetrics.ConnectionDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        var sqliteConnection = await OpenConnectionAsync(databasePath, sqliteOpenMode);
        CatalogMetrics.ConnectionOpens.Add(1);
        try
        {
            await ConfigureDurabilityAsync(sqliteConnection);
            await ConfigureJournalAsync(sqliteConnection);
            if (openStarted != 0) CatalogMetrics.ConnectionDuration.Record(Stopwatch.GetElapsedTime(openStarted).TotalMilliseconds);
            var migrationStarted = CatalogMetrics.MigrationDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
            try
            {
                GalleryDatabase.Upgrade(sqliteConnection);
                catalogInitialized = true;
            }
            finally
            {
                if (migrationStarted != 0) CatalogMetrics.MigrationDuration.Record(Stopwatch.GetElapsedTime(migrationStarted).TotalMilliseconds);
            }
            return sqliteConnection;
        }
        catch
        {
            await sqliteConnection.DisposeAsync();
            throw;
        }
    }

    private static async Task ConfigureDurabilityAsync(SqliteConnection sqliteConnection)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateConfigureDurability(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string dataSource, SqliteOpenMode sqliteOpenMode)
    {
        var sqliteConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = sqliteOpenMode,
            Pooling = false
        }.ToString());
        try
        {
            await sqliteConnection.OpenAsync();
            return sqliteConnection;
        }
        catch
        {
            await sqliteConnection.DisposeAsync();
            throw;
        }
    }

    private async Task ConfigureJournalAsync(SqliteConnection sqliteConnection)
    {
        using var sqliteCommand = IsNetworkDirectory(Path.GetDirectoryName(databasePath)!)
            ? GalleryStoreCommandFactory.CreateConfigureDeleteJournal(sqliteConnection)
            : GalleryStoreCommandFactory.CreateConfigureWalJournal(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    internal static bool IsNetworkDirectory(string directory) => CatalogDirectoryRules.IsNetworkDirectory(directory);

    public async Task<long> CountRecordsAsync(byte[] key, CancellationToken token = default)
    {
        await using var connection = await OpenExistingAsync();
        return await ReadRecordCountAsync(connection, token);
    }

    private static async Task<long> ReadRecordCountAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command = GalleryStoreCommandFactory.CreateCountRecords(connection);
        using var cancellation = token.Register(command.Cancel);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    public async Task<List<ImageRecord>> ReadRecordsAsync(byte[] key)
    {
        var activity = Activity.Current is { OperationName: GalleryLoadOperationName, IsAllDataRequested: true } load ? load : null;
        var openStarted = VaultOpenTelemetry.Start(activity);
        await using var sqliteConnection = await OpenAsync();
        VaultOpenTelemetry.Record(activity, "gallery.load.connection_open_ms", openStarted);
        return await ReadImageRecordsAsync(sqliteConnection, key, activity);
    }

    private static async Task<List<ImageRecord>> ReadImageRecordsAsync(SqliteConnection sqliteConnection, byte[] key, Activity? activity,
        CancellationToken token = default, SqliteTransaction? transaction = null)
    {
        var readStarted = VaultOpenTelemetry.Start(activity);
        var pauseStarted = activity is null ? TimeSpan.Zero : GC.GetTotalPauseDuration();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadImageRecords(sqliteConnection);
        sqliteCommand.Transaction = transaction;
        using var cancellation = token.Register(sqliteCommand.Cancel);
        var queryStarted = VaultOpenTelemetry.Start(activity);
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync(token);
        if (activity is null)
            return await ReadUnmeasuredImageRecordsAsync(sqliteDataReader, key, token);

        return await ReadMeasuredImageRecordsAsync(sqliteDataReader, key, activity, readStarted, pauseStarted, queryStarted, token);
    }

    private static async Task<List<ImageRecord>> ReadUnmeasuredImageRecordsAsync(
        SqliteDataReader sqliteDataReader, byte[] key, CancellationToken token)
    {
        // Leave the ordinary application path free of per-row stopwatch work.
        var imageRecords = new List<ImageRecord>();
        while (await sqliteDataReader.ReadAsync(token))
        {
            token.ThrowIfCancellationRequested();
            var imageRecord = MapImageRecord(sqliteDataReader);
            imageRecords.Add(DecodeRecord(imageRecord.Id, imageRecord.Record, key));
        }
        return imageRecords;
    }

    private static async Task<List<ImageRecord>> ReadMeasuredImageRecordsAsync(
        SqliteDataReader sqliteDataReader, byte[] key, Activity activity, long readStarted,
        TimeSpan pauseStarted, long queryStarted, CancellationToken token)
    {
        var imageRecords = new List<ImageRecord>();
        double decryptMs = 0, deserializeMs = 0, materializeMs = 0;
        var queryMs = VaultOpenTelemetry.Elapsed(queryStarted);
        long recordBytes = 0;
        while (true)
        {
            var stepStarted = Stopwatch.GetTimestamp();
            token.ThrowIfCancellationRequested();
            var hasRow = await sqliteDataReader.ReadAsync(token);
            queryMs += Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds;
            if (!hasRow) break;
            var materializeStarted = Stopwatch.GetTimestamp();
            var imageRecord = MapImageRecord(sqliteDataReader);
            materializeMs += Stopwatch.GetElapsedTime(materializeStarted).TotalMilliseconds;
            recordBytes += imageRecord.Record.Length;
            imageRecords.Add(DecodeMeasuredRecord(imageRecord.Id, imageRecord.Record, key, ref decryptMs, ref deserializeMs));
        }
        var residualMs = Math.Max(0, VaultOpenTelemetry.Elapsed(readStarted) - decryptMs - deserializeMs);
        activity.SetTag("gallery.load.record_count", imageRecords.Count);
        activity.SetTag("gallery.load.record_bytes", recordBytes);
        activity.SetTag("gallery.load.decrypt_ms", decryptMs);
        activity.SetTag("gallery.load.deserialize_ms", deserializeMs);
        // Keep the legacy residual, but expose its major components rather than calling it pure I/O.
        activity.SetTag("gallery.load.query_read_ms", residualMs);
        activity.SetTag("gallery.load.query_step_ms", queryMs);
        activity.SetTag("gallery.load.blob_materialize_ms", materializeMs);
        activity.SetTag("gallery.load.other_loop_ms", Math.Max(0, residualMs - queryMs - materializeMs));
        activity.SetTag("gallery.load.gc_pause_ms", (GC.GetTotalPauseDuration() - pauseStarted).TotalMilliseconds);
        return imageRecords;
    }

    private static ImageRecord DecodeMeasuredRecord(string id, byte[] encryptedRecord, byte[] key,
        ref double decryptMs, ref double deserializeMs)
    {
        VaultEnvelopeValidator.Validate(encryptedRecord, $"stored value 'record:{id}'");
        var started = Stopwatch.GetTimestamp();
        var plain = VaultCrypto.Decrypt(encryptedRecord, key, $"record:{id}");
        decryptMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        try
        {
            started = Stopwatch.GetTimestamp();
            var record = JsonSerializer.Deserialize<ImageRecord>(plain)
                ?? throw new InvalidOperationException(InvalidMediaRecordMessage);
            deserializeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return RequireRecordIdentity(record, id);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static (string Id, byte[] Record) MapImageRecord(SqliteDataReader sqliteDataReader) =>
        (sqliteDataReader.GetString(0), sqliteDataReader.GetFieldValue<byte[]>(1));

    public Task<ImageThumbnail> ReadThumbnailAsync(string id, byte[] key) =>
        ReadThumbnailAsync(id, key, OpenAsync);

    public Task<ImageThumbnail> ReadBrowsingThumbnailAsync(string id, byte[] key) =>
        ReadThumbnailAsync(id, key, OpenThumbnailConnectionAsync);

    private async Task<SqliteConnection> OpenThumbnailConnectionAsync()
    {
        // An initialized catalog already has its schema and journal mode. A thumbnail lookup only reads.
        if (!catalogInitialized) return await OpenAsync();
        var started = CatalogMetrics.ConnectionDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        var connection = await OpenConnectionAsync(databasePath, SqliteOpenMode.ReadOnly);
        CatalogMetrics.ConnectionOpens.Add(1);
        if (started != 0) CatalogMetrics.ConnectionDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return connection;
    }

    private async Task<ImageThumbnail> ReadThumbnailAsync(string id, byte[] key, Func<Task<SqliteConnection>> open)
    {
        var started = CatalogMetrics.ThumbnailDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            await using var sqliteConnection = await open();
            using var sqliteCommand = GalleryStoreCommandFactory.CreateReadThumbnail(sqliteConnection, id);
            var encryptedThumbnail = await ReadRequiredEncryptedThumbnailAsync(sqliteCommand, id);
            CatalogMetrics.ThumbnailBytes.Add(encryptedThumbnail.Length);
            return DecodeThumbnail(encryptedThumbnail, key, id, $"thumbnail '{id}'");
        }
        finally
        {
            if (started != 0) CatalogMetrics.ThumbnailDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static async Task<byte[]> ReadRequiredEncryptedThumbnailAsync(SqliteCommand sqliteCommand, string id) =>
        (byte[]?)await sqliteCommand.ExecuteScalarAsync()
            ?? throw new ThumbnailNotFoundException($"Expected one thumbnail for media item '{id}', but found 0.",
                LogEventGallery.ReadThumbnail, new ContextualData(System.Collections.Immutable.ImmutableDictionary<string, object>.Empty.Add("Image.Id", id)));

    private static ImageThumbnail DecodeThumbnail(byte[] encryptedThumbnail, byte[] key, string id, string envelopeDescription)
    {
        VaultEnvelopeValidator.Validate(encryptedThumbnail, envelopeDescription);
        return new ImageThumbnail(VaultCrypto.Decrypt(encryptedThumbnail, key, $"thumbnail:{id}"));
    }

    public async Task InsertAsync(ImageRecord imageRecord, ImageThumbnail imageThumbnail, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        await InsertAsync(sqliteConnection, imageRecord, imageThumbnail, key);
    }

    private static async Task InsertAsync(SqliteConnection sqliteConnection, ImageRecord imageRecord, ImageThumbnail imageThumbnail, byte[] key)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateInsertImage(sqliteConnection, imageRecord.Id, EncodeRecord(imageRecord, key),
            VaultCrypto.Encrypt(imageThumbnail.Content.Span, key, $"thumbnail:{imageRecord.Id}"),
            EncodeContentFingerprint(key, imageRecord.Sha256));
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    public async Task<ImportCommitResult> CommitImportsAsync(IReadOnlyList<ImportCatalogEntry> importCatalogEntries, byte[] key)
    {
        await Gate.WaitAsync();
        try
        {
            return await CommitImportBatchAsync(importCatalogEntries, key);
        }
        catch (SqliteException sqliteException)
        {
            throw new ImportCatalogWriteException("The prepared import batch could not be committed to the gallery catalog.", sqliteException);
        }
        finally { Gate.Release(); }
    }

    private async Task<ImportCommitResult> CommitImportBatchAsync(IReadOnlyList<ImportCatalogEntry> importCatalogEntries, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        var imported = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>();
        var duplicates = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>();
        foreach (var importCatalogEntry in importCatalogEntries)
        {
            if (await ContainsFingerprintAsync(sqliteConnection, importCatalogEntry.Record.Sha256, key, sqliteTransaction))
            {
                duplicates.Add(importCatalogEntry.Record.Id);
                continue;
            }
            await InsertImportCatalogEntryAsync(sqliteConnection, sqliteTransaction, importCatalogEntry, key);
            imported.Add(importCatalogEntry.Record.Id);
        }
        sqliteTransaction.Commit();
        return new(imported.ToImmutable(), duplicates.ToImmutable());
    }

    private static async Task InsertImportCatalogEntryAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction,
        ImportCatalogEntry importCatalogEntry, byte[] key)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateInsertImage(sqliteConnection, importCatalogEntry.Record.Id,
            EncodeRecord(importCatalogEntry.Record, key), importCatalogEntry.EncryptedThumbnail.ToArray(),
            EncodeContentFingerprint(key, importCatalogEntry.Record.Sha256));
        sqliteCommand.Transaction = sqliteTransaction;
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    public async Task<(ImageRecord Record, ImageThumbnail Thumbnail)> ReadVerificationAsync(string id, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadVerification(sqliteConnection, id);
        return await ReadVerifiedImageAsync(sqliteCommand, id, key);
    }

    private static async Task<(ImageRecord Record, ImageThumbnail Thumbnail)> ReadVerifiedImageAsync(
        SqliteCommand sqliteCommand, string id, byte[] key)
    {
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        if (!await sqliteDataReader.ReadAsync())
        {
            var exception = new InvalidOperationException($"Expected one media item for verification '{id}', but found 0.");
            exception.Data["CommandIdentity"] = "GalleryStoreCommandFactory.CreateReadVerification";
            exception.Data["ObservedRowCount"] = 0;
            throw exception;
        }
        var imageRecord = DecodeRecord(id, sqliteDataReader.GetFieldValue<byte[]>(0), key);
        var encryptedThumbnail = sqliteDataReader.GetFieldValue<byte[]>(1);
        return (imageRecord, DecodeThumbnail(encryptedThumbnail, key, id, $"verification thumbnail '{id}'"));
    }

    // Caller holds Gate so backfill and ingestion cannot interleave in this process.
    public Task BackfillFingerprintsAsync(byte[] key) =>
        BackfillInTransactionAsync(key);

    private async Task BackfillInTransactionAsync(byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        var recordsMissingFingerprints = await ReadRecordsMissingFingerprintsAsync(sqliteConnection, sqliteTransaction);
        if (Activity.Current is { OperationName: GalleryLoadOperationName } activity)
            activity.SetTag("gallery.load.backfill_fingerprint_count", recordsMissingFingerprints.Count);
        await UpdateMissingFingerprintsAsync(sqliteConnection, sqliteTransaction, recordsMissingFingerprints, key);
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
        byte[] key)
    {
        string id;
        byte[] record;
        foreach (var imageRecord in recordsMissingFingerprints)
        {
            id = imageRecord.Id;
            record = imageRecord.Record;
            using var sqliteCommand = GalleryStoreCommandFactory.CreateUpdateFingerprint(
                sqliteConnection, sqliteTransaction, id, EncodeContentFingerprint(key, DecodeRecord(id, record, key).Sha256));
            await sqliteCommand.ExecuteNonQueryAsync();
        }
    }

    public async Task<bool> ContainsFingerprintAsync(string sourceSha256, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        return await ContainsFingerprintAsync(sqliteConnection, sourceSha256, key);
    }

    private static async Task<bool> ContainsFingerprintAsync(SqliteConnection sqliteConnection, string sourceSha256, byte[] key,
        SqliteTransaction? sqliteTransaction = null)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateContainsFingerprint(sqliteConnection, EncodeContentFingerprint(key, sourceSha256));
        sqliteCommand.Transaction = sqliteTransaction;
        return Convert.ToInt64(await sqliteCommand.ExecuteScalarAsync()) != 0;
    }

    public async Task<ImageRecord> ReadRecordAsync(string id, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadRecord(sqliteConnection, id);
        var encryptedRecord = await ReadRequiredEncryptedRecordAsync(sqliteCommand, id);
        return DecodeRecord(id, encryptedRecord, key);
    }

    private static async Task<byte[]> ReadRequiredEncryptedRecordAsync(SqliteCommand sqliteCommand, string id) =>
        (byte[]?)await sqliteCommand.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"Expected one record for media item '{id}', but found 0.");

    public async Task UpdateAsync(ImageRecord imageRecord, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var read = GalleryStoreCommandFactory.CreateReadRecord(connection, imageRecord.Id);
        read.Transaction = transaction;
        var previous = DecodeRecord(imageRecord.Id, await ReadRequiredEncryptedRecordAsync(read, imageRecord.Id), key);
        await WriteUpdatedRecordAsync(connection, transaction, imageRecord, key, CancellationToken.None);
        await PreserveBrowseIndexAsync(connection, transaction, previous, imageRecord, key, CancellationToken.None);
        transaction.Commit();
    }

    public async Task UpdateRecordsAsync(IReadOnlyList<ImageRecord> imageRecords, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        foreach (var imageRecord in imageRecords)
        {
            using var read = GalleryStoreCommandFactory.CreateReadRecord(connection, imageRecord.Id);
            read.Transaction = transaction;
            var previous = DecodeRecord(imageRecord.Id, await ReadRequiredEncryptedRecordAsync(read, imageRecord.Id), key);
            await WriteUpdatedRecordAsync(connection, transaction, imageRecord, key, CancellationToken.None);
            await PreserveBrowseIndexAsync(connection, transaction, previous, imageRecord, key, CancellationToken.None);
        }
        transaction.Commit();
    }

    private static async Task WriteUpdatedRecordAsync(SqliteConnection connection, SqliteTransaction transaction,
        ImageRecord record, byte[] key, CancellationToken token)
    {
        using var command = GalleryStoreCommandFactory.CreateUpdateImage(connection, record.Id, EncodeRecord(record, key));
        command.Transaction = transaction;
        using var cancellation = token.Register(command.Cancel);
        var affectedRows = await command.ExecuteNonQueryAsync(token);
        RequireSingleImageMutation(affectedRows, record.Id, UpdateAction, UpdatedOutcome);
    }

    public async Task DeleteAsync(string id, byte[]? key = null)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateDeleteImage(sqliteConnection, id);
        var affectedRows = await sqliteCommand.ExecuteNonQueryAsync();
        RequireSingleImageMutation(affectedRows, id, DeleteAction, DeletedOutcome);
    }

    private const string UpdateAction = "update";
    private const string UpdatedOutcome = "updated";
    private const string DeleteAction = "delete";
    private const string DeletedOutcome = "deleted";

    private static void RequireSingleImageMutation(int affectedRows, string id, string action, string outcome)
    {
        if (affectedRows != 1)
            throw new InvalidOperationException($"Expected to {action} one media item with id '{id}', but {outcome} {affectedRows}.");
    }

    public async Task<T?> ReadSettingsAsync<T>(string id, string purpose, byte[] key) where T : class
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadSettings(sqliteConnection, id);
        var encryptedSettings = (byte[]?)await sqliteCommand.ExecuteScalarAsync();
        return encryptedSettings is null ? null : DecodeSetting<T>(encryptedSettings, purpose, key);
    }

    public async Task SaveSettingsAsync<T>(T value, string id, string purpose, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateSaveSettings(sqliteConnection, id, EncodeSetting(value, purpose, key));
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private const string SavedViewsId = "saved-views";
    private const string SavedViewsPurpose = "saved-views:v1";

    public async Task<List<SavedView>> LoadViewsAsync(byte[] key) =>
        await ReadSettingsAsync<List<SavedView>>(SavedViewsId, SavedViewsPurpose, key) ?? [];

    public Task SaveViewAsync(string name, GalleryFilter galleryFilter, byte[] key) =>
        ChangeViewsUnderGateAsync(views => SavedViewProcessor.Replace(views, name, galleryFilter), key);

    public Task DeleteViewAsync(string name, byte[] key) =>
        ChangeViewsUnderGateAsync(views => SavedViewProcessor.Remove(views, name), key);

    private async Task ChangeViewsUnderGateAsync(Action<List<SavedView>> change, byte[] key)
    {
        await Gate.WaitAsync();
        try
        {
            var views = await LoadViewsAsync(key);
            change(views);
            await SaveSettingsAsync(views, SavedViewsId, SavedViewsPurpose, key);
        }
        finally { Gate.Release(); }
    }

    private static ImageRecord DecodeRecord(string id, byte[] encryptedRecord, byte[] key)
    {
        var imageRecord = DecodeSetting<ImageRecord>(encryptedRecord, $"record:{id}", key)
            ?? throw new InvalidOperationException(InvalidMediaRecordMessage);
        return RequireRecordIdentity(imageRecord, id);
    }

    private static ImageRecord RequireRecordIdentity(ImageRecord imageRecord, string id)
    {
        if (imageRecord.Id != id) throw new CryptographicException(MediaRecordIdentityMismatchMessage);
        return imageRecord;
    }

    private static byte[] EncodeRecord(ImageRecord imageRecord, byte[] key) =>
        EncodeSetting(imageRecord, $"record:{imageRecord.Id}", key);

    private static T? DecodeSetting<T>(byte[] encryptedSettings, string purpose, byte[] key)
    {
        VaultEnvelopeValidator.Validate(encryptedSettings, $"stored value '{purpose}'");
        var plain = VaultCrypto.Decrypt(encryptedSettings, key, purpose);
        try { return JsonSerializer.Deserialize<T>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] EncodeSetting<T>(T value, string purpose, byte[] key)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try { return VaultCrypto.Encrypt(plain, key, purpose); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] EncodeContentFingerprint(byte[] key, string sourceSha256) =>
        VaultCrypto.ContentFingerprint(key, SourceFingerprintValidator.Decode(sourceSha256));

}
