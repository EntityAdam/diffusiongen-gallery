using Gallery.Core.Models;
using Gallery.Web.Components.Workspace;

namespace Gallery.Web.Components.Collections;

// Shared by the collections sidebar and the collection browser.
public sealed class CollectionBrowsingState(WorkspaceCatalog catalog)
{
    public CollectionLocation Location { get; private set; } = new();
    public bool IsBusy { get; set; }

    internal void Navigate(CollectionLocation location) => Location = location;

    internal Task CatalogChangedAsync()
    {
        if (Location.CollectionId is { } collectionId && !catalog.Collections.Collections.Any(item => item.Id == collectionId))
            Location = new();
        else if (Location.SetId is { } setId && !catalog.Collections.Sets.Any(item => item.Id == setId))
            Location = new(Location.CollectionId);
        return Task.CompletedTask;
    }

    internal async Task<(CollectionCatalog Catalog, IReadOnlyList<ImageRecord> Images)> ReloadAsync()
    {
        await catalog.ReloadAsync();
        return (catalog.Collections, catalog.Images);
    }
}
