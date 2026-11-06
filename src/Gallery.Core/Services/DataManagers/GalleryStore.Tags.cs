using Gallery.Core.Models;

namespace Gallery.Core.Services.DataManagers;

internal sealed partial class GalleryStore
{
    public async Task CommitTagCatalogAsync(IReadOnlyList<ImageRecord> records, TagCatalog catalog,
        IReadOnlyList<SavedView>? views, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        foreach (var record in records)
        {
            using var command = CommandFactories.GalleryStoreCommandFactory.CreateUpdateImage(connection, record.Id, EncodeRecord(record, key), transaction);
            RequireSingleImageMutation(await command.ExecuteNonQueryAsync(), record.Id, UpdateAction, UpdatedOutcome);
        }
        using (var command = CommandFactories.GalleryStoreCommandFactory.CreateSaveSettings(connection, TagCatalog.SettingId,
            EncodeSetting(catalog, TagCatalog.SettingPurpose, key)))
        {
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync();
        }
        if (views is not null)
        {
            using var command = CommandFactories.GalleryStoreCommandFactory.CreateSaveSettings(connection, SavedViewsId,
                EncodeSetting(views, SavedViewsPurpose, key));
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }
}
