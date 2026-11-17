using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Media;

public sealed class MediaPresentationState(GalleryService gallery, IJSRuntime js, WorkspaceCommands commands) : IDisposable
{
    public ImageRecord? Selected { get; private set; }
    public string Preview { get; private set; } = "";
    public string VideoUrl { get; private set; } = "";
    public IReadOnlyList<ImageRecord>? DetailViewerImages { get; private set; }
    public IReadOnlyList<ImageRecord> ViewerImages { get; private set; } = new List<ImageRecord>();
    public string ViewerInitialId { get; private set; } = "";
    public string ViewerInitialPreview { get; private set; } = "";
    public bool ReviewMode { get; private set; }
    public bool CanPresent => !disposed && gallery.IsUnlocked;
    public bool IsOpen => Selected is not null || ViewerImages.Count > 0;
    public bool DetailsOpen => Selected is not null;
    private bool disposed;
    private long generation;

    public Task OpenAsync(ImageRecord image, IReadOnlyList<ImageRecord>? viewerImages = null)
    {
        if (disposed) return Task.CompletedTask;
        return commands.RunAsync("Decrypting preview...", async () =>
        {
            if (disposed || !gallery.IsUnlocked) return;
            var revision = ++generation;
            await ReleaseVideoAsync();
            var preview = await gallery.PreviewAsync(image.Id);
            if (!IsCurrent(revision)) return;
            var url = "";
            if (image.IsVideo)
            {
                commands.Activity.Progress = "Decrypting video...";
                var (content, type) = await gallery.OpenMediaAsync(image.Id);
                using var reference = new DotNetStreamReference(content);
                if (!IsCurrent(revision)) return;
                url = await js.InvokeAsync<string>("galleryMedia.createStreamUrl", reference, type);
            }
            if (!IsCurrent(revision)) { await ReleaseUrlAsync(url); return; }
            VideoUrl = url;
            DetailViewerImages = viewerImages;
            Selected = image;
            Preview = preview;
        });
    }

    private bool IsCurrent(long revision) => !disposed && revision == generation && gallery.IsUnlocked;

    public void Update(ImageRecord updated)
    {
        if (disposed) return;
        if (Selected?.Id == updated.Id) Selected = updated;
        if (DetailViewerImages is not null)
            DetailViewerImages = DetailViewerImages.Select(image => image.Id == updated.Id ? updated : image).ToArray();
    }

    public async Task ReconcileAsync(IReadOnlyDictionary<string, ImageRecord> records)
    {
        if (disposed) return;
        Selected = Selected is null ? null : records.GetValueOrDefault(Selected.Id);
        if (Selected is null) { Preview = ""; await ReleaseVideoAsync(); }
    }

    public void CloseDetail()
    {
        generation++;
        DetailViewerImages = null;
        Selected = null;
        Preview = "";
        _ = ReleaseVideoAsync();
    }

    private async Task ReleaseVideoAsync()
    {
        var url = VideoUrl;
        VideoUrl = "";
        await ReleaseUrlAsync(url);
    }
    private async Task ReleaseUrlAsync(string url)
    {
        if (url.Length == 0) return;
        try { await js.InvokeVoidAsync("galleryViewer.releaseImageUrls", (object)new[] { url }); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
    }

    public void OpenViewer(IEnumerable<ImageRecord> pageImages, string? initialId = null)
    {
        if (disposed) return;
        ReviewMode = false;
        var images = (DetailViewerImages ?? pageImages).Select(image => image with { }).ToList();
        if (initialId is not null && !images.Any(image => image.Id == initialId) && Selected is not null)
            images.Add(Selected with { });
        ViewerImages = images;
        ViewerInitialId = initialId ?? images.FirstOrDefault()?.Id ?? "";
        ViewerInitialPreview = Selected?.Id == ViewerInitialId && !Selected.IsVideo ? Preview : "";
        CloseDetail();
    }

    public void StartReview(IEnumerable<ImageRecord> reviewPool, int goal, bool randomOrder)
    {
        if (disposed) return;
        var pool = reviewPool.ToArray();
        if (randomOrder) Random.Shared.Shuffle(pool);
        ViewerImages = pool.Take(goal).Select(image => image with { }).ToList();
        ViewerInitialId = ViewerImages.FirstOrDefault()?.Id ?? "";
        ViewerInitialPreview = "";
        ReviewMode = true;
        CloseDetail();
    }

    public void CloseViewer()
    {
        ViewerImages = new List<ImageRecord>();
        ViewerInitialId = ViewerInitialPreview = "";
        ReviewMode = false;
    }

    public void Dispose()
    {
        disposed = true;
        CloseDetail();
        CloseViewer();
    }
}
