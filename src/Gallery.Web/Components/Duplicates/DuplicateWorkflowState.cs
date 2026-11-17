using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Workspace;

namespace Gallery.Web.Components.Duplicates;

public sealed class DuplicateWorkflowState(GalleryService gallery, WorkspaceCatalog catalog) : IDisposable
{
    internal DuplicateCatalog Results { get; } = new();
    internal bool HasScan => Results.Scan is not null;
    internal IReadOnlyList<DuplicateGroup> Groups => Results.Groups;
    internal bool Active { get; set; }

    internal async Task LoadSummaryAsync()
    {
        await Results.LoadScanAsync(gallery);
        Results.UpdateGroups(catalog.ImagesById);
    }

    internal async Task OpenAsync()
    {
        await LoadSummaryAsync();
        await Results.LoadThumbnailsAsync(gallery);
    }

    internal Task CatalogChangedAsync()
    {
        Results.UpdateGroups(catalog.ImagesById);
        return Active ? Results.LoadThumbnailsAsync(gallery) : Task.CompletedTask;
    }

    public void Dispose() => Results.Dispose();
}
