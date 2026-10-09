using Gallery.Models;

namespace Gallery.Core;

public enum ReviewAction { Keep, Favorite, MarkDeletion, Rate }

public sealed record ReviewState(bool Reviewed, bool Favorite, bool MarkedForDeletion, int Rating)
{
    public static ReviewState Capture(ImageRecord image) => new(image.Reviewed, image.Favorite, image.MarkedForDeletion, image.Rating);
    public void Restore(ImageRecord image)
    {
        image.Reviewed = Reviewed;
        image.Favorite = Favorite;
        image.MarkedForDeletion = MarkedForDeletion;
        image.Rating = Rating;
    }
}

public static class ReviewDecision
{
    public static void Apply(ImageRecord image, ReviewAction action, int rating = 0)
    {
        if (!Enum.IsDefined(action)) throw new InvalidOperationException("Invalid review action.");
        if (action == ReviewAction.Rate && rating is < 1 or > 5)
            throw new InvalidOperationException("Choose a rating between 1 and 5.");
        image.Reviewed = true;
        if (action == ReviewAction.MarkDeletion) image.MarkedForDeletion = true;
        else
        {
            image.MarkedForDeletion = false;
            if (action == ReviewAction.Favorite) image.Favorite = true;
            if (action == ReviewAction.Rate) image.Rating = rating;
        }
    }
}
