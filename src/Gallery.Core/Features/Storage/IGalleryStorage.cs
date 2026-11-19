using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Reads storage usage and rebuilds the media storage index.</summary>
public interface IGalleryStorage
{
    Task RebuildStorageIndexAsync();
    Task<StorageOverview?> CachedStorageOverviewAsync();
    Task<StorageOverview> RefreshStorageOverviewAsync();
}
