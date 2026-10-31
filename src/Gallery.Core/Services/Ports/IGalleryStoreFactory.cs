using Gallery.Core.Models;

namespace Gallery.Core.Services.Ports;

internal interface IGalleryStoreFactory
{
    CatalogBackend NewCatalogBackend { get; }
    IGalleryStore Create(string vaultRoot);
    IGalleryStore CreateForDatabase(string databasePath, CatalogBackend backend);
    IGalleryStore Create(string vaultRoot, CatalogBackend backend);
}
