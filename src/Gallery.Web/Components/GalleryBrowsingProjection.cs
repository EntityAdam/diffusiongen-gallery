using System.Collections.Immutable;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components;

internal sealed record GalleryBrowsingProjection
{
    public GalleryBrowsingProjection(ImmutableArray<ImageRecord> imageRecords,
        ImmutableDictionary<string, PromptGroup> promptGroupsByImageId,
        ImmutableDictionary<string, DiffusionModelGroup> diffusionModelGroupsByImageId)
    {
        // Box once so repeated interface reads retain the snapshot's identity.
        Images = imageRecords;
        PromptGroupsByImageId = promptGroupsByImageId;
        DiffusionModelGroupsByImageId = diffusionModelGroupsByImageId;
    }

    public IReadOnlyList<ImageRecord> Images { get; }
    public ImmutableDictionary<string, PromptGroup> PromptGroupsByImageId { get; }
    public ImmutableDictionary<string, DiffusionModelGroup> DiffusionModelGroupsByImageId { get; }

    internal static GalleryBrowsingProjection Empty { get; } =
        new(ImmutableArray<ImageRecord>.Empty, ImmutableDictionary<string, PromptGroup>.Empty,
            ImmutableDictionary<string, DiffusionModelGroup>.Empty);
}

internal static class GalleryBrowsingProjector
{
    private const string PromptGrouping = "prompt";
    private const string ModelGrouping = "model";

    public static GalleryBrowsingProjection CreateGroupedByPrompt(GalleryService galleryService,
        IReadOnlyList<ImageRecord> sourceImages, GalleryFilter galleryFilter) =>
        Create(galleryService, sourceImages, galleryFilter, PromptGrouping);

    public static GalleryBrowsingProjection CreateUngrouped(GalleryService galleryService,
        IReadOnlyList<ImageRecord> sourceImages, GalleryFilter galleryFilter) =>
        Create(galleryService, sourceImages, galleryFilter, "none");

    public static GalleryBrowsingProjection Create(GalleryService galleryService,
        IReadOnlyList<ImageRecord> sourceImages, GalleryFilter galleryFilter, string grouping)
    {
        var filteredImages = galleryService.Filter(sourceImages, galleryFilter);
        var promptGroups = grouping == PromptGrouping ? galleryService.GroupByPrompt(filteredImages) : [];
        var diffusionModelGroups = grouping == ModelGrouping ? galleryService.GroupByDiffusionModel(filteredImages) : [];
        var images = SelectImages(grouping, filteredImages, promptGroups, diffusionModelGroups);
        var promptGroupsByImageId = IndexPromptGroupsByImageId(promptGroups);
        var diffusionModelGroupsByImageId = IndexDiffusionModelGroupsByImageId(diffusionModelGroups);
        return new(images, promptGroupsByImageId, diffusionModelGroupsByImageId);
    }

    private static ImmutableArray<ImageRecord> SelectImages(string grouping, IReadOnlyList<ImageRecord> filteredImages,
        IReadOnlyList<PromptGroup> promptGroups, IReadOnlyList<DiffusionModelGroup> diffusionModelGroups) =>
        grouping switch
        {
            PromptGrouping => promptGroups.SelectMany(promptGroup => promptGroup.Images).ToImmutableArray(),
            ModelGrouping => diffusionModelGroups.SelectMany(diffusionModelGroup => diffusionModelGroup.Images).ToImmutableArray(),
            _ => filteredImages.ToImmutableArray()
        };

    private static ImmutableDictionary<string, PromptGroup> IndexPromptGroupsByImageId(IEnumerable<PromptGroup> promptGroups) =>
        promptGroups
            .SelectMany(promptGroup => promptGroup.Images.Select(imageRecord => (imageRecord.Id, Group: promptGroup)))
            .ToImmutableDictionary(item => item.Id, item => item.Group);

    private static ImmutableDictionary<string, DiffusionModelGroup> IndexDiffusionModelGroupsByImageId(
        IEnumerable<DiffusionModelGroup> diffusionModelGroups) =>
        diffusionModelGroups
            .SelectMany(diffusionModelGroup => diffusionModelGroup.Images.Select(imageRecord => (imageRecord.Id, Group: diffusionModelGroup)))
            .ToImmutableDictionary(item => item.Id, item => item.Group);
}
