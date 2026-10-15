using Gallery.Web.Models;

namespace Gallery.Web.Models;

public enum WebReviewAction { Keep, Favorite, MarkDeletion, Rate }

public sealed record WebReviewState(bool Reviewed, bool Favorite, bool MarkedForDeletion, int Rating);
