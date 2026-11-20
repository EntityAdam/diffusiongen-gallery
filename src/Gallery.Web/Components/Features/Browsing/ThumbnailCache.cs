using System.Diagnostics;
using Gallery.Core.Services;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Features.Browsing;

// All callers and continuations run on the workspace renderer dispatcher.
internal sealed class ThumbnailCache(IGalleryVault vault, IGalleryMedia media, ILogger logger, Action changedCallback, IJSRuntime js)
{
    internal const long ViewportByteBudget = 96 * 1024 * 1024;
    private static readonly TimeSpan ScrollDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumScrollDelay = TimeSpan.FromMilliseconds(250);

    private sealed class Entry
    {
        internal string? Data;
        internal bool Failed;
        internal bool BudgetExceeded;
        internal bool NeedsRead => Data is null && !Failed && !BudgetExceeded;
        internal long Bytes;
        internal bool OwnsUrl;
    }

    private readonly Dictionary<string, Entry> entries = [];
    private readonly List<string> retiredUrls = [];
    private string[] order = [];
    private HashSet<string> visible = [];
    private Task? worker;
    private Task? release;
    private bool paused;
    private bool reading;
    private bool virtualized;
    private long retainedBytes;
    private long firstDeferred;
    private long due;
    private CancellationTokenSource? delayCancellation;

    internal string? Get(string id) => entries.GetValueOrDefault(id)?.Data;
    internal bool Failed(string id) => entries.GetValueOrDefault(id) is { Failed: true } or { BudgetExceeded: true };
    internal int InFlight => reading ? 1 : 0;
    internal int PendingCount => entries.Values.Count(entry => !ReferenceEquals(entry, activeEntry) && entry.NeedsRead);
    internal long RetainedBytes => retainedBytes;
    private Entry? activeEntry;

    internal void SetWindow(string[] ids, int visibleCount, bool immediate, bool virtualized)
    {
        var retained = ids.ToHashSet();
        var nextVisible = ids.Take(visibleCount).ToHashSet();
        var visibilityChanged = !visible.SetEquals(nextVisible);
        foreach (var id in entries.Keys.Where(id => !retained.Contains(id)).ToArray()) Remove(id);
        foreach (var id in ids)
        {
            if (!entries.TryGetValue(id, out var entry)) entries.Add(id, new());
            else if (visibilityChanged) entry.BudgetExceeded = false;
        }

        var changed = !order.AsSpan().SequenceEqual(ids) || visibilityChanged;
        this.virtualized = virtualized;
        order = ids;
        visible = nextVisible;
        if (immediate) firstDeferred = due = 0;
        else if (changed)
        {
            var now = Stopwatch.GetTimestamp();
            if (firstDeferred == 0) firstDeferred = now;
            due = Math.Min(now + Ticks(ScrollDelay), firstDeferred + Ticks(MaximumScrollDelay));
        }

        delayCancellation?.Cancel();
        StartReleasingUrls();
        GalleryBrowsingMetrics.Worker(virtualized, this);
        Start();
    }

    internal void Invalidate(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (!entries.ContainsKey(id)) continue;
            Remove(id);
            entries.Add(id, new());
        }
        StartReleasingUrls();
        Start();
    }

    internal void Clear()
    {
        order = [];
        visible.Clear();
        foreach (var entry in entries.Values) DropData(entry);
        entries.Clear();
        retainedBytes = firstDeferred = due = 0;
        delayCancellation?.Cancel();
        StartReleasingUrls();
        GalleryBrowsingMetrics.Worker(virtualized, this);
    }

    private void Remove(string id)
    {
        if (entries.Remove(id, out var entry)) DropData(entry);
    }

    private void DropData(Entry entry)
    {
        if (entry.OwnsUrl && entry.Data is { } url) retiredUrls.Add(url);
        retainedBytes -= entry.Bytes;
        entry.Data = null;
        entry.Bytes = 0;
        entry.OwnsUrl = false;
    }

    private void StartReleasingUrls()
    {
        if (retiredUrls.Count > 0 && (release is null || release.IsCompleted)) release = ReleaseUrlsAsync();
    }

    private async Task ReleaseUrlsAsync()
    {
        await Task.Yield();
        while (retiredUrls.Count > 0)
        {
            var urls = retiredUrls.ToArray();
            retiredUrls.Clear();
            try { await js.InvokeVoidAsync("galleryViewer.releaseImageUrls", (object)urls); }
            catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
            {
                retiredUrls.Clear();
                return;
            }
            catch (JSException exception) { logger.LogWarning(exception, "Thumbnail URLs could not be released"); }
        }
    }

    private static long Ticks(TimeSpan interval) => (long)(interval.TotalSeconds * Stopwatch.Frequency);

    internal async Task PauseAsync()
    {
        paused = true;
        delayCancellation?.Cancel();
        if (worker is { } running) await running;
    }

    internal void Resume()
    {
        paused = false;
        Start();
    }

    private void Start()
    {
        if (!paused && vault.IsUnlocked && (worker is null || worker.IsCompleted) &&
            entries.Values.Any(entry => entry.NeedsRead))
            worker = RunAsync();
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        var changed = false;
        var lastRender = Stopwatch.GetTimestamp();
        try
        {
            while (!paused && vault.IsUnlocked)
            {
                var id = Array.Find(order, id => entries[id].NeedsRead);
                if (id is null) break;
                var remaining = due - Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    if (changed) { changedCallback(); changed = false; }
                    using var delay = new CancellationTokenSource();
                    delayCancellation = delay;
                    try { await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), delay.Token); }
                    catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
                    finally { delayCancellation = null; }
                    continue;
                }

                firstDeferred = due = 0;
                var entry = entries[id];
                changed |= await LoadAsync(id, entry);
                if (changed && Stopwatch.GetElapsedTime(lastRender).TotalMilliseconds >= 80)
                {
                    changedCallback();
                    changed = false;
                    lastRender = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            if (changed) changedCallback();
        }
    }

    private async Task<bool> LoadAsync(string id, Entry entry)
    {
        activeEntry = entry;
        reading = true;
        var infinite = virtualized;
        GalleryBrowsingMetrics.Worker(infinite, this);
        try
        {
            if (infinite) return await LoadObjectUrlAsync(id, entry);
            var data = await media.ThumbnailAsync(id);
            if (!IsCurrent(id, entry)) return Discard(infinite);
            Remember(entry, data, data.Length * (long)sizeof(char), ownsUrl: false);
            return true;
        }
        catch (Exception exception)
        {
            if (!IsCurrent(id, entry)) return false;
            logger.LogWarning(exception, "Thumbnail could not be loaded");
            entry.Failed = true;
            return true;
        }
        finally
        {
            reading = false;
            activeEntry = null;
            GalleryBrowsingMetrics.Worker(virtualized, this);
        }
    }

    private async Task<bool> LoadObjectUrlAsync(string id, Entry entry)
    {
        using var stream = await media.OpenThumbnailAsync(id);
        if (!IsCurrent(id, entry)) return Discard(infinite: true);

        var bytes = stream.Length;
        if (retainedBytes + bytes > ViewportByteBudget)
        {
            if (visible.Contains(id))
            {
                foreach (var outside in order.Where(other => !visible.Contains(other) && entries[other].Data is not null))
                {
                    var buffer = entries[outside];
                    DropData(buffer);
                    buffer.BudgetExceeded = true;
                    if (retainedBytes + bytes <= ViewportByteBudget) break;
                }
                StartReleasingUrls();
            }
            if (retainedBytes + bytes > ViewportByteBudget)
            {
                entry.BudgetExceeded = true;
                return true;
            }
        }

        using var reference = new DotNetStreamReference(stream, leaveOpen: true);
        var url = await js.InvokeAsync<string>("galleryMedia.createStreamUrl", reference, "image/png");
        if (!IsCurrent(id, entry))
        {
            retiredUrls.Add(url);
            StartReleasingUrls();
            return Discard(infinite: true);
        }

        Remember(entry, url, bytes, ownsUrl: true);
        return true;
    }

    private void Remember(Entry entry, string value, long bytes, bool ownsUrl)
    {
        entry.Data = value;
        entry.Bytes = bytes;
        entry.OwnsUrl = ownsUrl;
        retainedBytes += bytes;
    }

    private static bool Discard(bool infinite)
    {
        GalleryBrowsingMetrics.Discarded(infinite);
        return false;
    }

    private bool IsCurrent(string id, Entry entry) => vault.IsUnlocked && ReferenceEquals(entries.GetValueOrDefault(id), entry);
}
