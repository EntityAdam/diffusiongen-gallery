using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence.Sqlite;
using Gallery.Core.Infrastructure.Persistence.Turso;
using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence;

/// <summary>Composition boundary: application services depend only on the catalog ports.</summary>
internal sealed class GalleryStoreFactory(CatalogBackend newCatalogBackend = CatalogBackend.Sqlite) : IGalleryStoreFactory
{
    internal const string CatalogFileName = "gallery.db";

    public CatalogBackend NewCatalogBackend { get; } = Enum.IsDefined(newCatalogBackend)
        ? newCatalogBackend : throw new ArgumentOutOfRangeException(nameof(newCatalogBackend));

    public IGalleryStore Create(string vaultRoot)
    {
        var vault = new VaultStore(vaultRoot);
        return Create(vaultRoot, vault.Exists ? vault.CatalogBackend : NewCatalogBackend);
    }

    public IGalleryStore Create(string vaultRoot, CatalogBackend backend) =>
        CreateForDatabase(Path.Combine(vaultRoot, CatalogFileName), backend);

    public IGalleryStore CreateForDatabase(string databasePath, CatalogBackend backend) => backend switch
    {
        CatalogBackend.Sqlite => new GalleryStore(databasePath),
        CatalogBackend.Turso => new TursoGalleryStore(databasePath),
        _ => throw new InvalidOperationException("Unsupported catalog storage adapter.")
    };
}
