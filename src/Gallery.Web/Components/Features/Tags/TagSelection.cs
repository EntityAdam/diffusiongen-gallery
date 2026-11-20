using Gallery.Core.Services;

namespace Gallery.Web.Components.Features.Tags;

internal static class TagSelection
{
    internal static string AddMissing(IGalleryRecordEditor gallery, string tags, string selected)
    {
        var missing = string.Join(", ", Split(selected).Except(Split(tags), StringComparer.OrdinalIgnoreCase));
        return missing.Length == 0 ? tags : gallery.MergeTags(tags, missing);
    }

    // Apply only the delta, preserving mixed assignments during a bulk edit.
    internal static string Apply(IGalleryRecordEditor gallery, string tags, string originalSelection, string selected)
    {
        var desired = Split(selected);
        var original = Split(originalSelection);
        foreach (var removed in original.Except(desired, StringComparer.OrdinalIgnoreCase))
            tags = gallery.RemoveTag(tags, removed);
        return gallery.MergeTags(tags, string.Join(", ", desired.Except(original, StringComparer.OrdinalIgnoreCase)));
    }

    private static string[] Split(string tags) => tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
