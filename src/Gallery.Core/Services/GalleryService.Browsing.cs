using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<GalleryCatalog> LoadInitialPageAsync(int pageSize = 49, bool markedOnly = false, CancellationToken cancellationToken = default) =>
        catalogLoader.LoadInitialPageAsync(pageSize, markedOnly, cancellationToken);

    public Task<GalleryCatalog> LoadCatalogAsync(CancellationToken cancellationToken = default) =>
        catalogLoader.LoadCatalogAsync(cancellationToken);

    public Task<List<ImageRecord>> ReloadRecordsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) =>
        catalogLoader.ReloadRecordsAsync(ids, cancellationToken);

    /// <summary>Counts catalog entries without reading or decoding media records.</summary>
    public Task<long> CountRecordsAsync(CancellationToken cancellationToken = default) =>
        catalogLoader.CountRecordsAsync(cancellationToken);

    public Task<List<ImageRecord>> ListAsync() => catalogLoader.ListAsync();
    public Task<List<ImageRecord>> LoadAsync() => catalogLoader.LoadAsync();
}
