using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Browsing.Validators;
using Gallery.Core.Features.Browsing.Filtering;
using Gallery.Core.Features.Ingestion.Metadata;
using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Features.Storage;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Browsing;

internal sealed class GalleryCatalogLoader(
    GalleryLifetime galleryLifetime, VaultSession vaultSession, MediaStorageIndex mediaStorageIndex, GalleryDriveQueue driveQueue) : IGalleryCatalog
{
    private const string GalleryLoadActivityName = "gallery.load";
    private const string GalleryLoadElapsedMetricName = "gallery.load.elapsed_ms";

    public async Task<GalleryCatalog> LoadInitialPageAsync(int pageSize = 49, bool markedOnly = false, CancellationToken cancellationToken = default)
    {
        GalleryCatalogValidator.ValidatePageSize(pageSize);
        var generation = vaultSession.AcquireUnlockedSession();
        using var lease = generation.Borrow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.RevocationToken);
        cancellationToken = linked.Token;
        var store = galleryLifetime.Current.GalleryStore;
        try
        {
            var snapshot = await Task.Run(
                () => ReadInitialSnapshotAsync(store, pageSize, markedOnly, lease.Bytes, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (CanCompleteBrowseCatalog(snapshot))
                snapshot = await Task.Run(() => CompleteBrowseCatalogAsync(store, snapshot, lease.Bytes, cancellationToken), cancellationToken);
            return snapshot;
        }
        catch (Exception error) when (IsCanceledStorageFailure(error, cancellationToken)) { throw new OperationCanceledException(cancellationToken); }
    }

    public async Task<GalleryCatalog> LoadCatalogAsync(CancellationToken cancellationToken = default)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        using var lease = generation.Borrow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.RevocationToken);
        cancellationToken = linked.Token;
        var store = galleryLifetime.Current.GalleryStore;
        try
        {
            var snapshot = await Task.Run(() => ReadSnapshotAsync(store, lease.Bytes, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation.IsRevoked) throw new OperationCanceledException("The gallery session ended.");
            if (snapshot.IsCurrent)
                snapshot = await Task.Run(() => CompleteBrowseCatalogAsync(store, snapshot, lease.Bytes, cancellationToken), cancellationToken);
            return snapshot;
        }
        catch (Exception error) when (IsCanceledStorageFailure(error, cancellationToken)) { throw new OperationCanceledException(cancellationToken); }
    }

    public async Task<List<ImageRecord>> ReloadRecordsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var capturedIds = ids.ToArray();
        var generation = vaultSession.AcquireUnlockedSession();
        using var lease = generation.Borrow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.RevocationToken);
        cancellationToken = linked.Token;
        IGalleryBrowseReader reader = galleryLifetime.Current.GalleryStore;
        return await Task.Run(() => reader.ReadBrowseRecordsAsync(capturedIds, lease.Bytes, cancellationToken), cancellationToken);
    }

    private static async Task<GalleryCatalog> ReadInitialSnapshotAsync(
        IGalleryBrowseReader reader, int pageSize, bool markedOnly, byte[] key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var mode = markedOnly ? InitialPageMode.Marked : InitialPageMode.Active;
        var page = await reader.ReadInitialPageAsync(pageSize, mode, key, token);
        // An invalidated index is rebuilt from one consistent snapshot.
        return page ?? await ReadSnapshotAsync(reader, key, token);
    }

    private static async Task<GalleryCatalog> ReadSnapshotAsync(IGalleryBrowseReader reader, byte[] key, CancellationToken token)
    {
        using var activity = VaultOpenTelemetry.Source.StartActivity(GalleryLoadActivityName);
        var started = VaultOpenTelemetry.Start(activity);
        try { return await reader.ReadCatalogSnapshotAsync(key, token); }
        finally { VaultOpenTelemetry.Record(activity, GalleryLoadElapsedMetricName, started); }
    }

    private async Task<GalleryCatalog> CompleteBrowseCatalogAsync(IGalleryStore store, GalleryCatalog snapshot, byte[] key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Normal catalogs take only the empty backfill/index checks. Legacy backfills retain the existing behavior.
        await store.Gate.WaitAsync(token);
        try
        {
            if (await store.ReadStorageRevisionAsync(key) != snapshot.Revision) return snapshot with { IsCurrent = false };
            await store.BackfillFingerprintsAsync(key);
            snapshot = await BackfillGenerationAsync(store, snapshot, key, token);
        }
        finally { store.Gate.Release(); }
        token.ThrowIfCancellationRequested();
        await mediaStorageIndex.ScheduleAsync(snapshot.Records, key);
        await driveQueue.StartAfterObservingPreviousFailureAsync();
        token.ThrowIfCancellationRequested();
        return snapshot;
    }

    /// <summary>Counts catalog entries without reading or decoding media records.</summary>
    public async Task<long> CountRecordsAsync(CancellationToken cancellationToken = default)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        using var key = generation.Borrow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.RevocationToken);
        IGalleryBrowseReader reader = galleryLifetime.Current.GalleryStore;
        var count = await reader.CountRecordsAsync(key.Bytes, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        return count;
    }

    public async Task<List<ImageRecord>> ListAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        IGalleryBrowseReader reader = galleryLifetime.Current.GalleryStore;
        return await reader.ReadRecordsAsync(key.Bytes);
    }

    public async Task<List<ImageRecord>> LoadAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await LoadGalleryWithKeyAsync(galleryLifetime.Current.GalleryStore, key.Bytes);
    }

    private async Task<List<ImageRecord>> LoadGalleryWithKeyAsync(IGalleryStore store, byte[] key)
    {
        using var activity = VaultOpenTelemetry.Source.StartActivity(GalleryLoadActivityName);
        var started = VaultOpenTelemetry.Start(activity);
        List<ImageRecord> records;
        try
        {
            await store.Gate.WaitAsync();
            VaultOpenTelemetry.Record(activity, "gallery.load.gate_wait_ms", started);
            try
            {
                var revision = await store.ReadStorageRevisionAsync(key);
                records = await store.ReadRecordsAsync(key);
                var backfillStarted = VaultOpenTelemetry.Start(activity);
                await store.BackfillFingerprintsAsync(key);
                VaultOpenTelemetry.Record(activity, "gallery.load.backfill_fingerprint_ms", backfillStarted);
                backfillStarted = VaultOpenTelemetry.Start(activity);
                var snapshot = new GalleryCatalog(records, revision, records.Count, IsComplete: true, IsCurrent: true, CacheHit: false);
                records = (await BackfillGenerationAsync(store, snapshot, key, CancellationToken.None)).Records;
                VaultOpenTelemetry.Record(activity, "gallery.load.backfill_generation_ms", backfillStarted);
            }
            finally { store.Gate.Release(); }
            var scheduleStarted = VaultOpenTelemetry.Start(activity);
            await mediaStorageIndex.ScheduleAsync(records, key);
            VaultOpenTelemetry.Record(activity, "gallery.load.schedule_media_index_ms", scheduleStarted);
            driveQueue.Start();
            return records;
        }
        finally { VaultOpenTelemetry.Record(activity, GalleryLoadElapsedMetricName, started); }
    }

    /// <summary>Parses ComfyUI metadata once for records imported before (or with an older) parser.</summary>
    private static async Task<GalleryCatalog> BackfillGenerationAsync(IGalleryStore store, GalleryCatalog snapshot,
        byte[] key, CancellationToken token)
    {
        var staleRecords = SelectStaleRecords(snapshot.Records);
        if (Activity.Current is { OperationName: GalleryLoadActivityName } activity)
            activity.SetTag("gallery.load.backfill_generation_count", staleRecords.Count);
        if (staleRecords.Count == 0) return snapshot;

        var records = snapshot.Records.ToList();
        var refreshed = new List<ImageRecord>(staleRecords.Count);
        foreach (var (record, index) in staleRecords)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var imageWithRefreshedGenerationMetadata = RefreshGenerationMetadata(record);
                records[index] = imageWithRefreshedGenerationMetadata;
                refreshed.Add(imageWithRefreshedGenerationMetadata);
            }
            catch (Exception exception) when (exception is InvalidOperationException or CryptographicException or StoredMetadataFormatException)
            {
                throw new InvalidOperationException($"Could not backfill generation metadata for media item '{record.Id}'.", exception);
            }
        }

        try
        {
            var committedRevision = await store.RefreshGenerationMetadataAsync(snapshot, refreshed, key, token);
            return snapshot with { Records = records, Revision = committedRevision };
        }
        catch (CatalogRevisionConflictException)
        {
            return snapshot with { IsCurrent = false };
        }
    }

    private static List<(ImageRecord Record, int Index)> SelectStaleRecords(IReadOnlyList<ImageRecord> records) =>
        records.Select((record, index) => (Record: record, Index: index))
            .Where(item => item.Record.MetadataVersion < ComfyMetadata.Version).ToList();

    private static ImageRecord RefreshGenerationMetadata(ImageRecord record) => record with
    {
        Generation = ComfyMetadata.Extract(record.Metadata),
        MetadataVersion = ComfyMetadata.Version
    };

    private static bool CanCompleteBrowseCatalog(GalleryCatalog snapshot) => snapshot.IsComplete && snapshot.IsCurrent;

    private static bool IsCanceledStorageFailure(Exception error, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested && error is (DbException or CatalogStorageException);

    public List<ImageRecord> Filter(IEnumerable<ImageRecord> records, GalleryFilter filter,
        IReadOnlySet<string>? awaitingApprovalIds = null)
    {
        GalleryCatalogValidator.ValidateFilterArguments(records, filter);
        return GalleryFilterProcessor.Filter(filter, records, awaitingApprovalIds).ToList();
    }

    public List<PromptGroup> GroupByPrompt(IEnumerable<ImageRecord> records)
    {
        GalleryCatalogValidator.ValidateRecords(records);
        return PromptRules.GroupByPrompt(records);
    }

    public List<DiffusionModelGroup> GroupByDiffusionModel(IEnumerable<ImageRecord> records)
    {
        GalleryCatalogValidator.ValidateRecords(records);
        return DiffusionModelRules.GroupByDiffusionModel(records);
    }
}
