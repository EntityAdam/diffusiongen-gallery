using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence.Sqlite;

internal sealed partial class GalleryStore
{
    public async Task CommitCollectionCatalogAsync(IReadOnlyList<ImageRecord> records, CollectionCatalog catalog,
        IReadOnlyList<SavedView>? views, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        foreach (var record in records)
            await WriteUpdatedRecordAsync(connection, transaction, record, key, CancellationToken.None);
        using (var command = GalleryStoreCommandFactory.CreateSaveSettings(connection, CollectionCatalog.SettingId,
            EncodeSetting(catalog, CollectionCatalog.SettingPurpose, key)))
        {
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync();
        }
        if (views is not null)
        {
            using var command = GalleryStoreCommandFactory.CreateSaveSettings(connection, SavedViewsId,
                EncodeSetting(views, SavedViewsPurpose, key));
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }
}
