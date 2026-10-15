using Gallery.Core.Models;

namespace Gallery.Core.Processors;

public static class ReviewDecision
{
    public static ImageRecordEdit Restore(ImageRecordEdit image, ReviewState state) =>
        image with { Reviewed = state.Reviewed, Favorite = state.Favorite,
            MarkedForDeletion = state.MarkedForDeletion, Rating = state.Rating };

    public static ImageRecordEdit Apply(ImageRecordEdit imageRecordEdit, ReviewAction action, int rating = 0) =>
        imageRecordEdit with
        {
            Reviewed = true,
            MarkedForDeletion = action == ReviewAction.MarkDeletion,
            Favorite = action == ReviewAction.Favorite || imageRecordEdit.Favorite,
            Rating = action == ReviewAction.Rate ? rating : imageRecordEdit.Rating
        };
}
