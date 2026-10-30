using System.Diagnostics;
using Gallery.Core.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Services.DataManagers;

internal sealed partial class GalleryStore
{
    private const string BrowseIndexId = "browse-first-page";
    private const string BrowseIndexPurpose = "browse-first-page:v1";
    internal const int BrowseIndexCapacity = 1024;

    private sealed record BrowseIndex(int Version, long Revision, int TotalRecords,
        string[] All, string[] Active, string[] Marked);

    public async Task<GalleryCatalog?> ReadInitialPageAsync(int pageSize, bool markedOnly, byte[] key, CancellationToken token)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction(deferred: true);
        var revision = await ReadBrowseRevisionAsync(connection, transaction, token);
        var index = await ReadBrowseIndexAsync(connection, transaction, key, token);
        if (index is null || index.Version != 1 || index.Revision != revision || index.TotalRecords < 0
            || index.All is null || index.Active is null || index.Marked is null
            || index.All.Length > BrowseIndexCapacity || index.Active.Length > BrowseIndexCapacity || index.Marked.Length > BrowseIndexCapacity)
            return null;
        var complete = index.TotalRecords == index.All.Length;
        var ids = complete ? index.All : (markedOnly ? index.Marked : index.Active).Take(pageSize).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new InvalidOperationException("Invalid encrypted browse index.");
        using var activity = VaultOpenTelemetry.Source.StartActivity("gallery.first_page");
        var records = await ReadBrowseRecordsAsync(connection, transaction, ids, key, token, activity);
        // An authenticated but inconsistent index must never serve a stale/deleted record.
        if (records.Count != ids.Length) return null;
        transaction.Commit();
        activity?.SetTag("gallery.first_page.record_count", records.Count);
        activity?.SetTag("gallery.first_page.catalog_count", index.TotalRecords);
        return new(records, revision, index.TotalRecords, complete, true, true);
    }

    public async Task<GalleryCatalog> ReadCatalogSnapshotAsync(byte[] key, CancellationToken token)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction(deferred: true);
        var revision = await ReadBrowseRevisionAsync(connection, transaction, token);
        var records = await ReadImageRecordsAsync(connection, key,
            Activity.Current is { OperationName: "gallery.load", IsAllDataRequested: true } activity ? activity : null, token, transaction);
        transaction.Commit();
        var current = await SaveBrowseIndexAsync(records, revision, key, token);
        return new(records, revision, records.Count, true, current, false);
    }

    public async Task<List<ImageRecord>> ReadBrowseRecordsAsync(IEnumerable<string> ids, byte[] key, CancellationToken token)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction(deferred: true);
        var records = await ReadBrowseRecordsAsync(connection, transaction, ids.Distinct(StringComparer.Ordinal).ToArray(), key, token, null);
        transaction.Commit();
        return records;
    }

    private static async Task<List<ImageRecord>> ReadBrowseRecordsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<string> ids, byte[] key, CancellationToken token, Activity? activity)
    {
        var records = new List<ImageRecord>(ids.Count);
        if (ids.Count == 0) return records;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, record FROM images WHERE id IN (" + string.Join(',', ids.Select((_, i) => "$id" + i)) + ")";
        for (var i = 0; i < ids.Count; i++) command.Parameters.AddWithValue("$id" + i, ids[i]);
        using var cancellation = token.Register(command.Cancel);
        var byId = new Dictionary<string, ImageRecord>(StringComparer.Ordinal);
        long bytes = 0;
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                token.ThrowIfCancellationRequested();
                var row = MapImageRecord(reader);
                bytes += row.Record.Length;
                byId.Add(row.Id, DecodeRecord(row.Id, row.Record, key));
            }
        foreach (var id in ids) if (byId.TryGetValue(id, out var record)) records.Add(record);
        activity?.SetTag("gallery.first_page.record_bytes", bytes);
        return records;
    }

    public async Task<bool> SaveBrowseIndexAsync(List<ImageRecord> records, long revision, byte[] key, CancellationToken token)
    {
        // LINQ's stable ordering preserves the existing newest-first tie order of the catalog scan.
        var ordered = records.OrderByDescending(record => record.ImportedAt);
        var index = new BrowseIndex(1, revision, records.Count,
            ordered.Take(BrowseIndexCapacity).Select(r => r.Id).ToArray(),
            ordered.Where(r => !r.IsMarkedForDeletion).Take(BrowseIndexCapacity).Select(r => r.Id).ToArray(),
            ordered.Where(r => r.IsMarkedForDeletion).Take(BrowseIndexCapacity).Select(r => r.Id).ToArray());
        token.ThrowIfCancellationRequested();
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        if (await ReadBrowseRevisionAsync(connection, transaction, token) != revision) return false;
        var existing = await ReadBrowseIndexAsync(connection, transaction, key, token);
        if (existing is not null && existing.Version == index.Version && existing.Revision == revision
            && existing.TotalRecords == index.TotalRecords && existing.All is not null && existing.Active is not null && existing.Marked is not null && existing.All.SequenceEqual(index.All)
            && existing.Active.SequenceEqual(index.Active) && existing.Marked.SequenceEqual(index.Marked))
            return true;
        using var command = CommandFactories.GalleryStoreCommandFactory.CreateSaveSettings(connection,
            BrowseIndexId, EncodeSetting(index, BrowseIndexPurpose, key));
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(token);
        transaction.Commit();
        return true;
    }

    private static async Task<BrowseIndex?> ReadBrowseIndexAsync(SqliteConnection connection, SqliteTransaction transaction,
        byte[] key, CancellationToken token)
    {
        using var command = CommandFactories.GalleryStoreCommandFactory.CreateReadSettings(connection, BrowseIndexId);
        command.Transaction = transaction;
        var encrypted = (byte[]?)await command.ExecuteScalarAsync(token);
        return encrypted is null ? null : DecodeSetting<BrowseIndex>(encrypted, BrowseIndexPurpose, key);
    }

    private static async Task<long> ReadBrowseRevisionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        using var command = CommandFactories.GalleryStoreCommandFactory.CreateReadStorageRevision(connection);
        command.Transaction = transaction;
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    // Rating, notes, names, and tags do not change newest-first membership. Preserve a valid index
    // atomically for those edits; ranking/deletion changes and other write paths invalidate via the revision trigger.
    private static async Task PreserveBrowseIndexAsync(SqliteConnection connection, SqliteTransaction transaction,
        ImageRecord previous, ImageRecord updated, byte[] key, CancellationToken token)
    {
        if (previous.ImportedAt != updated.ImportedAt || previous.IsMarkedForDeletion != updated.IsMarkedForDeletion) return;
        var revision = await ReadBrowseRevisionAsync(connection, transaction, token);
        var index = await ReadBrowseIndexAsync(connection, transaction, key, token);
        if (index is null || index.Revision != revision - 1 || index.Version != 1) return;
        using var command = CommandFactories.GalleryStoreCommandFactory.CreateSaveSettings(connection,
            BrowseIndexId, EncodeSetting(index with { Revision = revision }, BrowseIndexPurpose, key));
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(token);
    }
}
