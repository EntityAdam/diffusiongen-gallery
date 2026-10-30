using Gallery.Core.Models;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    private bool catalogPartial;
    private bool catalogLoading;
    private string catalogLoadError = "";
    private long catalogGeneration;
    private CancellationTokenSource? catalogCancellation;
    private Task catalogLoad = Task.CompletedTask;

    private void CancelCatalogLoad()
    {
        catalogGeneration++;
        catalogCancellation?.Cancel();
        catalogLoading = false;
    }

    private async Task OpenGalleryAsync()
    {
        CancelCatalogLoad();
        var generation = catalogGeneration;
        var snapshot = await Gallery.LoadInitialPageAsync(PageSize, TrashActive);
        if (!IsCatalogGenerationCurrent(generation)) return;
        catalogPartial = !snapshot.IsComplete || !snapshot.IsCurrent;
        catalogLoadError = "";
        images = snapshot.Records;
        await ApplyLoadedRecordsAsync();
        if (catalogPartial) StartCatalogLoad();
    }

    private bool IsCatalogGenerationCurrent(long generation) =>
        !disposed && generation == catalogGeneration && Gallery.IsUnlocked;

    private void StartCatalogLoad()
    {
        var previous = catalogLoad;
        CancelCatalogLoad();
        var cancellation = new CancellationTokenSource();
        catalogCancellation = cancellation;
        var generation = catalogGeneration;
        catalogLoading = true;
        catalogLoadError = "";
        catalogLoad = FinishCatalogLoadAsync(previous, generation, cancellation);
    }

    private async Task FinishCatalogLoadAsync(Task previous, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            await previous;
            // Give the initial render and thumbnail requests priority over the full scan.
            await Task.Delay(250, cancellation.Token);
            var snapshot = await Gallery.LoadCatalogAsync(cancellation.Token);
            if (!IsCatalogGenerationCurrent(generation) || cancellation.IsCancellationRequested) return;
            if (!snapshot.IsCurrent) { StartCatalogLoad(); return; }
            images = snapshot.Records;
            imagesById = images.ToDictionary(image => image.Id);
            browsingSource = null;
            catalogPartial = false;
            catalogLoading = false;
            if (selected is not null) selected = imagesById.GetValueOrDefault(selected.Id);
            UpdateDuplicateGroups();
            page = Math.Min(page, PageCount - 1);
            await LoadThumbnailsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!disposed && generation == catalogGeneration && Gallery.IsUnlocked)
            {
                Logger.LogError(exception, "Background catalog load failed");
                catalogLoadError = "The rest of the library could not be loaded. The initial page is still available.";
                catalogLoading = false;
            }
        }
        finally
        {
            if (ReferenceEquals(catalogCancellation, cancellation))
            {
                catalogCancellation = null;
                catalogLoad = Task.CompletedTask;
            }
            cancellation.Dispose();
            if (!disposed && generation == catalogGeneration) StateHasChanged();
        }
    }

    private async Task ApplyLoadedRecordsAsync()
    {
        browsingSource = null;
        mediaDirectory = (await Gallery.LoadMediaStorageOptionsAsync()).MediaDirectory;
        catalogDirectory = "";
        ApplyIngestionProfile(await Gallery.LoadIngestionOptionsAsync());
        imagesById = images.ToDictionary(image => image.Id);
        UpdateDuplicateGroups();
        if (workflow == "duplicates") await LoadDuplicateThumbnailsAsync();
        settings = await Gallery.SettingsAsync();
        storageOverview = await Gallery.CachedStorageOverviewAsync();
        selected = selected is null ? null : images.SingleOrDefault(image => image.Id == selected.Id);
        if (selected is null) { preview = ""; _ = ReleaseVideoAsync(); }
        removalSelection.IntersectWith(images.Where(image => image.IsMarkedForDeletion).Select(image => image.Id));
        librarySelection.IntersectWith(images.Select(image => image.Id));
        page = Math.Min(page, PageCount - 1);
        await LoadThumbnailsAsync();
    }

    private Task RetryCatalogLoadAsync()
    {
        if (catalogPartial && !catalogLoading) StartCatalogLoad();
        return Task.CompletedTask;
    }
}
