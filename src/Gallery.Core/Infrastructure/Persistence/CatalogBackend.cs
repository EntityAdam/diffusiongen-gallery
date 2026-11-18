namespace Gallery.Core.Models;

/// <summary>Storage adapter used when creating a new vault. Existing vaults select their own recorded format.</summary>
public enum CatalogBackend
{
    Sqlite,
    Turso
}
