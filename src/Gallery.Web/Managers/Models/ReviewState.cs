using Gallery.Web.Models;

namespace Gallery.Core;

public enum ReviewAction { Keep, Favorite, MarkDeletion, Rate }

public sealed record ReviewState(bool Reviewed, bool Favorite, bool MarkedForDeletion, int Rating)
{
    internal static ReviewState Capture(ImageRecord imageRecord) => new(imageRecord.Reviewed, imageRecord.Favorite, imageRecord.MarkedForDeletion, imageRecord.Rating);
}

