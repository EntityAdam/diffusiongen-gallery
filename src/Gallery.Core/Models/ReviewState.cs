namespace Gallery.Core.Models;

public enum ReviewAction { Keep, Favorite, MarkDeletion, Rate }

public sealed record ReviewState(bool Reviewed, bool Favorite, bool MarkedForDeletion, int Rating)
{
    public static ReviewState Capture(ImageRecord image) =>
        new(image.Reviewed, image.Favorite, image.MarkedForDeletion, image.Rating);
}
