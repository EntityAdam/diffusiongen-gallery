using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    private Task LoadThumbnailsAsync()
    {
        var pageIds = PageImages.Select(image => image.Id).ToHashSet();
        foreach (var id in thumbnails.Keys.Where(id => !pageIds.Contains(id)).ToList()) thumbnails.Remove(id);
        staleThumbnails.Clear();
        staleThumbnails.UnionWith(pageIds);
        thumbnailFailures.Clear();
        StartThumbnailLoad();
        return Task.CompletedTask;
    }

    private Task LoadMissingThumbnailsAsync()
    {
        StartThumbnailLoad();
        return Task.CompletedTask;
    }

    private void StartThumbnailLoad()
    {
        StopThumbnailLoad();
        var ids = PendingThumbnailIds();
        if (ids.Count == 0) return;
        thumbnailLoad = new CancellationTokenSource();
        thumbnailTask = LoadThumbnailsInBackgroundAsync(ids, thumbnailLoad.Token);
    }

    private List<string> PendingThumbnailIds() => PageImages.Select(image => image.Id)
        .Where(id => (!thumbnails.ContainsKey(id) || staleThumbnails.Contains(id)) && !thumbnailFailures.Contains(id))
        .ToList();

    private void StopThumbnailLoad()
    {
        thumbnailLoad?.Cancel();
        thumbnailLoad?.Dispose();
        thumbnailLoad = null;
    }

    private async Task PauseThumbnailLoadAsync()
    {
        var running = thumbnailTask;
        StopThumbnailLoad();
        thumbnailTask = null;
        if (running is not null) await running;
    }

    private void ResumeThumbnailLoad()
    {
        if (disposed || !Gallery.IsUnlocked || thumbnailTask is { IsCompleted: false }) return;
        StartThumbnailLoad();
    }

    private async Task LoadThumbnailsInBackgroundAsync(List<string> ids, CancellationToken token)
    {
        await Task.Yield();
        var lastRender = System.Diagnostics.Stopwatch.GetTimestamp();
        var changed = false;
        foreach (var id in ids)
        {
            if (token.IsCancellationRequested || disposed || !Gallery.IsUnlocked) break;
            try
            {
                var thumbnail = await Gallery.ThumbnailAsync(id);
                if (token.IsCancellationRequested) break;
                thumbnails[id] = thumbnail;
                staleThumbnails.Remove(id);
                changed = true;
            }
            catch (Exception exception)
            {
                if (token.IsCancellationRequested || !Gallery.IsUnlocked) break;
                Logger.LogWarning(exception, "Thumbnail could not be loaded");
                thumbnailFailures.Add(id);
                staleThumbnails.Remove(id);
            }
            if (changed && System.Diagnostics.Stopwatch.GetElapsedTime(lastRender).TotalMilliseconds >= 100)
            {
                StateHasChanged();
                lastRender = System.Diagnostics.Stopwatch.GetTimestamp();
                changed = false;
            }
        }
        if (changed && !disposed) StateHasChanged();
    }
    private Task RateImageAsync(ImageRecord image, int value) => RunAsync("Saving rating...", async () =>
    {
        ViewerImageChanged(await Gallery.UpdateAsync(image.Id, current =>
            Gallery.SetCuration(current, current.Rating == value ? 0 : value, null)));
        await LoadThumbnailsAsync();
    });

    private Task FavoriteAsync(ImageRecord image) => RunAsync("Saving favorite...", async () =>
    {
        ViewerImageChanged(await Gallery.UpdateAsync(image.Id, current => current with { IsFavorite = !current.IsFavorite }));
        await LoadThumbnailsAsync();
    });

    private Task MarkAsync(ImageRecord image) => RunAsync("Saving deletion mark...", async () =>
    {
        ViewerImageChanged(await Gallery.UpdateAsync(image.Id, current => Gallery.SetDeletionMark(current, !current.IsMarkedForDeletion)));
        await LoadThumbnailsAsync();
    });

    private Task PrepareBulkRemovalAsync() => PrepareRemovalAsync(removalSelection.ToArray());
    private Task PrepareRemovalAsync(IEnumerable<string> ids) => RunAsync("Checking originals...", async () =>
    {
        EnsureNoLibraryJob();
        removalPlan = await Gallery.PrepareRemovalAsync(ids);
        confirmRemoval = false;
        existingOriginalAction = ExistingOriginalAction.Retain;
        missingOriginalAction = MissingOriginalAction.RestorePng;
        removalResults.Clear();
    });

    private void CancelRemoval() { removalPlan.Clear(); confirmRemoval = false; }

    private Task ExecuteRemovalAsync() => RunAsync("Removing marked media...", async () =>
    {
        EnsureNoLibraryJob();
        if (!confirmRemoval) throw new InvalidOperationException("Confirm removal first.");
        removalResults = [];
        try
        {
            await Gallery.RemoveAsync(removalPlan, existingOriginalAction, missingOriginalAction, restoreFolder, removalResults.Add);
        }
        finally
        {
            foreach (var result in removalResults.Where(result => result.Error is not null))
                Logger.LogWarning("Media removal issue for {ImageId}: {Error}", result.Id, result.Error);
            CancelRemoval();
            CloseDetail();
            await ReloadAsync();
        }
        message = $"Removed {removalResults.Count(result => result.WasRemoved)} media item(s). {removalResults.Count(result => result.Error is not null)} issue(s); review the removal results.";
    });

    private Task OpenAsync(ImageRecord image) => RunAsync("Decrypting preview...", async () =>
    {
        await ReleaseVideoAsync();
        var data = await Gallery.PreviewAsync(image.Id);
        if (image.IsVideo)
        {
            progress = "Decrypting video...";
            var (content, type) = await Gallery.OpenMediaAsync(image.Id);
            using var reference = new DotNetStreamReference(content);
            videoUrl = await JS.InvokeAsync<string>("galleryMedia.createStreamUrl", reference, type);
        }
        detailViewerImages = null;
        selected = image;
        preview = data;
        editDescription = image.Description;
        editUserDescription = image.UserDescription;
        destination = Path.GetDirectoryName(image.StoredPath)!;
        rename = image.Name;
    });

    private void CloseDetail()
    {
        pendingDetailAction = null;
        detailViewerImages = null;
        selected = null;
        preview = editDescription = editUserDescription = destination = rename = "";
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
        if (catalogPartial) return;
        CloseDetail();
        if (!ShowsGallery) await SwitchWorkflowAsync("gallery");
        filter = filter with { Prompt = prompt };
        await FilterChangedAsync();
    }

    private async Task DownloadAsync(ExportFile file)
    {
        var contentBytes = GalleryService.CopyExportContent(file);
        try { await DownloadAsync(new MemoryStream(contentBytes, writable: false), file.FileName, file.ContentType); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(contentBytes); }
    }

    private async Task DownloadAsync(Stream content, string fileName, string contentType)
    {
        using var reference = new DotNetStreamReference(content);
        await JS.InvokeVoidAsync("galleryMedia.download", reference, fileName, contentType);
    }

    private Task ExportAsync(ImageRecord image) => RunAsync(image.IsVideo ? "Decrypting video copy..." : "Preparing image copy...", async () =>
    {
        var file = await Gallery.ExportAsync(image.Id);
        try { await DownloadAsync(file); }
        finally { GalleryService.ClearExportContent(file); }
        message = $"Saved a decrypted copy as {file.FileName}. It is no longer protected by the gallery.";
    });

    private Task ExportSelectedAsync() => RunAsync($"Packing {librarySelection.Count} decrypted copies...", async () =>
    {
        var file = await Gallery.ExportZipAsync(librarySelection.ToList());
        try { await DownloadAsync(file); }
        finally { GalleryService.ClearExportContent(file); }
        message = $"Saved {librarySelection.Count} decrypted copies as {file.FileName}. They are no longer protected by the gallery.";
    });

    private void OpenViewer(string? initialId = null)
    {
        reviewMode = false;
        viewerImages = (detailViewerImages ?? PageImages).Select(image => image with { }).ToList();
        if (initialId is not null && !viewerImages.Any(image => image.Id == initialId) && selected is not null)
            viewerImages.Add(selected with { });
        viewerInitialId = initialId ?? viewerImages.FirstOrDefault()?.Id ?? "";
        viewerInitialPreview = selected?.Id == viewerInitialId && !selected.IsVideo ? preview : "";
        CloseDetail();
    }

    private void StartReview()
    {
        var pool = ReviewPool.ToArray();
        if (reviewRandomOrder) Random.Shared.Shuffle(pool);
        viewerImages = pool.Take(ReviewGoal).Select(image => image with { }).ToList();
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

    private Task SaveNotesAsync() => RunAsync("Saving notes...", SaveDetailNotesCoreAsync);

    private Task ExportWorkflowAsync(ImageRecord image) => RunAsync("Exporting workflow...", async () =>
    {
        var file = await Gallery.WorkflowAsync(image.Id)
            ?? throw new InvalidOperationException("No ComfyUI workflow was found in this file.");
        try { await DownloadAsync(file); }
        finally { GalleryService.ClearExportContent(file); }
        message = $"Saved the ComfyUI workflow as {file.FileName}.";
    });

    private Task AnalyzeAsync() => RunAsync("Analyzing with your local vision model...", async () =>
    {
        EnsureNoLibraryJob();
        if (selected is null || workbench is null) return;
        await workbench.AnalyzeDetailAsync(selected);
        if (Gallery.IsUnlocked) message = "Review available suggestions in the AI toolbox. Unfinished analysis can be resumed there.";
    });

    private Task MoveAsync() => RunAsync("Moving encrypted media...", async () =>
    {
        EnsureNoLibraryJob();
        if (selected is null) return;
        try { await Gallery.MoveAsync(selected.Id, destination, rename); }
        finally { await ReloadAsync(); }
        message = "Encrypted media moved and renamed.";
    });

    private readonly Dictionary<string, string> thumbnails = [];
    private readonly HashSet<string> staleThumbnails = [];
    private readonly HashSet<string> thumbnailFailures = [];
    private CancellationTokenSource? thumbnailLoad;
    private Task? thumbnailTask;

    private readonly HashSet<string> removalSelection = [];
    private List<RemovalItem> removalPlan = [];
    private List<RemovalResult> removalResults = [];
    private ExistingOriginalAction existingOriginalAction = ExistingOriginalAction.Retain;
    private MissingOriginalAction missingOriginalAction = MissingOriginalAction.RestorePng;
    private string restoreFolder = "";
    private bool confirmRemoval;
}
