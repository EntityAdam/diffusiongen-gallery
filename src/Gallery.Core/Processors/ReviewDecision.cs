using Gallery.Models;

namespace Gallery.Core.Processors;

internal static class ReviewDecision
{
    internal static ImageRecordEdit Restore(ImageRecordEdit image, ReviewState state) =>
        image with { Reviewed = state.Reviewed, Favorite = state.Favorite,
            MarkedForDeletion = state.MarkedForDeletion, Rating = state.Rating };

    internal static ImageRecordEdit Apply(ImageRecordEdit imageRecordEdit, ReviewAction action, int rating = 0) =>
        imageRecordEdit with
        {
            Reviewed = true,
            MarkedForDeletion = action == ReviewAction.MarkDeletion,
            Favorite = action == ReviewAction.Favorite || imageRecordEdit.Favorite,
            Rating = action == ReviewAction.Rate ? rating : imageRecordEdit.Rating
        };
}
