using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Ports.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Queries;
using Gallery.Core.Services.Sessions;
using Gallery.Core.Services.Validators;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    internal const string CatalogFileName = GalleryStoreFactory.CatalogFileName;
    internal const string VaultHeaderFileName = VaultStore.HeaderFileName;

    private const string EmptyDuplicateCandidatesMessage =
        "Cannot choose a duplicate keeper by deletion status, rating, favorite status, dimensions, original size, and import time: candidate count was 0.";

    private readonly GalleryLifetime galleryLifetime;
    private readonly VaultManager vaultManager;
    private readonly RemovalManager removalManager;
    private readonly GalleryMediaReader mediaReader;
    private IGalleryStore galleryStore => galleryLifetime.Current.GalleryStore;
    private VaultStore vaultStore => galleryLifetime.Current.VaultStore;
    private readonly VaultSession vaultSession;
    private readonly ImportMediaPreparer importMediaPreparer;
    private readonly GalleryVisionWorkflow visionWorkflow;

    internal GalleryService(IGalleryStore galleryStore, VaultStore vaultStore, VaultSession vaultSession, VideoTools? videoTools = null,
        VisionManager? visionManager = null, IGalleryStoreFactory? storeFactory = null)
    {
        galleryLifetime = new GalleryLifetime(galleryStore, vaultStore, storeFactory);
        this.vaultSession = vaultSession;
        vaultManager = new VaultManager(galleryLifetime, vaultSession);
        mediaReader = new GalleryMediaReader(galleryLifetime, vaultSession);
        importMediaPreparer = new ImportMediaPreparer(videoTools);
        visionWorkflow = new GalleryVisionWorkflow(galleryLifetime, vaultSession, visionManager);
        this.vaultSession.AutoLocked += () =>
        {
            _ = ReleaseCheckoutAfterOperationsAsync();
            OnAutoLocked?.Invoke();
        };
        driveOperationPlayer = CreateDriveOperationPlayer();
        removalManager = new RemovalManager(galleryLifetime, vaultSession, driveOperationPlayer.Start);
    }

    public List<ImageRecord> Filter(IEnumerable<ImageRecord> records, GalleryFilter filter,
        IReadOnlySet<string>? awaitingApprovalIds = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(filter);
        return GalleryFilterProcessor.Apply(filter, records, awaitingApprovalIds)
            .ToList();
    }

    public List<PromptGroup> GroupByPrompt(IEnumerable<ImageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return PromptRules.GroupByPrompt(records)
            .ToList();
    }

    public List<DiffusionModelGroup> GroupByDiffusionModel(IEnumerable<ImageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return DiffusionModelRules.GroupByDiffusionModel(records);
    }

    public ImageRecord PickDuplicateKeeper(IEnumerable<ImageRecord> records)
    {
        var candidates = RequireDuplicateCandidates(records);
        return DuplicateRules.PickKeeper(candidates);
    }

    public HashSet<string> InitialDuplicateKeepers(IReadOnlyCollection<ImageRecord> duplicateImageRecords)
    {
        var candidates = RequireDuplicateCandidates(duplicateImageRecords);
        return DuplicateRules.InitialKeepers(candidates.ToArray());
    }

    private static List<ImageRecord> RequireDuplicateCandidates(IEnumerable<ImageRecord> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var candidateRecords = candidates.ToList();
        if (candidateRecords.Count == 0)
            throw new InvalidOperationException(EmptyDuplicateCandidatesMessage);
        return candidateRecords;
    }

    public ImageRecordEdit ApplyReviewDecision(ImageRecordEdit image, ReviewAction action, int rating = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!Enum.IsDefined(action))
            throw new InvalidOperationException(
                $"Review action value {(int)action} is invalid; choose Keep, Favorite, MarkDeletion, or Rate.");
        if (action == ReviewAction.Rate && rating is < 1 or > 5)
            throw new InvalidOperationException($"Rating {rating} must be between 1 and 5.");
        return ReviewDecision.Apply(image, action, rating);
    }

    public ReviewState CaptureReviewState(ImageRecord image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ReviewDecision.Capture(image);
    }

    public ReviewState CaptureReviewState(ImageRecordEdit image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ReviewDecision.Capture(image);
    }

    public async Task<ImageRecord> RestoreReviewStateAsync(string id, ReviewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ImageRecordEditValidator.ValidateRating(state.Rating);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.Gate.WaitAsync();
        try
        {
            var image = await FindAsync(id, key.Bytes);
            var restored = ReviewDecision.Restore(image, state);
            await galleryStore.UpdateAsync(restored, key.Bytes);
            return restored;
        }
        finally { galleryStore.Gate.Release(); }
    }

    public ImageRecordEdit SetCuration(ImageRecordEdit image, int? rating, bool? reviewed)
    {
        ArgumentNullException.ThrowIfNull(image);
        ImageRecordEditValidator.ValidateRating(rating);
        return LibraryRules.SetCuration(image, rating, reviewed);
    }

    public ImageRecordEdit SetDeletionMark(ImageRecordEdit image, bool markedForDeletion)
    {
        ArgumentNullException.ThrowIfNull(image);
        return LibraryRules.SetDeletionMark(image, markedForDeletion);
    }

    public ImageRecordEdit SetOrganization(ImageRecordEdit image, int rating, bool reviewed, string? collection)
    {
        ArgumentNullException.ThrowIfNull(image);
        ImageRecordEditValidator.ValidateRating(rating);
        if (collection is not null && ImageRecordEditValidator.IsInvalidCollectionName(collection))
            throw new InvalidOperationException($"Collection name '{collection}' is invalid; collection names must have 1 to 80 characters.");
        return LibraryRules.SetOrganization(image, rating, reviewed, collection);
    }

    public ImageRecordEdit AddCollection(ImageRecordEdit image, string collection)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateCollectionName(collection);
        return LibraryRules.AddCollection(image, collection);
    }

    public ImageRecordEdit RemoveCollection(ImageRecordEdit image, string collection)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateCollectionName(collection);
        return LibraryRules.RemoveCollection(image, collection);
    }

    private static void ValidateCollectionName(string collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (ImageRecordEditValidator.IsInvalidCollectionName(collection))
            throw new InvalidOperationException($"Collection name '{collection}' is invalid; collection names must have 1 to 80 characters.");
    }

    public ImageRecord ApplyEdit(ImageRecord image, Func<ImageRecordEdit, ImageRecordEdit> update)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(update);
        var edit = update(new ImageRecordEdit(image));
        ImageRecordEditValidator.Validate(edit, image.Id);
        return LibraryRules.ApplyEdit(image, edit);
    }

    public string MergeTags(string existing, string additions) => LibraryRules.MergeTags(existing, additions);

    public string RemoveTag(string existing, string tag) => LibraryRules.RemoveTag(existing, tag);

    public async Task<List<SavedView>> LoadViewsAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryStore.LoadViewsAsync(key.Bytes);
    }

    public async Task SaveViewAsync(string name, GalleryFilter filter)
    {
        var normalizedName = name.Trim();
        SavedViewNameValidator.Validate(normalizedName);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.SaveViewAsync(normalizedName, filter, key.Bytes);
    }

    public async Task DeleteViewAsync(string name)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.DeleteViewAsync(name, key.Bytes);
    }

    public static string FormatDuration(ImageRecord image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return GalleryDisplayFormatter.FormatDuration(image);
    }

    public static string FormatBytes(long bytes) => GalleryDisplayFormatter.FormatBytes(bytes);
    public static string FormatModelFilename(string model) => GalleryDisplayFormatter.FormatModelFilename(model);

    /// <summary>Counts catalog entries without reading or decoding media records.</summary>
    public async Task<long> CountRecordsAsync(CancellationToken cancellationToken = default)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        using var key = generation.Borrow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.RevocationToken);
        var count = await galleryStore.CountRecordsAsync(key.Bytes, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        return count;
    }

    public async Task<List<ImageRecord>> ListAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await ListWithKeyAsync(key.Bytes);
    }

    public async Task<List<ImageRecord>> LoadAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await LoadGalleryWithKeyAsync(key.Bytes);
    }

    private async Task<List<ImageRecord>> LoadGalleryWithKeyAsync(byte[] key)
    {
        using var activity = VaultOpenTelemetry.Source.StartActivity("gallery.load");
        var started = VaultOpenTelemetry.Start(activity);
        List<ImageRecord> records;
        try
        {
            await galleryStore.Gate.WaitAsync();
            VaultOpenTelemetry.Record(activity, "gallery.load.gate_wait_ms", started);
            try
            {
                records = await ListWithKeyAsync(key);
                var backfillStarted = VaultOpenTelemetry.Start(activity);
                await BackfillFingerprintsAsync(key);
                VaultOpenTelemetry.Record(activity, "gallery.load.backfill_fingerprint_ms", backfillStarted);
                backfillStarted = VaultOpenTelemetry.Start(activity);
                await BackfillGenerationAsync(records, key);
                VaultOpenTelemetry.Record(activity, "gallery.load.backfill_generation_ms", backfillStarted);
            }
            finally { galleryStore.Gate.Release(); }
            var scheduleStarted = VaultOpenTelemetry.Start(activity);
            await ScheduleMediaIndexAsync(records, key);
            VaultOpenTelemetry.Record(activity, "gallery.load.schedule_media_index_ms", scheduleStarted);
            driveOperationPlayer.Start();
            return records;
        }
        finally { VaultOpenTelemetry.Record(activity, "gallery.load.elapsed_ms", started); }
    }
    /// <summary>Parses ComfyUI metadata once for records imported before (or with an older) parser.</summary>
    private async Task BackfillGenerationAsync(List<ImageRecord> records, byte[] key, CancellationToken cancellationToken = default)
    {
        var staleRecordIndices = records.Select((record, index) => (record, index))
            .Where(item => item.record.MetadataVersion < ComfyMetadata.Version).ToList();
        if (System.Diagnostics.Activity.Current is { OperationName: "gallery.load" } activity)
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
                await galleryStore.UpdateAsync(refreshedImageRecord, key, preserveBrowseIndex: false);
                records[index] = refreshedImageRecord;
            }
            catch (Exception exception) when (exception is InvalidOperationException or CryptographicException or StoredMetadataFormatException)
            {
                throw new InvalidOperationException($"Could not backfill generation metadata for media item '{record.Id}'.", exception);
            }
        }
    }

    private Task<List<ImageRecord>> ListWithKeyAsync(byte[] key) => galleryStore.ReadRecordsAsync(key);

    private Task BackfillFingerprintsAsync(byte[] key) => galleryStore.BackfillFingerprintsAsync(key);

    public async Task<ImageRecord> UpdateAsync(string id, Func<ImageRecordEdit, ImageRecordEdit> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.Gate.WaitAsync();
        try { return await UpdateRecordAsync(id, update, key.Bytes); }
        finally { galleryStore.Gate.Release(); }
    }

    public async Task<List<ImageRecord>> UpdateManyAsync(IEnumerable<string> ids, Func<ImageRecordEdit, ImageRecordEdit> update)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(update);
        // Callbacks can mutate the caller's ID collection; capture the batch before invoking them.
        var uniqueImageIds = ids.Distinct(StringComparer.Ordinal).ToList();
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.Gate.WaitAsync();
        try { return await GalleryRecordUpdater.UpdateBatchAsync(uniqueImageIds, id => UpdateRecordAsync(id, update, key.Bytes)); }
        finally { galleryStore.Gate.Release(); }
    }

    private async Task<ImageRecord> UpdateRecordAsync(string imageId, Func<ImageRecordEdit, ImageRecordEdit> update, byte[] key)
    {
        var record = await FindAsync(imageId, key);
        var updated = ApplyEdit(record, update);
        // Placement is changed only by atomic hierarchy operations, never by legacy edit callbacks.
        if (!updated.Collections.SequenceEqual(record.Collections)
            && (record.SetId is not null || await galleryStore.ReadSettingsAsync<CollectionCatalog>(
                CollectionCatalog.SettingId, CollectionCatalog.SettingPurpose, key) is not null))
            throw new InvalidOperationException("Choose a destination set to change organization.");
        if (record.Tags == updated.Tags) await galleryStore.UpdateAsync(updated, key);
        else await SaveEditedTagsAsync(record, updated, key);
        return updated;
    }

    public Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids) =>
        removalManager.PrepareRemovalAsync(ids);

    public Task RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval = null) =>
        removalManager.RemoveAsync(removalItems, existingAction, missingAction, restoreFolder, reportRemoval);

    public async Task MoveAsync(string id, string folder, string name)
    {
        StoredImageMoveValidator.ValidateName(name);
        var destination = StoredImageMover.NormalizeDestination(folder, name);
        StoredImageMoveValidator.ValidateDestination(destination);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await MoveUnderGateAsync(id, destination, key.Bytes);
        driveOperationPlayer.Start();
    }

    private async Task MoveUnderGateAsync(string id, StoredImageMoveDestination destination, byte[] key)
    {
        await galleryStore.Gate.WaitAsync();
        try
        {
            var record = await FindAsync(id, key);
            var target = StoredImageMover.TargetPath(record, destination);
            StoredImageMoveValidator.ValidateDifferentPath(record.StoredPath, target);
            await MoveStoredImageAsync(record, target, destination.Name, key);
        }
        finally { galleryStore.Gate.Release(); }
    }

    // The old copy is deleted by the drive queue after the catalog commits. Moving an image back onto a path
    // still queued for deletion cancels that delete and keeps the file instead of copying it again.
    private async Task MoveStoredImageAsync(ImageRecord record, string target, string newName, byte[] key)
    {
        var deleteOldCopy = CreateDriveOperation(DriveOperationKind.DeleteFile, record.StoredPath, record.Id, key);
        var queuedDelete = await galleryStore.FindPendingDriveOperationAsync(
            DriveOperationKind.DeleteFile, DriveOperationProcessor.PathFingerprint(key, target), key);
        if (queuedDelete is not null)
        {
            if (queuedDelete.ImageId == record.Id && File.Exists(target))
            {
                await VerifyCiphertextAsync(target, record.Id, key);
                await galleryStore.UpdateImageAndEnqueueAsync(record with { StoredPath = target, Name = newName }, key,
                    deleteOldCopy, queuedDelete.Sequence);
                return;
            }
            if (await CanDeleteQueuedFileAsync(galleryStore, queuedDelete.ImageId, target, key))
            {
                await DeleteQueuedFileAsync(galleryStore, queuedDelete, target, key);
                await galleryStore.RemoveDriveOperationAsync(queuedDelete.Sequence, key);
            }
        }

        var movedRecord = StoredImageMover.CopyToDestination(record, target, newName);
        try { await galleryStore.UpdateImageAndEnqueueAsync(movedRecord, key, deleteOldCopy); }
        catch
        {
            StoredImageMover.RollBackCopiedDestination(target);
            throw;
        }
    }

    private Task<ImageRecord> FindAsync(string id, byte[] key) => galleryStore.ReadRecordAsync(id, key);
}
