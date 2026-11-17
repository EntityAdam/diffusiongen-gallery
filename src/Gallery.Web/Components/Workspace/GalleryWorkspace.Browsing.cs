using Gallery.Core.Models;

namespace Gallery.Web.Components.Workspace;

public partial class GalleryWorkspace
{
    private WorkspaceCatalog? catalog;
    private GalleryBrowserState? browser;
    private GalleryBrowser? galleryBrowser;
    private WorkspaceCatalog Catalog
    {
        get
        {
            if (catalog is not null) return catalog;
            catalog = new(Gallery, Logger, action => InvokeAsync(action));
            catalog.Changed += CatalogChangedAsync;
            return catalog;
        }
    }
    private GalleryBrowserState Browser => browser ??= new(Gallery, Catalog, Logger, RefreshWorkspace, () => SavePreferencesAsync());

    internal Task ReloadAsync() => Catalog.ReloadAsync();
    private async Task<(CollectionCatalog Catalog, IReadOnlyList<ImageRecord> Images)> ReloadCollectionsAsync()
    {
        await ReloadAsync();
        return (Catalog.Collections, Catalog.Images);
    }
    private Task RetryCatalogLoadAsync() { Catalog.RetryLoad(); return Task.CompletedTask; }
    private async Task CatalogChangedAsync()
    {
        if (disposed) return;
        var currentCollection = previousCollections.Collections.FirstOrDefault(item => item.Name.Equals(Browser.Filter.Collection, StringComparison.OrdinalIgnoreCase));
        if (currentCollection is not null && Catalog.Collections.Collections.FirstOrDefault(item => item.Id == currentCollection.Id) is { } renamed)
            Browser.Filter = Browser.Filter with { Collection = renamed.Name };
        previousCollections = Catalog.Collections;
        duplicates.UpdateGroups(Catalog.ImagesById);
        if (workflow == "duplicates") await duplicates.LoadThumbnailsAsync(Gallery);
        selected = selected is null ? null : Catalog.ImagesById.GetValueOrDefault(selected.Id);
        if (selected is null) { preview = ""; await ReleaseVideoAsync(); }
        await Browser.CatalogChangedAsync();
        RefreshWorkspace();
    }
    private CollectionCatalog previousCollections = CollectionCatalog.Empty;
}
