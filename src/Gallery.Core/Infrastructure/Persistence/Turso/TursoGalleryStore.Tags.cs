using System.Data.Common;
using Gallery.Core.Features.Tags;
using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence.Turso;

internal sealed partial class TursoGalleryStore
{
    public Task CommitTagCatalogAsync(IReadOnlyList<ImageRecord> records, TagCatalog catalog,
        IReadOnlyList<SavedView>? views, byte[] key) =>
        ExecuteAsync(key, connection => CommitTagCatalogAsync(connection, records, catalog, views));

    private static async Task CommitTagCatalogAsync(DbConnection connection, IReadOnlyList<ImageRecord> records,
        TagCatalog catalog, IReadOnlyList<SavedView>? views)
    {
        using var transaction = connection.BeginTransaction();
        var existingRecords = await ReadBrowseRecordsAsync(connection, transaction,
            records.Select(record => record.Id).Distinct(StringComparer.Ordinal).ToArray(), CancellationToken.None);
        var existingById = existingRecords.ToDictionary(record => record.Id, StringComparer.Ordinal);
        foreach (var record in records)
        {
            await WriteMediaRowAsync(connection, transaction, record, RecordWriteMode.Update);
            await UpsertMediaMetadataAsync(connection, transaction, record);
            if (!existingById.TryGetValue(record.Id, out var existing)
                || !existing.Collections.SequenceEqual(record.Collections))
                await ReplaceCollectionsAsync(connection, transaction, record);
            if (existing is null || existing.Generation != record.Generation)
                await ReplaceGenerationAsync(connection, transaction, record);
            existingById[record.Id] = record;
        }
        await UpsertSettingAsync(connection, transaction, TagCatalog.SettingId, catalog);
        if (views is not null)
            await UpsertSettingAsync(connection, transaction, SavedViewsSettingId, views);
        transaction.Commit();
    }
}
