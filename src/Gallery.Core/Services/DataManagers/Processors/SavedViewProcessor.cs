using Gallery.Core.Exceptions;
using Gallery.Core.Models;

namespace Gallery.Core.Services.DataManagers.Processors;

internal static class SavedViewProcessor
{
    public static void Replace(List<SavedView> views, string name, GalleryFilter galleryFilter)
    {
        views.RemoveAll(savedView => savedView.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        views.Add(new SavedView(name, galleryFilter with { }));
    }

    public static void Remove(List<SavedView> views, string name) =>
        views.RemoveAll(savedView => savedView.Name == name);
}
