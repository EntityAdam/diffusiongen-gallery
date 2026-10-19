using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class ReviewDecision
{
    public static ReviewState Capture(ImageRecord imageRecord) =>
        new(imageRecord.IsReviewed, imageRecord.IsFavorite, imageRecord.IsMarkedForDeletion, imageRecord.Rating);

    public static ReviewState Capture(ImageRecordEdit imageRecordEdit) =>
        new(imageRecordEdit.IsReviewed, imageRecordEdit.IsFavorite, imageRecordEdit.IsMarkedForDeletion, imageRecordEdit.Rating);

    public static ImageRecord Restore(ImageRecord imageRecord, ReviewState reviewState) =>
        imageRecord with { IsReviewed = reviewState.IsReviewed, IsFavorite = reviewState.IsFavorite,
            IsMarkedForDeletion = reviewState.IsMarkedForDeletion, Rating = reviewState.Rating };

    public static ImageRecordEdit Restore(ImageRecordEdit imageRecordEdit, ReviewState reviewState) =>
        imageRecordEdit with { IsReviewed = reviewState.IsReviewed, IsFavorite = reviewState.IsFavorite,
            IsMarkedForDeletion = reviewState.IsMarkedForDeletion, Rating = reviewState.Rating };

    public static ImageRecordEdit Apply(ImageRecordEdit imageRecordEdit, ReviewAction action, int rating = 0) =>
        imageRecordEdit with
        {
            IsReviewed = true,
            IsMarkedForDeletion = action == ReviewAction.MarkDeletion,
            IsFavorite = action == ReviewAction.Favorite || imageRecordEdit.IsFavorite,
            Rating = action == ReviewAction.Rate ? rating : imageRecordEdit.Rating
        };
}
