using Gallery.Core.Models;

namespace Gallery.Web.Components;

// Built once per immutable catalog/media snapshot and Trash visibility setting.
internal sealed class CollectionBrowsingIndex
{
    public required IReadOnlyDictionary<string, CollectionDefinition> Collections { get; init; }
    public required IReadOnlyDictionary<string, MediaSet> Sets { get; init; }
    public required ILookup<string, MediaSet> SetsByCollection { get; init; }
    public required ILookup<string?, ImageRecord> MediaBySet { get; init; }
    public required ILookup<string?, ImageRecord> MediaByCollection { get; init; }
    public required IReadOnlyList<ImageRecord> Images { get; init; }

    public static CollectionBrowsingIndex Create(CollectionCatalog catalog, IEnumerable<ImageRecord> source, bool includeTrash)
    {
        var sets = catalog.Sets.ToDictionary(set => set.Id);
        var images = source.Where(image => includeTrash || !image.IsMarkedForDeletion).ToArray();
        return new()
        {
            Collections = catalog.Collections.ToDictionary(collection => collection.Id),
            Sets = sets,
            SetsByCollection = catalog.Sets.OrderBy(set => set.Order).ToLookup(set => set.CollectionId),
            MediaBySet = images.ToLookup(image => image.SetId),
            MediaByCollection = images.ToLookup(image => image.SetId is { } id && sets.TryGetValue(id, out var set)
                ? set.CollectionId : null),
            Images = images
        };
    }
}
