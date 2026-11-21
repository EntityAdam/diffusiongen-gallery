using Gallery.Core.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Infrastructure.Persistence.Sqlite;

internal sealed partial class GalleryStore
{
    public async Task CommitCollectionCatalogAsync(IReadOnlyList<ImageRecord> records, CollectionCatalog catalog,
        IReadOnlyList<SavedView>? views, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        await UpdateCatalogRecordsAsync(connection, transaction, records, key);
        await SaveCatalogSettingAsync(connection, transaction, CollectionCatalog.SettingId,
            EncodeSetting(catalog, CollectionCatalog.SettingPurpose, key));
        if (views is not null)
            await SaveCatalogSettingAsync(connection, transaction, SavedViewsId, EncodeSetting(views, SavedViewsPurpose, key));
        transaction.Commit();
    }

    private static async Task UpdateCatalogRecordsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<ImageRecord> records, byte[] key)
    {
        foreach (var record in records)
        {
            using var command = GalleryStoreCommandFactory.CreateUpdateImage(connection, record.Id, EncodeRecord(record, key), transaction);
            RequireSingleImageMutation(await command.ExecuteNonQueryAsync(), record.Id, UpdateAction, UpdatedOutcome);
        }
    }

    private static async Task SaveCatalogSettingAsync(SqliteConnection connection, SqliteTransaction transaction, string id, byte[] payload)
    {
        using var command = GalleryStoreCommandFactory.CreateSaveSettings(connection, id, payload);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync();
    }
}
