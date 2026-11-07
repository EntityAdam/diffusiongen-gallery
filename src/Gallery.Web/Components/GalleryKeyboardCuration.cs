using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components;

internal static class GalleryKeyboardCuration
{
    public static Func<ImageRecordEdit, ImageRecordEdit> Rating(GalleryService gallery, int value) => current => gallery.SetCuration(current, value, null);

    public static Func<ImageRecordEdit, ImageRecordEdit>? ForKey(GalleryService gallery, string key, ImageRecord image) => key switch
    {
        "0" or "1" or "2" or "3" or "4" or "5" => Rating(gallery, key[0] - '0'),
        "f" => current => current with { IsFavorite = !image.IsFavorite },
        "d" => current => gallery.SetDeletionMark(current, !image.IsMarkedForDeletion),
        _ => null
    };

    public static string Message(string key, ImageRecord image) => key switch
    {
        "f" => image.IsFavorite ? "Removed from favorites." : "Added to favorites.",
        "d" => image.IsMarkedForDeletion ? "Deletion mark cleared." : "Marked for deletion.",
        "0" => "Rating cleared.",
        _ => $"Rated {key} stars."
    };
}
