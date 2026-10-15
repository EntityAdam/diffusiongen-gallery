using Gallery.Web.Exceptions;
using Gallery.Web.Models;

namespace Gallery.Web.Services.DataManagers.Processors;

internal static class SavedViewProcessor
{
    public static void Replace(List<WebSavedView> views, string name, WebGalleryFilter galleryFilter)
    {
        views.RemoveAll(savedView => savedView.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        views.Add(new WebSavedView(name, galleryFilter with { }));
    }

    public static void Remove(List<WebSavedView> views, string name) =>
        views.RemoveAll(savedView => savedView.Name == name);
}
