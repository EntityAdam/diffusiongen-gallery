using Gallery.Core.Features.Tags;
using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence.Sqlite;

internal sealed partial class GalleryStore
{
    public async Task CommitTagCatalogAsync(IReadOnlyList<ImageRecord> records, TagCatalog catalog,
        IReadOnlyList<SavedView>? views, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        await UpdateCatalogRecordsAsync(connection, transaction, records, key);
        await SaveCatalogSettingAsync(connection, transaction, TagCatalog.SettingId,
            EncodeSetting(catalog, TagCatalog.SettingPurpose, key));
        if (views is not null)
            await SaveCatalogSettingAsync(connection, transaction, SavedViewsId, EncodeSetting(views, SavedViewsPurpose, key));
        transaction.Commit();
    }
}
