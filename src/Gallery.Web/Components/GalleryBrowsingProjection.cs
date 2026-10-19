using System.Collections.Immutable;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components;

internal sealed record GalleryBrowsingProjection(
    IReadOnlyList<ImageRecord> Images,
    IReadOnlyDictionary<string, PromptGroup> PromptGroupsByImageId)
{
    internal static GalleryBrowsingProjection Empty { get; } =
        new(ImmutableArray<ImageRecord>.Empty, ImmutableDictionary<string, PromptGroup>.Empty);
}

internal static class GalleryBrowsingProjector
{
    public static GalleryBrowsingProjection Create(GalleryService galleryService,
        IReadOnlyList<ImageRecord> records, GalleryFilter galleryFilter, bool groupByPrompt)
    {
        var filtered = galleryService.Filter(records, galleryFilter);
        var groups = groupByPrompt ? galleryService.GroupByPrompt(filtered) : [];
        var images = groupByPrompt
            ? groups.SelectMany(group => group.Images).ToImmutableArray()
            : filtered.ToImmutableArray();
        var promptGroupsByImageId = groups
            .SelectMany(group => group.Images.Select(image => (image.Id, Group: group)))
            .ToImmutableDictionary(item => item.Id, item => item.Group);
        return new(images, promptGroupsByImageId);
    }
}
