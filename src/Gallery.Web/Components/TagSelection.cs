using Gallery.Core.Services;

namespace Gallery.Web.Components;

internal static class TagSelection
{
    // Apply only the delta, preserving mixed assignments during a bulk edit.
    internal static string Apply(GalleryService gallery, string tags, string originalSelection, string selected)
    {
        var desired = Split(selected);
        var original = Split(originalSelection);
        foreach (var removed in original.Except(desired, StringComparer.OrdinalIgnoreCase))
            tags = gallery.RemoveTag(tags, removed);
        return gallery.MergeTags(tags, string.Join(", ", desired.Except(original, StringComparer.OrdinalIgnoreCase)));
    }

    private static string[] Split(string tags) => tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
