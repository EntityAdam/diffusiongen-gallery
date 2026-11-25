using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence.Sqlite.DataManagers.CommandFactories;
using Gallery.Core.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Infrastructure.Persistence.Sqlite.DataManagers;

internal sealed partial class GalleryStore
{
    private const string BrowseIndexId = "browse-first-page";
    private const string BrowseIndexPurpose = "browse-first-page:v1";
    private const int BrowseIndexVersion = 1;
    internal const int BrowseIndexCapacity = 1024;
    private const int BrowseQueryBatchSize = 900;

    private sealed record BrowseIndex(int Version, long Revision, int TotalRecords,
        ImmutableArray<string> All, ImmutableArray<string> Active, ImmutableArray<string> Marked);

    private sealed record BrowsePageSelection(BrowseIndex Index, long Revision, string[] Ids);
    private sealed record BrowseRecordBatchScan(
        Dictionary<string, ImageRecord> RecordsById, long EncryptedRecordBytes);

    private sealed class BrowseIndexRevisionConflictException(long expectedRevision, long actualRevision)
        : InvalidOperationException($"Browse index revision changed from {expectedRevision} to {actualRevision} before it could be saved.");

    public async Task<GalleryCatalog?> ReadInitialPageAsync(int pageSize, InitialPageMode mode, byte[] key, CancellationToken token)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction(deferred: true);
        var selection = await SelectUsableBrowsePageAsync(connection, transaction, pageSize, mode, key, token);
        if (selection is null) return null;
        using var activity = VaultOpenTelemetry.Source.StartActivity("gallery.first_page");
        var records = await TryReadSelectedBrowsePageAsync(connection, transaction, selection, key, token, activity);
        if (records is null) return null;
        transaction.Commit();
        RecordInitialPageMetrics(activity, selection.Index.TotalRecords, records.Count);
        return new(records, selection.Revision, selection.Index.TotalRecords,
            selection.Index.TotalRecords == selection.Index.All.Length, true, true);
    }

    private static async Task<BrowsePageSelection?> SelectUsableBrowsePageAsync(SqliteConnection connection,
        SqliteTransaction transaction, int pageSize, InitialPageMode mode, byte[] key, CancellationToken token)
    {
        var revision = await ReadBrowseRevisionAsync(connection, transaction, token);
        var index = await ReadBrowseIndexAsync(connection, transaction, key, token);
        if (!IsUsableBrowseIndex(index, revision)) return null;

        var usableIndex = index!;
        var ids = SelectInitialPageIds(usableIndex, pageSize, mode);
        EnsureInitialPageIdsAreUnique(ids);
        return new BrowsePageSelection(usableIndex, revision, ids);
    }

    private static void EnsureInitialPageIdsAreUnique(IReadOnlyCollection<string> ids)
    {
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new InvalidOperationException("Invalid encrypted browse index.");
    }

    private static async Task<List<ImageRecord>?> TryReadSelectedBrowsePageAsync(SqliteConnection connection,
        SqliteTransaction transaction, BrowsePageSelection selection, byte[] key, CancellationToken token,
        Activity? activity)
    {
        var records = await ReadBrowseRecordsAsync(connection, transaction, selection.Ids, key, token, activity);
        // An authenticated but inconsistent index must never serve a stale/deleted record.
        return records.Count == selection.Ids.Length ? records : null;
    }

    private static void RecordInitialPageMetrics(Activity? activity, int catalogCount, int recordCount)
    {
        activity?.SetTag("gallery.first_page.record_count", recordCount);
        activity?.SetTag("gallery.first_page.catalog_count", catalogCount);
    }

    public async Task<GalleryCatalog> ReadCatalogSnapshotAsync(byte[] key, CancellationToken token)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction(deferred: true);
        var revision = await ReadBrowseRevisionAsync(connection, transaction, token);
        var records = await ReadImageRecordsAsync(connection, key,
            Activity.Current is { OperationName: GalleryLoadOperationName, IsAllDataRequested: true } activity ? activity : null, token, transaction);
        transaction.Commit();
        var current = true;
        try
        {
            await SaveBrowseIndexAsync(records, revision, key, token);
        }
        catch (BrowseIndexRevisionConflictException)
        {
            current = false;
        }
        return new(records, revision, records.Count, true, current, false);
    }

    public async Task<List<ImageRecord>> ReadBrowseRecordsAsync(IEnumerable<string> ids, byte[] key, CancellationToken token)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction(deferred: true);
        var distinctIds = SelectDistinctBrowseRecordIds(ids);
        var records = await ReadBrowseRecordsAsync(connection, transaction, distinctIds, key, token, null);
        transaction.Commit();
        return records;
    }

    private static string[] SelectDistinctBrowseRecordIds(IEnumerable<string> ids) =>
        ids.Distinct(StringComparer.Ordinal).ToArray();

    private static async Task<List<ImageRecord>> ReadBrowseRecordsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<string> ids, byte[] key, CancellationToken token, Activity? activity)
    {
        if (ids.Count == 0) return [];
        var scan = await ScanBrowseRecordBatchesAsync(connection, transaction, ids, key, token);
        var records = SelectBrowseRecordsInRequestedOrder(ids, scan.RecordsById);
        activity?.SetTag("gallery.first_page.record_bytes", scan.EncryptedRecordBytes);
        return records;
    }

    private static async Task<BrowseRecordBatchScan> ScanBrowseRecordBatchesAsync(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyList<string> ids, byte[] key, CancellationToken token)
    {
        var imageRecordsById = new Dictionary<string, ImageRecord>(StringComparer.Ordinal);
        long bytes = 0;
        for (var batchStart = 0; batchStart < ids.Count; batchStart += BrowseQueryBatchSize)
        {
            var batchCount = Math.Min(BrowseQueryBatchSize, ids.Count - batchStart);
            using var command = CreateBrowseRecordsCommand(connection, transaction, ids, batchStart, batchCount);
            using var cancellation = token.Register(command.Cancel);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                token.ThrowIfCancellationRequested();
                var row = MapImageRecord(reader);
                bytes += row.Record.Length;
                imageRecordsById.Add(row.Id, DecodeRecord(row.Id, row.Record, key));
            }
        }
        return new BrowseRecordBatchScan(imageRecordsById, bytes);
    }

    private static SqliteCommand CreateBrowseRecordsCommand(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<string> ids, int batchStart, int batchCount)
    {
        var parameterNames = new string[batchCount];
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        for (var i = 0; i < batchCount; i++)
        {
            var parameterName = "$id" + i;
            parameterNames[i] = parameterName;
            var id = ids[batchStart + i];
            command.Parameters.Add(parameterName, SqliteType.Text, Math.Max(id.Length, 1)).Value = id;
        }
        command.CommandText = "SELECT id, record FROM images WHERE id IN (" + string.Join(',', parameterNames) + ")";
        return command;
    }

    private static List<ImageRecord> SelectBrowseRecordsInRequestedOrder(IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, ImageRecord> imageRecordsById)
    {
        var records = new List<ImageRecord>(ids.Count);
        foreach (var id in ids)
            if (imageRecordsById.TryGetValue(id, out var imageRecord))
                records.Add(imageRecord);
        return records;
    }

    private static bool IsUsableBrowseIndex(BrowseIndex? index, long revision) =>
        index is not null && index.Version == BrowseIndexVersion && index.Revision == revision && index.TotalRecords >= 0
        && !index.All.IsDefault && !index.Active.IsDefault && !index.Marked.IsDefault
        && index.All.Length <= BrowseIndexCapacity && index.Active.Length <= BrowseIndexCapacity && index.Marked.Length <= BrowseIndexCapacity;

    private static string[] SelectInitialPageIds(BrowseIndex index, int pageSize, InitialPageMode mode)
    {
        if (index.TotalRecords == index.All.Length) return index.All.ToArray();
        return (mode == InitialPageMode.Marked ? index.Marked : index.Active).Take(pageSize).ToArray();
    }

    public async Task<long> RefreshGenerationMetadataAsync(GalleryCatalog sourceSnapshot,
        IReadOnlyList<ImageRecord> refreshedRecords, byte[] key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        var currentRevision = await ReadBrowseRevisionAsync(connection, transaction, token);
        if (currentRevision != sourceSnapshot.Revision)
            throw new CatalogRevisionConflictException(sourceSnapshot.Revision, currentRevision);
        await WriteRefreshedRecordsAsync(connection, transaction, refreshedRecords, key, token);
        var revision = await ReadBrowseRevisionAsync(connection, transaction, token);
        await WriteBrowseIndexAsync(connection, transaction, sourceSnapshot.Records, revision, key, token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return revision;
    }

    private async Task SaveBrowseIndexAsync(List<ImageRecord> records, long revision, byte[] key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        var currentRevision = await ReadBrowseRevisionAsync(connection, transaction, token);
        if (currentRevision != revision)
            throw new BrowseIndexRevisionConflictException(revision, currentRevision);
        await WriteBrowseIndexAsync(connection, transaction, records, revision, key, token);
        transaction.Commit();
    }

    private static async Task WriteRefreshedRecordsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<ImageRecord> refreshedRecords, byte[] key, CancellationToken token)
    {
        foreach (var record in refreshedRecords)
        {
            token.ThrowIfCancellationRequested();
            await WriteUpdatedRecordAsync(connection, transaction, record, key, token);
        }
    }

    private static async Task WriteBrowseIndexAsync(SqliteConnection connection, SqliteTransaction transaction,
        List<ImageRecord> records, long revision, byte[] key, CancellationToken token)
    {
        // LINQ's stable ordering preserves the existing newest-first tie order of the catalog scan.
        var index = BuildBrowseIndex(records, revision);
        var existing = await ReadBrowseIndexAsync(connection, transaction, key, token);
        if (BrowseIndexesMatch(existing, index, revision))
            return;
        using var command = GalleryStoreCommandFactory.CreateSaveSettings(connection,
            BrowseIndexId, EncodeSetting(index, BrowseIndexPurpose, key));
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(token);
    }

    private static BrowseIndex BuildBrowseIndex(List<ImageRecord> records, long revision)
    {
        var ordered = records.OrderByDescending(record => record.ImportedAt);
        return new BrowseIndex(BrowseIndexVersion, revision, records.Count,
            ordered.Take(BrowseIndexCapacity).Select(record => record.Id).ToImmutableArray(),
            ordered.Where(record => !record.IsMarkedForDeletion).Take(BrowseIndexCapacity).Select(record => record.Id).ToImmutableArray(),
            ordered.Where(record => record.IsMarkedForDeletion).Take(BrowseIndexCapacity).Select(record => record.Id).ToImmutableArray());
    }

    private static bool BrowseIndexesMatch(BrowseIndex? existing, BrowseIndex index, long revision) =>
        existing is not null && existing.Version == index.Version && existing.Revision == revision
        && existing.TotalRecords == index.TotalRecords && !existing.All.IsDefault && !existing.Active.IsDefault
        && !existing.Marked.IsDefault && existing.All.SequenceEqual(index.All)
        && existing.Active.SequenceEqual(index.Active) && existing.Marked.SequenceEqual(index.Marked);

    private static async Task<BrowseIndex?> ReadBrowseIndexAsync(SqliteConnection connection, SqliteTransaction transaction,
        byte[] key, CancellationToken token)
    {
        using var command = GalleryStoreCommandFactory.CreateReadSettings(connection, BrowseIndexId);
        command.Transaction = transaction;
        var encrypted = (byte[]?)await command.ExecuteScalarAsync(token);
        if (encrypted is null) return null;
        try { return DecodeSetting<BrowseIndex>(encrypted, BrowseIndexPurpose, key); }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            Activity.Current?.AddEvent(new ActivityEvent("gallery.browse_index.cache_unreadable",
                tags: new ActivityTagsCollection { { "exception.type", exception.GetType().FullName ?? exception.GetType().Name } }));
            return null;
        }
    }

    private static async Task<long> ReadBrowseRevisionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        using var command = GalleryStoreCommandFactory.CreateReadStorageRevision(connection);
        command.Transaction = transaction;
        return await ReadRequiredStorageRevisionAsync(command, token);
    }

    // Rating, notes, names, and tags do not change newest-first membership. Preserve a valid index
    // atomically for those edits; ranking/deletion changes and other write paths invalidate via the revision trigger.
    private static async Task PreserveBrowseIndexAsync(SqliteConnection connection, SqliteTransaction transaction,
        ImageRecord previous, ImageRecord updated, byte[] key, CancellationToken token)
    {
        if (AffectsBrowseIndex(previous, updated)) return;
        var revision = await ReadBrowseRevisionAsync(connection, transaction, token);
        var index = await ReadBrowseIndexAsync(connection, transaction, key, token);
        if (!CanAdvanceBrowseIndex(index, revision)) return;
        var currentIndex = index!;
        using var command = GalleryStoreCommandFactory.CreateSaveSettings(connection,
            BrowseIndexId, EncodeSetting(currentIndex with { Revision = revision }, BrowseIndexPurpose, key));
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(token);
    }

    private static bool AffectsBrowseIndex(ImageRecord previous, ImageRecord updated) =>
        previous.ImportedAt != updated.ImportedAt || previous.IsMarkedForDeletion != updated.IsMarkedForDeletion;

    private static bool CanAdvanceBrowseIndex(BrowseIndex? index, long revision) =>
        index is not null && index.Revision == revision - 1 && index.Version == BrowseIndexVersion;
}
