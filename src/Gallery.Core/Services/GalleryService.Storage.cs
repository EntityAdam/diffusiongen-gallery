using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<StorageOverview?> CachedStorageOverviewAsync() => mediaStorageIndex.CachedStorageOverviewAsync();
    public Task<StorageOverview> RefreshStorageOverviewAsync() => mediaStorageIndex.RefreshStorageOverviewAsync();
}
