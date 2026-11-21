using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Curation.Validators;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Curation;

internal sealed class GalleryRecordEditor(
    GalleryLifetime galleryLifetime, VaultSession vaultSession, IGalleryRecordTagCommitter recordTagCommitter) : IGalleryRecordEditor
{
    public ImageRecordEdit ApplyReviewDecision(ImageRecordEdit image, ReviewAction action, int rating = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!Enum.IsDefined(action))
            throw new InvalidOperationException(
                $"Review action value {(int)action} is invalid; choose Keep, Favorite, MarkDeletion, or Rate.");
        if (HasInvalidReviewRating(action, rating))
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
        var store = galleryLifetime.Current.GalleryStore;
        await store.Gate.WaitAsync();
        try
        {
            var image = await store.ReadRecordAsync(id, key.Bytes);
            var restored = ReviewDecision.Restore(image, state);
            await store.UpdateAsync(restored, key.Bytes);
            return restored;
        }
        finally { store.Gate.Release(); }
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

    public ImageRecord ApplyEdit(ImageRecord image, Func<ImageRecordEdit, ImageRecordEdit> update)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(update);
        var edit = update(new ImageRecordEdit(image));
        ImageRecordEditValidator.Validate(edit, image.Id);
        return LibraryRules.ApplyEdit(image, edit);
    }

    public async Task<ImageRecord> UpdateAsync(string id, Func<ImageRecordEdit, ImageRecordEdit> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        await store.Gate.WaitAsync();
        try { return await UpdateRecordAsync(store, id, update, key.Bytes); }
        finally { store.Gate.Release(); }
    }

    public async Task<List<ImageRecord>> UpdateManyAsync(IEnumerable<string> ids, Func<ImageRecordEdit, ImageRecordEdit> update)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(update);
        // Callbacks can mutate the caller's ID collection; capture the batch before invoking them.
        var uniqueImageIds = ids.Distinct(StringComparer.Ordinal).ToList();
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        await store.Gate.WaitAsync();
        try { return await UpdateBatchAsync(uniqueImageIds, id => UpdateRecordAsync(store, id, update, key.Bytes)); }
        finally { store.Gate.Release(); }
    }

    private async Task<ImageRecord> UpdateRecordAsync(IGalleryStore store, string imageId, Func<ImageRecordEdit, ImageRecordEdit> update, byte[] key)
    {
        var record = await store.ReadRecordAsync(imageId, key);
        var updated = ApplyEdit(record, update);
        if (updated == record) return record;
        // Placement is changed only by atomic hierarchy operations, never by legacy edit callbacks.
        if (!updated.Collections.SequenceEqual(record.Collections)
            && (record.SetId is not null || await store.ReadSettingsAsync<CollectionCatalog>(
                CollectionCatalog.SettingId, CollectionCatalog.SettingPurpose, key) is not null))
            throw new InvalidOperationException("Choose a destination set to change organization.");
        if (record.Tags == updated.Tags) await store.UpdateAsync(updated, key);
        else await recordTagCommitter.CommitEditedTagsAsync(store, record, updated, key);
        return updated;
    }

    /// <summary>Unmarks the kept copies, marks the other copies for deletion, and returns the committed records.</summary>
    public async Task<List<ImageRecord>> ApplyDuplicateDecisionAsync(IReadOnlyCollection<string> groupImageIds, IReadOnlySet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(groupImageIds);
        ArgumentNullException.ThrowIfNull(keep);
        var capturedGroupImageIds = groupImageIds.ToArray();
        var retainedIds = ValidateDuplicateKeepers(capturedGroupImageIds, keep);
        var uniqueGroupImageIds = capturedGroupImageIds.Distinct(StringComparer.Ordinal).ToArray();
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        await store.Gate.WaitAsync();
        try
        {
            var groupRecords = new List<ImageRecord>(uniqueGroupImageIds.Length);
            var failures = new List<Exception>();
            foreach (var imageId in uniqueGroupImageIds)
            {
                try { groupRecords.Add(await store.ReadRecordAsync(imageId, key.Bytes)); }
                catch (Exception exception) when (exception is GalleryException or InvalidOperationException
                    or CryptographicException or IOException)
                {
                    failures.Add(new InvalidOperationException($"Could not update gallery record '{imageId}'.", exception));
                }
            }
            ThrowFailures(failures);

            var updatedRecords = groupRecords.Select(record =>
                ApplyEdit(record, image => SetDeletionMark(image, !retainedIds.Contains(record.Id)))).ToList();
            var changedRecords = updatedRecords.Where((record, index) => record != groupRecords[index]).ToArray();
            if (changedRecords.Length > 0) await store.UpdateRecordsAsync(changedRecords, key.Bytes);
            return updatedRecords;
        }
        finally { store.Gate.Release(); }
    }

    private static HashSet<string> ValidateDuplicateKeepers(string[] groupImageIds, IReadOnlySet<string> keep)
    {
        if (keep.Count == 0) throw new InvalidOperationException("Keep at least one media item in the group.");
        var groupIds = groupImageIds.ToHashSet(StringComparer.Ordinal);
        var invalidKeeperIds = keep.Where(imageId => !groupIds.Contains(imageId)).ToArray();
        if (invalidKeeperIds.Length > 0)
            throw new InvalidOperationException(
                $"Kept media items [{string.Join(", ", invalidKeeperIds)}] must belong to the group. Valid media IDs: [{string.Join(", ", groupImageIds)}].");
        return keep.ToHashSet(StringComparer.Ordinal);
    }

    private static bool HasInvalidReviewRating(ReviewAction action, int rating) =>
        action == ReviewAction.Rate && rating is < 1 or > 5;

    private static async Task<List<ImageRecord>> UpdateBatchAsync(
        IReadOnlyList<string> ids, Func<string, Task<ImageRecord>> update)
    {
        List<ImageRecord> records;
        List<Exception> failures;
        (records, failures) = await UpdateRecordsAsync(ids, update);
        ThrowFailures(failures);
        return records;
    }

    private static async Task<(List<ImageRecord> Records, List<Exception> Failures)> UpdateRecordsAsync(
        IReadOnlyList<string> ids, Func<string, Task<ImageRecord>> update)
    {
        var records = new List<ImageRecord>(ids.Count);
        var failures = new List<Exception>();
        foreach (var id in ids)
        {
            try { records.Add(await update(id)); }
            catch (Exception exception) when (exception is GalleryException or InvalidOperationException or CryptographicException or IOException)
            {
                failures.Add(new InvalidOperationException($"Could not update gallery record '{id}'.", exception));
            }
        }
        return (records, failures);
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 0) return;
        var summary = string.Join(", ", failures.Select(exception => exception.Message));
        throw new AggregateException($"Failed to update {failures.Count} gallery record(s): {summary}", failures);
    }

    public string MergeTags(string existing, string additions) => LibraryRules.MergeTags(existing, additions);

    public string RemoveTag(string existing, string tag) => LibraryRules.RemoveTag(existing, tag);
}
