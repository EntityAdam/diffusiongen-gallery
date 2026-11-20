using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence;

/// <summary>Reads individual media records and thumbnails using the caller's leased vault key.</summary>
internal interface IGalleryMediaStoreReader
{
    Task<ImageRecord> ReadRecordAsync(string id, byte[] key);
    Task<ImageThumbnail> ReadThumbnailAsync(string id, byte[] key);
    Task<ImageThumbnail> ReadBrowsingThumbnailAsync(string id, byte[] key) => ReadThumbnailAsync(id, key);
}
