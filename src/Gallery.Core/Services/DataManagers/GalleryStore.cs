using Gallery.Core.Services.Processors.Validators;
using Gallery.Core.Services.Processors.Codecs;
using Gallery.Core.Services.Processors;
using Gallery.Core.Exceptions;
using Gallery.Core.Services.DataManagers.Processors;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.DataManagers.CommandFactories;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Services.DataManagers;

internal sealed partial class GalleryStore(string databasePath)
{

    private readonly string databasePath = Path.GetFullPath(databasePath);
    public SemaphoreSlim Gate { get; } = new(1);
    public SemaphoreSlim BackupGate { get; } = new(1);

    public async Task InitializeAsync()
    {
        await using var sqliteConnection = await OpenAsync();
    }

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

    public async Task<long> ReadStorageRevisionAsync()
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadStorageRevision(sqliteConnection);
        return Convert.ToInt64(await sqliteCommand.ExecuteScalarAsync());
    }

    private async Task<SqliteConnection> OpenAsync(bool createIfMissing = true)
    {
        var sqliteConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        try
        {
            await sqliteConnection.OpenAsync();
            await ConfigureDurabilityAsync(sqliteConnection);
            await ConfigureJournalAsync(sqliteConnection);
            await InitializeSchemaAsync(sqliteConnection);
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

    public async Task CreateSnapshotAsync(string destinationPath)
    {
        await using var source = await OpenAsync();
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Pooling = false
        }.ToString());
        await destination.OpenAsync();
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

    private static async Task<SqliteConnection> OpenSnapshotAsync(string snapshotPath)
    {
        var sqliteConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadOnly,
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

    // Acquire the database write reservation before observing schema so independent stores cannot race an upgrade.
    private static async Task InitializeSchemaAsync(SqliteConnection sqliteConnection)
    {
        using var sqliteTransaction = sqliteConnection.BeginTransaction(deferred: false);
        await CreateBaseSchemaAsync(sqliteConnection, sqliteTransaction);
        if (!await HasFingerprintColumnAsync(sqliteConnection, sqliteTransaction))
            await AddFingerprintColumnAsync(sqliteConnection, sqliteTransaction);
        await CreateFingerprintIndexAsync(sqliteConnection, sqliteTransaction);
        sqliteTransaction.Commit();
    }

    private async Task ConfigureJournalAsync(SqliteConnection sqliteConnection)
    {
        using var sqliteCommand = IsNetworkDirectory(Path.GetDirectoryName(databasePath)!)
            ? GalleryStoreCommandFactory.CreateConfigureDeleteJournal(sqliteConnection)
            : GalleryStoreCommandFactory.CreateConfigureWalJournal(sqliteConnection);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private static async Task CreateBaseSchemaAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateInitializeSchema(sqliteConnection, sqliteTransaction);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    internal static bool IsNetworkDirectory(string directory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory))!;
        return root.StartsWith(@"\\", StringComparison.Ordinal)
            || OperatingSystem.IsWindows() && new DriveInfo(root).DriveType == DriveType.Network;
    }

    private static async Task<bool> HasFingerprintColumnAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateCheckFingerprintColumn(sqliteConnection, sqliteTransaction);
        return Convert.ToInt64(await sqliteCommand.ExecuteScalarAsync()) != 0;
    }

    private static async Task AddFingerprintColumnAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateAddFingerprintColumn(sqliteConnection, sqliteTransaction);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    private static async Task CreateFingerprintIndexAsync(SqliteConnection sqliteConnection, SqliteTransaction sqliteTransaction)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateFingerprintIndex(sqliteConnection, sqliteTransaction);
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    public async Task<List<ImageRecord>> ReadRecordsAsync(byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        return await ReadImageRecordsAsync(sqliteConnection, key);
    }

    private static async Task<List<ImageRecord>> ReadImageRecordsAsync(SqliteConnection sqliteConnection, byte[] key)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadImageRecords(sqliteConnection);
        await using var sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
        var imageRecords = new List<ImageRecord>();
        while (await sqliteDataReader.ReadAsync())
        {
            var imageRecord = MapImageRecord(sqliteDataReader);
            imageRecords.Add(DecodeRecord(imageRecord.Id, imageRecord.Record, key));
        }
        return imageRecords;
    }

    private static (string Id, byte[] Record) MapImageRecord(SqliteDataReader sqliteDataReader) =>
        (sqliteDataReader.GetString(0), sqliteDataReader.GetFieldValue<byte[]>(1));

    public async Task<ImageThumbnail> ReadThumbnailAsync(string id, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadThumbnail(sqliteConnection, id);
        var encryptedThumbnail = (byte[]?)await sqliteCommand.ExecuteScalarAsync()
            ?? throw new ThumbnailNotFoundException($"Expected one thumbnail for media item '{id}', but found 0.",
                GalleryLogEvent.ReadThumbnail, new ContextualData(System.Collections.Immutable.ImmutableDictionary<string, object>.Empty.Add("Image.Id", id)));
        VaultEnvelopeValidator.Validate(encryptedThumbnail, $"thumbnail '{id}'");
        return new ImageThumbnail(VaultCrypto.Decrypt(encryptedThumbnail, key, $"thumbnail:{id}"));
    }

    public async Task InsertAsync(ImageRecord record, ImageThumbnail thumbnail, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        await InsertAsync(sqliteConnection, record, thumbnail, key);
    }

    private static async Task InsertAsync(SqliteConnection sqliteConnection, ImageRecord record, ImageThumbnail thumbnail, byte[] key)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateInsertImage(sqliteConnection, record.Id, EncodeRecord(record, key),
            VaultCrypto.Encrypt(thumbnail.Content.Span, key, $"thumbnail:{record.Id}"),
            EncodeContentFingerprint(key, record.Sha256));
        await sqliteCommand.ExecuteNonQueryAsync();
    }

    public async Task<ImportCommitResult> CommitImportsAsync(IReadOnlyList<ImportCatalogEntry> entries, byte[] key)
    {
        await Gate.WaitAsync();
        try
        {
            await using var sqliteConnection = await OpenAsync();
            using var sqliteTransaction = sqliteConnection.BeginTransaction();
            var imported = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>();
            var duplicates = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>();
            foreach (var entry in entries)
            {
                if (await ContainsFingerprintAsync(sqliteConnection, entry.Record.Sha256, key, sqliteTransaction))
                {
                    duplicates.Add(entry.Record.Id);
                    continue;
                }
                using var sqliteCommand = GalleryStoreCommandFactory.CreateInsertImage(sqliteConnection, entry.Record.Id,
                    EncodeRecord(entry.Record, key), entry.EncryptedThumbnail.ToArray(),
                    EncodeContentFingerprint(key, entry.Record.Sha256));
                sqliteCommand.Transaction = sqliteTransaction;
                await sqliteCommand.ExecuteNonQueryAsync();
                imported.Add(entry.Record.Id);
            }
            sqliteTransaction.Commit();
            return new(imported.ToImmutable(), duplicates.ToImmutable());
        }
        catch (SqliteException exception)
        {
            throw new ImportCatalogWriteException("The prepared import batch could not be committed to the gallery catalog.", exception);
        }
        finally { Gate.Release(); }
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
            throw new InvalidOperationException($"Expected one media item for verification '{id}', but found 0.");
        var record = DecodeRecord(id, sqliteDataReader.GetFieldValue<byte[]>(0), key);
        var encryptedThumbnail = sqliteDataReader.GetFieldValue<byte[]>(1);
        VaultEnvelopeValidator.Validate(encryptedThumbnail, $"verification thumbnail '{id}'");
        return (record, new ImageThumbnail(VaultCrypto.Decrypt(encryptedThumbnail, key, $"thumbnail:{id}")));
    }

    // Caller holds Gate so backfill and ingestion cannot interleave in this process.
    public Task BackfillFingerprintsAsync(byte[] key) =>
        BackfillInTransactionAsync(key);

    private async Task BackfillInTransactionAsync(byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteTransaction = sqliteConnection.BeginTransaction();
        var recordsMissingFingerprints = await ReadRecordsMissingFingerprintsAsync(sqliteConnection, sqliteTransaction);
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

    public async Task<bool> ContainsFingerprintAsync(string contentSha256, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        return await ContainsFingerprintAsync(sqliteConnection, contentSha256, key);
    }

    private static async Task<bool> ContainsFingerprintAsync(SqliteConnection sqliteConnection, string contentSha256, byte[] key,
        SqliteTransaction? sqliteTransaction = null)
    {
        using var sqliteCommand = GalleryStoreCommandFactory.CreateContainsFingerprint(sqliteConnection, EncodeContentFingerprint(key, contentSha256));
        sqliteCommand.Transaction = sqliteTransaction;
        return Convert.ToInt64(await sqliteCommand.ExecuteScalarAsync()) != 0;
    }

    public async Task<ImageRecord> ReadRecordAsync(string id, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadRecord(sqliteConnection, id);
        var encryptedRecord = (byte[]?)await sqliteCommand.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"Expected one record for media item '{id}', but found 0.");
        return DecodeRecord(id, encryptedRecord, key);
    }

    public async Task UpdateAsync(ImageRecord record, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateUpdateImage(sqliteConnection, record.Id, EncodeRecord(record, key));
        var affectedRows = await sqliteCommand.ExecuteNonQueryAsync();
        RequireSingleImageMutation(affectedRows, record.Id, "update", "updated");
    }

    public async Task DeleteAsync(string id)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateDeleteImage(sqliteConnection, id);
        var affectedRows = await sqliteCommand.ExecuteNonQueryAsync();
        RequireSingleImageMutation(affectedRows, id, "delete", "deleted");
    }

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

    public Task SaveViewAsync(string name, GalleryFilter filter, byte[] key) =>
        ChangeViewsUnderGateAsync(views => SavedViewProcessor.Replace(views, name, filter), key);

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
        var record = DecodeSetting<ImageRecord>(encryptedRecord, $"record:{id}", key)
            ?? throw new InvalidOperationException("Invalid media record.");
        if (record.Id != id) throw new CryptographicException("Media record identity mismatch.");
        return record;
    }

    private static byte[] EncodeRecord(ImageRecord record, byte[] key) =>
        EncodeSetting(record, $"record:{record.Id}", key);

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
        VaultCrypto.ContentFingerprint(key, Validators.SourceFingerprintValidator.Decode(sourceSha256));

}
