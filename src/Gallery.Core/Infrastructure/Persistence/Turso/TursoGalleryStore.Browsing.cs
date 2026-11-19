using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence.Turso;

internal sealed partial class TursoGalleryStore
{
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

    public Task<long?> RefreshGenerationMetadataAsync(GalleryCatalog sourceSnapshot, IReadOnlyList<ImageRecord> refreshedRecords,
        byte[] key, CancellationToken token) => ExecuteAsync<long?>(key, async connection =>
    {
        token.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        if (await ReadRevisionAsync(connection, transaction) != sourceSnapshot.Revision) return null;
        foreach (var record in refreshedRecords)
        {
            token.ThrowIfCancellationRequested();
            await WriteRecordAsync(connection, transaction, record, insert: false);
        }
        var revision = await ReadRevisionAsync(connection, transaction);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return revision;
    });
}
