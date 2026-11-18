using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Validates and persists changes to media metadata and review state.</summary>
public interface IGalleryRecordEditor
{
    ImageRecordEdit ApplyReviewDecision(ImageRecordEdit image, ReviewAction action, int rating = 0);
    ReviewState CaptureReviewState(ImageRecord image);
    ReviewState CaptureReviewState(ImageRecordEdit image);
    Task<ImageRecord> RestoreReviewStateAsync(string id, ReviewState state);
    ImageRecordEdit SetCuration(ImageRecordEdit image, int? rating, bool? reviewed);
    ImageRecordEdit SetDeletionMark(ImageRecordEdit image, bool markedForDeletion);
    ImageRecordEdit SetOrganization(ImageRecordEdit image, int rating, bool reviewed, string? collection);
    ImageRecordEdit AddCollection(ImageRecordEdit image, string collection);
    ImageRecordEdit RemoveCollection(ImageRecordEdit image, string collection);
    ImageRecord ApplyEdit(ImageRecord image, Func<ImageRecordEdit, ImageRecordEdit> update);
    string MergeTags(string existing, string additions);
    string RemoveTag(string existing, string tag);
    Task<ImageRecord> UpdateAsync(string id, Func<ImageRecordEdit, ImageRecordEdit> update);
    Task<List<ImageRecord>> UpdateManyAsync(IEnumerable<string> ids, Func<ImageRecordEdit, ImageRecordEdit> update);
}
