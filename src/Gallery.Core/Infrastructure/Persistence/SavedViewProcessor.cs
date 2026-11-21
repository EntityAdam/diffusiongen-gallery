using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence;

internal static class SavedViewProcessor
{
    public static void Replace(List<SavedView> views, string name, GalleryFilter galleryFilter)
    {
        Remove(views, name);
        views.Add(new SavedView(name, galleryFilter with { }));
    }

    public static void Remove(List<SavedView> views, string name) =>
        views.RemoveAll(savedView => savedView.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
