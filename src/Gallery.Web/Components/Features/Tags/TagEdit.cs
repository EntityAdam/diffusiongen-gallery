using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Features.Tags;

public sealed record TagEdit(IReadOnlyList<string> Ids, IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    internal ImageRecordEdit Apply(IGalleryRecordEditor records, ImageRecordEdit image)
    {
        var existing = Split(image.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = image.Tags;
        foreach (var tag in Removed.Where(existing.Contains))
            tags = records.RemoveTag(tags, tag);
        var missing = string.Join(", ", Added.Where(tag => !existing.Contains(tag)));
        return image with { Tags = missing.Length == 0 ? tags : records.MergeTags(tags, missing) };
    }

    internal static string[] Split(string tags) =>
        tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
