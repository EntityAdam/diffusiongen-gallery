using System.Diagnostics;
using Gallery.Core.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public async Task<GalleryCatalog> LoadInitialPageAsync(int pageSize = 49, bool markedOnly = false, CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var generation = vaultSession.AcquireUnlockedSession();
        using var lease = generation.Borrow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.RevocationToken);
        cancellationToken = linked.Token;
        var store = galleryStore;
        try
        {
            var snapshot = await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await store.ReadInitialPageAsync(pageSize, markedOnly, lease.Bytes, cancellationToken);
                if (page is not null) return page;
                // A missing/invalidated index is rebuilt from one consistent snapshot, never from guessed row order.
                using var activity = VaultOpenTelemetry.Source.StartActivity("gallery.load");
                var started = VaultOpenTelemetry.Start(activity);
                try { return await store.ReadCatalogSnapshotAsync(lease.Bytes, cancellationToken); }
                finally { VaultOpenTelemetry.Record(activity, "gallery.load.elapsed_ms", started); }
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.IsComplete && snapshot.IsCurrent) snapshot = await Task.Run(() => CompleteBrowseCatalogAsync(snapshot, lease.Bytes, cancellationToken), cancellationToken);
            return snapshot;
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }

    public async Task<GalleryCatalog> LoadCatalogAsync(CancellationToken cancellationToken = default)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        using var lease = generation.Borrow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.RevocationToken);
        cancellationToken = linked.Token;
        var store = galleryStore;
        try
        {
            var snapshot = await Task.Run(async () =>
            {
                using var activity = VaultOpenTelemetry.Source.StartActivity("gallery.load");
                var started = VaultOpenTelemetry.Start(activity);
                try { return await store.ReadCatalogSnapshotAsync(lease.Bytes, cancellationToken); }
                finally { VaultOpenTelemetry.Record(activity, "gallery.load.elapsed_ms", started); }
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation.IsRevoked) throw new OperationCanceledException("The gallery session ended.");
            if (snapshot.IsCurrent) snapshot = await Task.Run(() => CompleteBrowseCatalogAsync(snapshot, lease.Bytes, cancellationToken), cancellationToken);
            return snapshot;
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }

    public async Task<List<ImageRecord>> ReloadRecordsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var capturedIds = ids.ToArray();
        var generation = vaultSession.AcquireUnlockedSession();
        using var lease = generation.Borrow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.RevocationToken);
        cancellationToken = linked.Token;
        var store = galleryStore;
        return await Task.Run(() => store.ReadBrowseRecordsAsync(capturedIds, lease.Bytes, cancellationToken), cancellationToken);
    }
    private async Task<GalleryCatalog> CompleteBrowseCatalogAsync(GalleryCatalog snapshot, byte[] key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Normal catalogs take only the empty backfill/index checks. Legacy backfills retain the existing behavior.
        await galleryStore.Gate.WaitAsync(token);
        try
        {
            if (await galleryStore.ReadStorageRevisionAsync() != snapshot.Revision) return snapshot with { IsCurrent = false };
            var changed = snapshot.Records.Count(record => record.MetadataVersion < Processors.ComfyMetadata.Version);
            await BackfillFingerprintsAsync(key);
            await BackfillGenerationAsync(snapshot.Records, key, token);
            if (changed > 0)
            {
                var revision = checked(snapshot.Revision + changed);
                var current = await galleryStore.SaveBrowseIndexAsync(snapshot.Records, revision, key, token);
                snapshot = snapshot with { Revision = revision, IsCurrent = current };
            }
        }
        finally { galleryStore.Gate.Release(); }
        token.ThrowIfCancellationRequested();
        await ScheduleMediaIndexAsync(snapshot.Records, key);
        driveOperationPlayer.Start();
        token.ThrowIfCancellationRequested();
        return snapshot;
    }
}
