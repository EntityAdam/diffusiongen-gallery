using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Features.Media;

internal sealed class ViewerMediaCache(IGalleryMedia media, IJSRuntime js) : IAsyncDisposable
{
    private const long CacheBudget = 64 * 1024 * 1024;
    private readonly Dictionary<string, (string Url, long Bytes)> fit = [];
    private readonly Dictionary<string, (string Url, long Bytes)> original = [];
    private readonly SemaphoreSlim gate = new(1);
    private volatile bool closed;

    public async Task<string> LoadAsync(ImageRecord image, bool full, string initialPreview = "")
    {
        await gate.WaitAsync();
        try
        {
            if (closed) return "";
            return await LoadCoreAsync(image, full, prefetch: false, initialPreview);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> PrefetchAsync(ImageRecord image)
    {
        await gate.WaitAsync();
        try
        {
            if (closed || fit.Values.Sum(entry => entry.Bytes) >= CacheBudget) return false;
            // Videos can be large and share one cache entry across both size modes.
            if (!image.IsVideo) await LoadCoreAsync(image, full: false, prefetch: true, initialPreview: "");
            return !closed;
        }
        finally { gate.Release(); }
    }

    private async Task<string> LoadCoreAsync(ImageRecord image, bool full, bool prefetch, string initialPreview)
    {
        full = full && !image.IsVideo;
        var cache = full ? original : fit;
        if (cache.TryGetValue(image.Id, out var cached)) return cached.Url;

        var (content, contentType) = image.IsVideo ? await media.OpenMediaAsync(image.Id) : (null, "");
        using var stream = content;
        var data = initialPreview;
        if (stream is null && data.Length == 0)
            data = await media.ViewerImageAsync(image.Id, full ? ImageResolution.Original : ImageResolution.Fit);
        var bytes = stream?.Length ?? data.Length * 3L / 4;
        if (closed || (prefetch && fit.Values.Sum(entry => entry.Bytes) + bytes > CacheBudget)) return "";

        while (cache.Count > 0 && (cache.Values.Sum(entry => entry.Bytes) + bytes > CacheBudget
            || full && cache.Count >= 2))
        {
            var victim = cache.First();
            await ReleaseAsync([victim.Value.Url]);
            cache.Remove(victim.Key);
        }

        string url;
        if (stream is not null)
        {
            using var reference = new DotNetStreamReference(stream, leaveOpen: true);
            url = await js.InvokeAsync<string>("galleryMedia.createStreamUrl", reference, contentType);
        }
        else
        {
            url = await js.InvokeAsync<string>("galleryViewer.createImageUrl", data);
        }

        // Register URLs created during shutdown so the waiting disposer releases them too.
        cache[image.Id] = (url, bytes);
        return closed ? "" : url;
    }

    public async ValueTask DisposeAsync()
    {
        closed = true;
        await gate.WaitAsync();
        try
        {
            if (fit.Count == 0 && original.Count == 0) return;
            await ReleaseAsync(fit.Values.Concat(original.Values).Select(entry => entry.Url).ToArray());
            fit.Clear();
            original.Clear();
        }
        finally { gate.Release(); }
    }

    private ValueTask ReleaseAsync(string[] urls) => js.InvokeVoidAsync("galleryViewer.releaseImageUrls", (object)urls);
}
