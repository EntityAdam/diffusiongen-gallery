namespace Gallery.Core.Models;

public sealed record IngestionSpaceEstimate(
    int FileCount, long SelectedSourceBytes, long EstimatedBytes, long RequiredBytes,
    long MediaRequiredBytes, long MediaAvailableBytes, long CatalogRequiredBytes, long CatalogAvailableBytes)
{
    public bool HasEnoughSpace => FileCount > 0 && MediaRequiredBytes <= MediaAvailableBytes
        && CatalogRequiredBytes <= CatalogAvailableBytes;
}
