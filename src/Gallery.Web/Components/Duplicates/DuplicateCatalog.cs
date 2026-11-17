using System.Collections.Immutable;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Duplicates;

public sealed class DuplicateCatalog : IDisposable
{
    private bool disposed;
    private readonly Dictionary<string, string> thumbnails = [];
    public DuplicateScan? Scan { get; private set; }
    public IReadOnlyList<DuplicateGroup> Groups { get; private set; } = [];
    public IReadOnlyDictionary<string, string> Thumbnails => thumbnails;
    public int DisplayLimit { get; private set; } = 20;

    internal async Task LoadScanAsync(GalleryService gallery)
    {
        if (disposed || Scan is not null) return;
        var scan = await gallery.CachedDuplicateScanAsync();
        if (!disposed) Scan ??= scan;
    }

    public void ReplaceScan(DuplicateScan scan)
    {
        if (disposed) return;
        Scan = scan;
        DisplayLimit = 20;
        thumbnails.Clear();
    }

    public void UpdateGroups(IReadOnlyDictionary<string, ImageRecord> images)
    {
        if (disposed) return;
        Groups = Scan is null ? [] : Scan.Groups
            .Select(group => group with { ImageIds = group.ImageIds.Where(images.ContainsKey).ToImmutableArray() })
            .Where(group => group.ImageIds.Length > 1).ToList();
    }

    public async Task ShowMoreAsync(GalleryService gallery)
    {
        if (disposed) return;
        DisplayLimit += 20;
        await LoadThumbnailsAsync(gallery);
    }

    public async Task LoadThumbnailsAsync(GalleryService gallery)
    {
        foreach (var id in Groups.Take(DisplayLimit).SelectMany(group => group.ImageIds))
        {
            if (disposed || !gallery.IsUnlocked) return;
            if (thumbnails.ContainsKey(id)) continue;
            var thumbnail = await gallery.ThumbnailAsync(id);
            if (disposed || !gallery.IsUnlocked) return;
            thumbnails[id] = thumbnail;
        }
    }

    public void Dispose()
    {
        disposed = true;
        Groups = [];
        Scan = null;
        thumbnails.Clear();
    }
}
