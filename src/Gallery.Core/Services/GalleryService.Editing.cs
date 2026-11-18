using Gallery.Core.Models;
using Gallery.Core.Services.Processors;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public ImageRecordEdit ApplyReviewDecision(ImageRecordEdit image, ReviewAction action, int rating = 0) =>
        GalleryRecordEditor.ApplyReviewDecision(image, action, rating);

    public ReviewState CaptureReviewState(ImageRecord image) => GalleryRecordEditor.CaptureReviewState(image);
    public ReviewState CaptureReviewState(ImageRecordEdit image) => GalleryRecordEditor.CaptureReviewState(image);
    public Task<ImageRecord> RestoreReviewStateAsync(string id, ReviewState state) => recordEditor.RestoreReviewStateAsync(id, state);

    public ImageRecordEdit SetCuration(ImageRecordEdit image, int? rating, bool? reviewed) =>
        GalleryRecordEditor.SetCuration(image, rating, reviewed);

    public ImageRecordEdit SetDeletionMark(ImageRecordEdit image, bool markedForDeletion) =>
        GalleryRecordEditor.SetDeletionMark(image, markedForDeletion);

    public ImageRecordEdit SetOrganization(ImageRecordEdit image, int rating, bool reviewed, string? collection) =>
        GalleryRecordEditor.SetOrganization(image, rating, reviewed, collection);

    public ImageRecordEdit AddCollection(ImageRecordEdit image, string collection) => GalleryRecordEditor.AddCollection(image, collection);
    public ImageRecordEdit RemoveCollection(ImageRecordEdit image, string collection) => GalleryRecordEditor.RemoveCollection(image, collection);

    public ImageRecord ApplyEdit(ImageRecord image, Func<ImageRecordEdit, ImageRecordEdit> update) =>
        GalleryRecordEditor.ApplyEdit(image, update);

    public string MergeTags(string existing, string additions) => LibraryRules.MergeTags(existing, additions);
    public string RemoveTag(string existing, string tag) => LibraryRules.RemoveTag(existing, tag);

    public Task<ImageRecord> UpdateAsync(string id, Func<ImageRecordEdit, ImageRecordEdit> update) =>
        recordEditor.UpdateAsync(id, update);

    public Task<List<ImageRecord>> UpdateManyAsync(IEnumerable<string> ids, Func<ImageRecordEdit, ImageRecordEdit> update) =>
        recordEditor.UpdateManyAsync(ids, update);
}
