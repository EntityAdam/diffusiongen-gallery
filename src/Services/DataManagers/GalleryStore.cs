using Gallery.Services.Processors;
using Gallery.Core;
using Gallery.Services.DataManagers.Processors;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Models;
using Gallery.Services.DataManagers.Models;
using Gallery.Services.DataManagers.CommandFactories;
using Microsoft.Data.Sqlite;

namespace Gallery.Services.DataManagers;

internal sealed class GalleryStore(string databasePath)
{

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
            ?? throw new ThumbnailNotFoundException($"Expected one thumbnail for image '{id}', but found 0.",
                GalleryLogEvent.ReadThumbnail, new ContextualData(System.Collections.Immutable.ImmutableDictionary<string, object>.Empty.Add("Image.Id", id)));
        return new ImageThumbnail(VaultCrypto.Decrypt(encryptedThumbnail, key, $"thumbnail:{id}"));
    }

    public async Task InsertAsync(ImageRecord record, ImageThumbnail thumbnail, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateInsertImage(sqliteConnection, record.Id, EncodeRecord(record, key),
            VaultCrypto.Encrypt(thumbnail.PngBytes, key, $"thumbnail:{record.Id}"),
            VaultCrypto.ContentFingerprint(key, record.Sha256));
        await sqliteCommand.ExecuteNonQueryAsync();
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
                sqliteConnection, sqliteTransaction, id, VaultCrypto.ContentFingerprint(key, DecodeRecord(id, record, key).Sha256));
            await sqliteCommand.ExecuteNonQueryAsync();
        }
    }

    public async Task<bool> ContainsFingerprintAsync(string contentSha256, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateContainsFingerprint(sqliteConnection, VaultCrypto.ContentFingerprint(key, contentSha256));
        return Convert.ToInt64(await sqliteCommand.ExecuteScalarAsync()) != 0;
    }

    public async Task<ImageRecord> ReadRecordAsync(string id, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateReadRecord(sqliteConnection, id);
        var encryptedRecord = (byte[]?)await sqliteCommand.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"Expected one record for image '{id}', but found 0.");
        return DecodeRecord(id, encryptedRecord, key);
    }

    public async Task UpdateAsync(ImageRecord record, byte[] key)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateUpdateImage(sqliteConnection, record.Id, EncodeRecord(record, key));
        var affectedRows = await sqliteCommand.ExecuteNonQueryAsync();
        if (affectedRows != 1)
            throw new InvalidOperationException($"Expected to update one image with id '{record.Id}', but updated {affectedRows}.");
    }

    public async Task DeleteAsync(string id)
    {
        await using var sqliteConnection = await OpenAsync();
        using var sqliteCommand = GalleryStoreCommandFactory.CreateDeleteImage(sqliteConnection, id);
        var affectedRows = await sqliteCommand.ExecuteNonQueryAsync();
        if (affectedRows != 1)
            throw new InvalidOperationException($"Expected to delete one image with id '{id}', but deleted {affectedRows}.");
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
            ?? throw new InvalidOperationException("Invalid image record.");
        if (record.Id != id) throw new CryptographicException("Image record identity mismatch.");
        return record;
    }

    private static byte[] EncodeRecord(ImageRecord record, byte[] key) =>
        EncodeSetting(record, $"record:{record.Id}", key);

    private static T? DecodeSetting<T>(byte[] encryptedSettings, string purpose, byte[] key)
    {
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

}
