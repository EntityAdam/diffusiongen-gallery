using System.Collections.Immutable;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components;

public sealed class DuplicateCatalog : IDisposable
{
    private bool disposed;
    private readonly Dictionary<string, string> thumbnails = [];
    public DuplicateScan? Scan { get; private set; }
    public IReadOnlyList<DuplicateGroup> Groups { get; private set; } = [];
    public IReadOnlyDictionary<string, string> Thumbnails => thumbnails;
    public int DisplayLimit { get; private set; } = 20;

    internal async Task LoadScanAsync(IGalleryDuplicates duplicates)
    {
        if (disposed || Scan is not null) return;
        var scan = await duplicates.CachedDuplicateScanAsync();
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

    public async Task ShowMoreAsync(IGalleryVault vault, IGalleryMedia media)
    {
        if (disposed) return;
        DisplayLimit += 20;
        await LoadThumbnailsAsync(vault, media);
    }

    public async Task LoadThumbnailsAsync(IGalleryVault vault, IGalleryMedia media)
    {
        foreach (var id in Groups.Take(DisplayLimit).SelectMany(group => group.ImageIds))
        {
            if (disposed || !vault.IsUnlocked) return;
            if (thumbnails.ContainsKey(id)) continue;
            var thumbnail = await media.ThumbnailAsync(id);
            if (disposed || !vault.IsUnlocked) return;
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
