using Gallery.Core.Features.Tags;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Tags.CatalogProjectors;

internal static class TagCatalogProjector
{
    internal static (TagCatalog Catalog, IReadOnlyList<TagDefinition> Definitions) Project(
        TagCatalog tagCatalog, IReadOnlyList<ImageRecord> records)
    {
        var names = TagCatalogStore.ReconcileNames(tagCatalog, records);
        var mediaCountsByTagName = records.SelectMany(image => TagCatalogStore.TagsOf(image).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return (new(names), names.Select(name => new TagDefinition(name, mediaCountsByTagName.GetValueOrDefault(name))).ToArray());
    }
}
