using System.Data.Common;
using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence.Turso;

internal sealed partial class TursoGalleryStore
{
    public Task CommitCollectionCatalogAsync(IReadOnlyList<ImageRecord> records, CollectionCatalog catalog,
        IReadOnlyList<SavedView>? views, byte[] key) =>
        ExecuteAsync(key, connection => CommitCollectionCatalogAsync(connection, records, catalog, views));

    private static async Task CommitCollectionCatalogAsync(DbConnection connection, IReadOnlyList<ImageRecord> records,
        CollectionCatalog catalog, IReadOnlyList<SavedView>? views)
    {
        using var transaction = connection.BeginTransaction();
        await UpdateCollectionRecordsAsync(connection, transaction, records);
        await UpsertSettingAsync(connection, transaction, CollectionCatalog.SettingId, catalog);
        if (views is not null)
            await UpsertSettingAsync(connection, transaction, SavedViewsSettingId, views);
        transaction.Commit();
    }

    private static async Task UpdateCollectionRecordsAsync(DbConnection connection, DbTransaction transaction,
        IReadOnlyList<ImageRecord> records)
    {
        foreach (var record in records)
            await WriteRecordAsync(connection, transaction, record, RecordWriteMode.Update);
    }
}
