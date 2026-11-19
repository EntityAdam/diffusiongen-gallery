using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using Gallery.Core.Exceptions;
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
    public async Task<GalleryCatalog> LoadInitialPageAsync(int pageSize = 49, bool markedOnly = false, CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
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
            if (snapshot.IsComplete && snapshot.IsCurrent)
                snapshot = await Task.Run(() => CompleteBrowseCatalogAsync(store, snapshot, lease.Bytes, cancellationToken), cancellationToken);
            return snapshot;
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested && error is DbException or CatalogStorageException) { throw new OperationCanceledException(cancellationToken); }
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
        catch (Exception error) when (cancellationToken.IsCancellationRequested && error is DbException or CatalogStorageException) { throw new OperationCanceledException(cancellationToken); }
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
        var page = await reader.ReadInitialPageAsync(pageSize, markedOnly, key, token);
        // An invalidated index is rebuilt from one consistent snapshot.
        return page ?? await ReadSnapshotAsync(reader, key, token);
    }

    private static async Task<GalleryCatalog> ReadSnapshotAsync(IGalleryBrowseReader reader, byte[] key, CancellationToken token)
    {
        using var activity = VaultOpenTelemetry.Source.StartActivity("gallery.load");
        var started = VaultOpenTelemetry.Start(activity);
        try { return await reader.ReadCatalogSnapshotAsync(key, token); }
        finally { VaultOpenTelemetry.Record(activity, "gallery.load.elapsed_ms", started); }
    }

    private async Task<GalleryCatalog> CompleteBrowseCatalogAsync(IGalleryStore store, GalleryCatalog snapshot, byte[] key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Normal catalogs take only the empty backfill/index checks. Legacy backfills retain the existing behavior.
        await store.Gate.WaitAsync(token);
        try
        {
            if (await store.ReadStorageRevisionAsync(key) != snapshot.Revision) return snapshot with { IsCurrent = false };
            var changed = snapshot.Records.Count(record => record.MetadataVersion < ComfyMetadata.Version);
            await store.BackfillFingerprintsAsync(key);
            await BackfillGenerationAsync(store, snapshot.Records, key, token);
            if (changed > 0)
            {
                var revision = await store.ReadStorageRevisionAsync(key);
                var current = await store.SaveBrowseIndexAsync(snapshot.Records, revision, key, token);
                snapshot = snapshot with { Revision = revision, IsCurrent = current };
            }
        }
        finally { store.Gate.Release(); }
        token.ThrowIfCancellationRequested();
        await mediaStorageIndex.ScheduleAsync(snapshot.Records, key);
        driveQueue.Start();
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
        using var activity = VaultOpenTelemetry.Source.StartActivity("gallery.load");
        var started = VaultOpenTelemetry.Start(activity);
        List<ImageRecord> records;
        try
        {
            await store.Gate.WaitAsync();
            VaultOpenTelemetry.Record(activity, "gallery.load.gate_wait_ms", started);
            try
            {
                records = await store.ReadRecordsAsync(key);
                var backfillStarted = VaultOpenTelemetry.Start(activity);
                await store.BackfillFingerprintsAsync(key);
                VaultOpenTelemetry.Record(activity, "gallery.load.backfill_fingerprint_ms", backfillStarted);
                backfillStarted = VaultOpenTelemetry.Start(activity);
                await BackfillGenerationAsync(store, records, key);
                VaultOpenTelemetry.Record(activity, "gallery.load.backfill_generation_ms", backfillStarted);
            }
            finally { store.Gate.Release(); }
            var scheduleStarted = VaultOpenTelemetry.Start(activity);
            await mediaStorageIndex.ScheduleAsync(records, key);
            VaultOpenTelemetry.Record(activity, "gallery.load.schedule_media_index_ms", scheduleStarted);
            driveQueue.Start();
            return records;
        }
        finally { VaultOpenTelemetry.Record(activity, "gallery.load.elapsed_ms", started); }
    }

    /// <summary>Parses ComfyUI metadata once for records imported before (or with an older) parser.</summary>
    private static async Task BackfillGenerationAsync(IGalleryStore store, List<ImageRecord> records, byte[] key, CancellationToken cancellationToken = default)
    {
        var staleRecordIndices = records.Select((record, index) => (record, index))
            .Where(item => item.record.MetadataVersion < ComfyMetadata.Version).ToList();
        if (Activity.Current is { OperationName: "gallery.load" } activity)
            activity.SetTag("gallery.load.backfill_generation_count", staleRecordIndices.Count);
        foreach (var staleRecordIndex in staleRecordIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageRecord record;
            int index;
            (record, index) = staleRecordIndex;
            try
            {
                var refreshedImageRecord = record with
                {
                    Generation = ComfyMetadata.Extract(record.Metadata),
                    MetadataVersion = ComfyMetadata.Version
                };
                // The browse opener rebuilds once after the batch; rewriting its index per legacy row is wasteful.
                await store.UpdateAsync(refreshedImageRecord, key, preserveBrowseIndex: false);
                records[index] = refreshedImageRecord;
            }
            catch (Exception exception) when (exception is InvalidOperationException or CryptographicException or StoredMetadataFormatException)
            {
                throw new InvalidOperationException($"Could not backfill generation metadata for media item '{record.Id}'.", exception);
            }
        }
    }

    public List<ImageRecord> Filter(IEnumerable<ImageRecord> records, GalleryFilter filter,
        IReadOnlySet<string>? awaitingApprovalIds = null) =>
        GalleryFilterProcessor.Apply(filter, records, awaitingApprovalIds).ToList();

    public List<PromptGroup> GroupByPrompt(IEnumerable<ImageRecord> records) => PromptRules.GroupByPrompt(records);

    public List<DiffusionModelGroup> GroupByDiffusionModel(IEnumerable<ImageRecord> records) =>
        DiffusionModelRules.GroupByDiffusionModel(records);
}
