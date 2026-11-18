using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Browsing;

internal sealed class ThumbnailCache(IGalleryVault vault, IGalleryMedia media, ILogger logger, Action changedCallback)
{
    public string? Get(string id) => thumbnails.GetValueOrDefault(id);
    public bool Failed(string id) => thumbnailFailures.Contains(id);

    public void Clear()
    {
        StopThumbnailLoad();
        thumbnailTask = null;
        thumbnails.Clear();
        staleThumbnails.Clear();
        thumbnailFailures.Clear();
    }

    private readonly Dictionary<string, string> thumbnails = [];
    private readonly HashSet<string> staleThumbnails = [];
    private readonly HashSet<string> thumbnailFailures = [];
    private CancellationTokenSource? thumbnailLoad;
    private Task? thumbnailTask;

    public Task ReloadAsync(IEnumerable<ImageRecord> images)
    {
        var pageIds = images.Select(image => image.Id).ToHashSet();
        foreach (var id in thumbnails.Keys.Where(id => !pageIds.Contains(id)).ToList()) thumbnails.Remove(id);
        staleThumbnails.Clear();
        staleThumbnails.UnionWith(pageIds);
        thumbnailFailures.Clear();
        StartThumbnailLoad(pageIds);
        return Task.CompletedTask;
    }

    public Task LoadMissingAsync(IEnumerable<ImageRecord> images)
    {
        StartThumbnailLoad(images.Select(image => image.Id));
        return Task.CompletedTask;
    }

    private void StartThumbnailLoad(IEnumerable<string> pageIds)
    {
        StopThumbnailLoad();
        var ids = PendingThumbnailIds(pageIds);
        if (ids.Count == 0) return;
        thumbnailLoad = new CancellationTokenSource();
        thumbnailTask = LoadThumbnailsInBackgroundAsync(ids, thumbnailLoad.Token);
    }

    private List<string> PendingThumbnailIds(IEnumerable<string> pageIds) => pageIds
        .Where(id => (!thumbnails.ContainsKey(id) || staleThumbnails.Contains(id)) && !thumbnailFailures.Contains(id))
        .ToList();

    private void StopThumbnailLoad()
    {
        thumbnailLoad?.Cancel();
        thumbnailLoad?.Dispose();
        thumbnailLoad = null;
    }

    public async Task PauseAsync()
    {
        var running = thumbnailTask;
        StopThumbnailLoad();
        thumbnailTask = null;
        if (running is not null) await running;
    }

    public void Resume(IEnumerable<ImageRecord> images)
    {
        if (!vault.IsUnlocked || thumbnailTask is { IsCompleted: false }) return;
        StartThumbnailLoad(images.Select(image => image.Id));
    }

    private async Task LoadThumbnailsInBackgroundAsync(List<string> ids, CancellationToken token)
    {
        await Task.Yield();
        var lastRender = System.Diagnostics.Stopwatch.GetTimestamp();
        var changed = false;
        foreach (var id in ids)
        {
            if (token.IsCancellationRequested || !vault.IsUnlocked) break;
            try
            {
                var thumbnail = await media.ThumbnailAsync(id);
                if (token.IsCancellationRequested) break;
                thumbnails[id] = thumbnail;
                staleThumbnails.Remove(id);
                changed = true;
            }
            catch (Exception exception)
            {
                if (token.IsCancellationRequested || !vault.IsUnlocked) break;
                logger.LogWarning(exception, "Thumbnail could not be loaded");
                thumbnailFailures.Add(id);
                staleThumbnails.Remove(id);
            }
            if (changed && System.Diagnostics.Stopwatch.GetElapsedTime(lastRender).TotalMilliseconds >= 100)
            {
                changedCallback();
                lastRender = System.Diagnostics.Stopwatch.GetTimestamp();
                changed = false;
            }
        }
        if (changed) changedCallback();
    }
}
