using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence;

/// <summary>Reads browse data using the caller's leased vault key.</summary>
internal interface IGalleryBrowseReader
{
    Task<long> CountRecordsAsync(byte[] key, CancellationToken token = default);
    Task<List<ImageRecord>> ReadRecordsAsync(byte[] key);
    Task<GalleryCatalog?> ReadInitialPageAsync(int pageSize, bool markedOnly, byte[] key, CancellationToken token);
    Task<GalleryCatalog> ReadCatalogSnapshotAsync(byte[] key, CancellationToken token);
    Task<List<ImageRecord>> ReadBrowseRecordsAsync(IEnumerable<string> ids, byte[] key, CancellationToken token);
}
