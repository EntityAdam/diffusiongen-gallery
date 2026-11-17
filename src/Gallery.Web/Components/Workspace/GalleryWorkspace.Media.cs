using Gallery.Core.Models;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Workspace;

public partial class GalleryWorkspace
{
    private Task RateImageAsync(ImageRecord image, int value) => RunAsync("Saving rating...", async () =>
    {
        ViewerImageChanged(await Gallery.UpdateAsync(image.Id, current =>
            Gallery.SetCuration(current, current.Rating == value ? 0 : value, null)));
        await Browser.LoadThumbnailsAsync();
    });

    private Task FavoriteAsync(ImageRecord image) => RunAsync("Saving favorite...", async () =>
    {
        ViewerImageChanged(await Gallery.UpdateAsync(image.Id, current => current with { IsFavorite = !current.IsFavorite }));
        await Browser.LoadThumbnailsAsync();
    });

    private Task MarkAsync(ImageRecord image) => RunAsync("Saving deletion mark...", async () =>
    {
        ViewerImageChanged(await Gallery.UpdateAsync(image.Id, current => Gallery.SetDeletionMark(current, !current.IsMarkedForDeletion)));
        await Browser.LoadThumbnailsAsync();
    });

    private Task PrepareBulkRemovalAsync() => PrepareRemovalAsync(Browser.RemovalSelection.ToArray());
    private Task PrepareRemovalAsync(IEnumerable<string> ids) => RunAsync("Checking originals...", async () =>
    {
        EnsureNoLibraryJob();
        removalPlan = await Gallery.PrepareRemovalAsync(ids);
        removalResults.Clear();
    });

    private void CancelRemoval() => removalPlan.Clear();

    private Task ExecuteRemovalAsync((ExistingOriginalAction Existing, MissingOriginalAction Missing, string Folder) choices) => RunAsync("Removing marked media...", async () =>
    {
        EnsureNoLibraryJob();
        removalResults = [];
        try
        {
            await Gallery.RemoveAsync(removalPlan, choices.Existing, choices.Missing, choices.Folder, removalResults.Add);
        }
        finally
        {
            foreach (var result in removalResults.Where(result => result.Error is not null))
                Logger.LogWarning("Media removal issue for {ImageId}: {Error}", result.Id, result.Error);
            CancelRemoval();
            CloseDetail();
            await ReloadAsync();
        }
        Activity.Message = $"Removed {removalResults.Count(result => result.WasRemoved)} media item(s). {removalResults.Count(result => result.Error is not null)} issue(s); review the removal results.";
    });

    private Task OpenAsync(ImageRecord image) => RunAsync("Decrypting preview...", async () =>
    {
        await ReleaseVideoAsync();
        var data = await Gallery.PreviewAsync(image.Id);
        if (image.IsVideo)
        {
            Activity.Progress = "Decrypting video...";
            var (content, type) = await Gallery.OpenMediaAsync(image.Id);
            using var reference = new DotNetStreamReference(content);
            videoUrl = await JS.InvokeAsync<string>("galleryMedia.createStreamUrl", reference, type);
        }
        detailViewerImages = null;
        selected = image;
        preview = data;
    });

    private void CloseDetail()
    {
        detailDialog = null;
        detailViewerImages = null;
        selected = null;
        preview = "";
        _ = ReleaseVideoAsync();
    }

    private async Task ReleaseVideoAsync()
    {
        if (videoUrl.Length == 0) return;
        var url = videoUrl;
        videoUrl = "";
        try { await JS.InvokeVoidAsync("galleryViewer.releaseImageUrls", (object)new[] { url }); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
    }

    private async Task ShowPromptAsync(string prompt)
    {
        if (Catalog.IsPartial) return;
        CloseDetail();
        if (workflow != "gallery") await SwitchWorkflowAsync("gallery");
        Browser.Filter = Browser.Filter with { Prompt = prompt };
        await FilterChangedAsync();
    }

    private void OpenViewer(string? initialId = null)
    {
        reviewMode = false;
        viewerImages = (detailViewerImages ?? Browser.PageImages).Select(image => image with { }).ToList();
        if (initialId is not null && !viewerImages.Any(image => image.Id == initialId) && selected is not null)
            viewerImages.Add(selected with { });
        viewerInitialId = initialId ?? viewerImages.FirstOrDefault()?.Id ?? "";
        viewerInitialPreview = selected?.Id == viewerInitialId && !selected.IsVideo ? preview : "";
        CloseDetail();
    }

    private void StartReview()
    {
        var pool = Browser.ReviewPool.ToArray();
        if (Browser.ReviewRandomOrder) Random.Shared.Shuffle(pool);
        viewerImages = pool.Take(Browser.ReviewGoal).Select(image => image with { }).ToList();
        viewerInitialId = viewerImages.FirstOrDefault()?.Id ?? "";
        viewerInitialPreview = "";
        reviewMode = true;
        CloseDetail();
    }

    private Task CloseViewerAsync()
    {
        viewerImages.Clear();
        viewerInitialId = "";
        viewerInitialPreview = "";
        return RunAsync("Refreshing gallery...", ReloadAsync);
    }

    private Task AnalyzeAsync() => RunAsync("Analyzing with your local vision model...", async () =>
    {
        EnsureNoLibraryJob();
        if (selected is null || galleryBrowser is null) return;
        await galleryBrowser.AnalyzeDetailAsync(selected);
        if (Gallery.IsUnlocked) Activity.Message = "Review available suggestions in the AI analysis drawer. Unfinished analysis can be resumed there.";
    });

    private List<RemovalItem> removalPlan = [];
    private List<RemovalResult> removalResults = [];

}
