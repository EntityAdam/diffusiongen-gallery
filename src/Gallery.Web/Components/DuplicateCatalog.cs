using System.Collections.Immutable;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components;

public sealed class DuplicateCatalog
{
    private readonly Dictionary<string, string> thumbnails = [];
    public DuplicateScan? Scan { get; private set; }
    public IReadOnlyList<DuplicateGroup> Groups { get; private set; } = [];
    public IReadOnlyDictionary<string, string> Thumbnails => thumbnails;
    public int DisplayLimit { get; private set; } = 20;

    public async Task LoadAsync(GalleryService gallery, IReadOnlyDictionary<string, ImageRecord> images)
    {
        Scan ??= await gallery.CachedDuplicateScanAsync();
        UpdateGroups(images);
    }

    public void ReplaceScan(DuplicateScan scan)
    {
        Scan = scan;
        DisplayLimit = 20;
        thumbnails.Clear();
    }

    public void UpdateGroups(IReadOnlyDictionary<string, ImageRecord> images)
    {
        Groups = Scan is null ? [] : Scan.Groups
            .Select(group => group with { ImageIds = group.ImageIds.Where(images.ContainsKey).ToImmutableArray() })
            .Where(group => group.ImageIds.Length > 1).ToList();
    }

    public async Task ShowMoreAsync(GalleryService gallery)
    {
        DisplayLimit += 20;
        await LoadThumbnailsAsync(gallery);
    }

    public async Task LoadThumbnailsAsync(GalleryService gallery)
    {
        foreach (var id in Groups.Take(DisplayLimit).SelectMany(group => group.ImageIds))
            if (!thumbnails.ContainsKey(id)) thumbnails[id] = await gallery.ThumbnailAsync(id);
    }
}
