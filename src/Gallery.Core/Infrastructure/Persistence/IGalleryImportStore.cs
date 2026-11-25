using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence;

/// <summary>Persists prepared imports and verifies them using the caller's leased vault key.</summary>
internal interface IGalleryImportStore
{
    Task<ImportCommitResult> CommitImportsAsync(IReadOnlyList<ImportCatalogEntry> importCatalogEntries, byte[] key);
    Task<(ImageRecord Record, ImageThumbnail Thumbnail)> ReadVerificationAsync(string id, byte[] key);
    Task BackfillFingerprintsAsync(byte[] key);
    Task<bool> ContainsFingerprintAsync(string sourceSha256, byte[] key);
}
