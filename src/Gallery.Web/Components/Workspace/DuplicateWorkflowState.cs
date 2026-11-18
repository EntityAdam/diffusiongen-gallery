using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Workspace;

public sealed class DuplicateWorkflowState(IGalleryDuplicates duplicates, IGalleryVault vault, IGalleryMedia media, WorkspaceCatalog catalog) : IDisposable
{
    internal DuplicateCatalog Results { get; } = new();
    internal bool HasScan => Results.Scan is not null;
    internal IReadOnlyList<DuplicateGroup> Groups => Results.Groups;
    internal bool Active { get; set; }

    internal async Task LoadSummaryAsync()
    {
        await Results.LoadScanAsync(duplicates);
        Results.UpdateGroups(catalog.ImagesById);
    }

    internal async Task OpenAsync()
    {
        await LoadSummaryAsync();
        await Results.LoadThumbnailsAsync(vault, media);
    }

    internal Task CatalogChangedAsync()
    {
        Results.UpdateGroups(catalog.ImagesById);
        return Active ? Results.LoadThumbnailsAsync(vault, media) : Task.CompletedTask;
    }

    public void Dispose() => Results.Dispose();
}
