using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Ports;
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
    private readonly CollectionOrganizer collectionOrganizer;
    private readonly GalleryBackupWriter backupWriter;
    private readonly DuplicateScanner duplicateScanner;
    private readonly GalleryDriveQueue driveQueue;
    private readonly MediaStorageIndex mediaStorageIndex;
    private readonly TagCatalogManager tagCatalogManager;
    private readonly GalleryCatalogLoader catalogLoader;
    private readonly StoredMediaMover storedMediaMover;
    private readonly VaultLocationCoordinator vaultLocations;

    internal GalleryService(IGalleryStore galleryStore, VaultStore vaultStore, VaultSession vaultSession, VideoTools? videoTools = null,
        VisionManager? visionManager = null, IGalleryStoreFactory? storeFactory = null, HttpClient? httpClient = null)
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
        collectionOrganizer = new CollectionOrganizer(galleryLifetime, vaultSession);
        backupWriter = new GalleryBackupWriter(galleryLifetime, vaultSession, httpClient);
        duplicateScanner = new DuplicateScanner(galleryLifetime, vaultSession);
        driveQueue = new GalleryDriveQueue(galleryLifetime, vaultSession);
        mediaStorageIndex = new MediaStorageIndex(galleryLifetime, vaultSession, driveQueue);
        removalManager = new RemovalManager(galleryLifetime, vaultSession, driveQueue.Start);
        tagCatalogManager = new TagCatalogManager(galleryLifetime, vaultSession);
        catalogLoader = new GalleryCatalogLoader(galleryLifetime, vaultSession, mediaStorageIndex, driveQueue);
        storedMediaMover = new StoredMediaMover(galleryLifetime, vaultSession, driveQueue);
        vaultLocations = new VaultLocationCoordinator(galleryLifetime, vaultSession, driveQueue);
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
            var image = await galleryStore.ReadRecordAsync(id, key.Bytes);
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
        var store = galleryStore;
        var record = await store.ReadRecordAsync(imageId, key);
        var updated = ApplyEdit(record, update);
        // Placement is changed only by atomic hierarchy operations, never by legacy edit callbacks.
        if (!updated.Collections.SequenceEqual(record.Collections)
            && (record.SetId is not null || await store.ReadSettingsAsync<CollectionCatalog>(
                CollectionCatalog.SettingId, CollectionCatalog.SettingPurpose, key) is not null))
            throw new InvalidOperationException("Choose a destination set to change organization.");
        if (record.Tags == updated.Tags) await store.UpdateAsync(updated, key);
        else await TagCatalogManager.SaveEditedTagsAsync(store, record, updated, key);
        return updated;
    }

    public Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids) =>
        removalManager.PrepareRemovalAsync(ids);

    public Task RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval = null) =>
        removalManager.RemoveAsync(removalItems, existingAction, missingAction, restoreFolder, reportRemoval);
}
