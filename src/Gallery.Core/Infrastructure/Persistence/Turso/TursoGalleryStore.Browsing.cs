using System.Data.Common;
using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence.Turso;

internal sealed partial class TursoGalleryStore
{
    public Task<GalleryCatalog?> ReadInitialPageAsync(int pageSize, InitialPageMode mode, byte[] key, CancellationToken token) =>
        ExecuteAsync<GalleryCatalog?>(key,
            connection => ReadInitialPageAsync(connection, pageSize, mode, token),
            cancellationToken: token);

    private static async Task<GalleryCatalog?> ReadInitialPageAsync(DbConnection connection, int pageSize,
        InitialPageMode mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        var revision = await ReadRevisionAsync(connection);
        var count = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM media", transaction));
        var catalogFitsOnOnePage = count <= pageSize;
        var records = await ReadRecordsAsync(connection,
            (catalogFitsOnOnePage ? "" : "WHERE m.is_marked_for_deletion=$marked ") +
            "ORDER BY m.imported_ticks DESC,m.rowid LIMIT $limit",
            token, transaction, catalogFitsOnOnePage
                ? [("$limit", pageSize)]
                : [("$marked", mode == InitialPageMode.Marked ? 1L : 0L), ("$limit", pageSize)]);
        transaction.Commit();
        // This is an indexed query, not a hit in the legacy encrypted first-page cache.
        return new GalleryCatalog(records, revision, count, catalogFitsOnOnePage, true, false);
    }

    public Task<GalleryCatalog> ReadCatalogSnapshotAsync(byte[] key, CancellationToken token) => ExecuteAsync(key, async connection =>
    {
        using var transaction = connection.BeginTransaction();
        var revision = await ReadRevisionAsync(connection);
        var records = await ReadRecordsAsync(connection, "ORDER BY m.rowid", token, transaction);
        transaction.Commit();
        return new GalleryCatalog(records, revision, records.Count, true, true, false);
    }, cancellationToken: token);

    public Task<List<ImageRecord>> ReadBrowseRecordsAsync(IEnumerable<string> ids, byte[] key, CancellationToken token)
    {
        var captured = ids.Distinct(StringComparer.Ordinal).ToArray();
        return ExecuteAsync(key, connection => ReadBrowseRecordsAsync(connection, captured, token),
            cancellationToken: token);
    }

    public Task<long?> RefreshGenerationMetadataAsync(GalleryCatalog sourceSnapshot, IReadOnlyList<ImageRecord> refreshedRecords,
        byte[] key, CancellationToken token) =>
        ExecuteAsync<long?>(key,
            connection => RefreshGenerationMetadataAsync(connection, sourceSnapshot, refreshedRecords, token),
            cancellationToken: token);

    private static async Task<List<ImageRecord>> ReadBrowseRecordsAsync(DbConnection connection,
        IReadOnlyList<string> requestedIds, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        var records = await ReadBrowseRecordsAsync(connection, transaction, requestedIds, token);
        transaction.Commit();
        return records;
    }

    private static async Task<List<ImageRecord>> ReadBrowseRecordsAsync(DbConnection connection, DbTransaction transaction,
        IReadOnlyList<string> requestedIds, CancellationToken token)
    {
        var recordsById = new Dictionary<string, ImageRecord>(StringComparer.Ordinal);
        for (var offset = 0; offset < requestedIds.Count; offset += CollectionMembershipBatchSize)
        {
            token.ThrowIfCancellationRequested();
            var batchIds = requestedIds.Skip(offset).Take(CollectionMembershipBatchSize).ToArray();
            var parameters = batchIds.Select((id, index) => ("$id" + index, (object?)id)).ToArray();
            var parameterNames = string.Join(',', batchIds.Select((_, index) => "$id" + index));
            var records = await ReadRecordsAsync(connection, $"WHERE m.id IN ({parameterNames})",
                token, transaction, parameters);
            foreach (var record in records) recordsById.Add(record.Id, record);
        }

        return requestedIds.Where(recordsById.ContainsKey).Select(id => recordsById[id]).ToList();
    }

    private static async Task<long?> RefreshGenerationMetadataAsync(DbConnection connection, GalleryCatalog sourceSnapshot,
        IReadOnlyList<ImageRecord> refreshedRecords, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        if (await ReadRevisionAsync(connection, transaction) != sourceSnapshot.Revision) return null;
        foreach (var record in refreshedRecords)
        {
            token.ThrowIfCancellationRequested();
            await WriteRecordAsync(connection, transaction, record, RecordWriteMode.Update);
        }
        var revision = await ReadRevisionAsync(connection, transaction);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return revision;
    }
}
