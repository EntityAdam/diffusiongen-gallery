using System.Collections.Immutable;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components;

internal sealed record GalleryBrowsingProjection(
    IReadOnlyList<ImageRecord> Images,
    IReadOnlyDictionary<string, PromptGroup> PromptGroupsByImageId,
    IReadOnlyDictionary<string, DiffusionModelGroup> DiffusionModelGroupsByImageId)
{
    internal static GalleryBrowsingProjection Empty { get; } =
        new(ImmutableArray<ImageRecord>.Empty, ImmutableDictionary<string, PromptGroup>.Empty,
            ImmutableDictionary<string, DiffusionModelGroup>.Empty);
}

internal static class GalleryBrowsingProjector
{
    public static GalleryBrowsingProjection Create(GalleryService galleryService,
        IReadOnlyList<ImageRecord> records, GalleryFilter galleryFilter, bool groupByPrompt) =>
        Create(galleryService, records, galleryFilter, groupByPrompt ? "prompt" : "none");

    public static GalleryBrowsingProjection Create(GalleryService galleryService,
        IReadOnlyList<ImageRecord> records, GalleryFilter galleryFilter, string grouping)
    {
        var filtered = galleryService.Filter(records, galleryFilter);
        var promptGroups = grouping == "prompt" ? galleryService.GroupByPrompt(filtered) : [];
        var diffusionModelGroups = grouping == "model" ? galleryService.GroupByDiffusionModel(filtered) : [];
        var images = grouping switch
        {
            "prompt" => promptGroups.SelectMany(group => group.Images).ToImmutableArray(),
            "model" => diffusionModelGroups.SelectMany(group => group.Images).ToImmutableArray(),
            _ => filtered.ToImmutableArray()
        };
        var promptGroupsByImageId = promptGroups
            .SelectMany(group => group.Images.Select(image => (image.Id, Group: group)))
            .ToImmutableDictionary(item => item.Id, item => item.Group);
        var diffusionModelGroupsByImageId = diffusionModelGroups
            .SelectMany(group => group.Images.Select(image => (image.Id, Group: group)))
            .ToImmutableDictionary(item => item.Id, item => item.Group);
        return new(images, promptGroupsByImageId, diffusionModelGroupsByImageId);
    }
}
