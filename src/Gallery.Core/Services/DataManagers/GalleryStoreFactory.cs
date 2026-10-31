using Gallery.Core.Models;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.DataManagers.Turso;

namespace Gallery.Core.Services.DataManagers;

/// <summary>Composition boundary: application services depend only on the catalog ports.</summary>
internal sealed class GalleryStoreFactory(CatalogBackend newCatalogBackend = CatalogBackend.Sqlite) : IGalleryStoreFactory
{
    public CatalogBackend NewCatalogBackend { get; } = Enum.IsDefined(newCatalogBackend)
        ? newCatalogBackend : throw new ArgumentOutOfRangeException(nameof(newCatalogBackend));

    public IGalleryStore Create(string vaultRoot)
    {
        var vault = new VaultStore(vaultRoot);
        return Create(vaultRoot, vault.Exists ? vault.CatalogBackend : NewCatalogBackend);
    }

    public IGalleryStore Create(string vaultRoot, CatalogBackend backend) =>
        CreateForDatabase(Path.Combine(vaultRoot, GalleryService.CatalogFileName), backend);

    public IGalleryStore CreateForDatabase(string databasePath, CatalogBackend backend) => backend switch
    {
        CatalogBackend.Sqlite => new GalleryStore(databasePath),
        CatalogBackend.Turso => new TursoGalleryStore(databasePath),
        _ => throw new InvalidOperationException("Unsupported catalog storage adapter.")
    };
}
