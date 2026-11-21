namespace Gallery.Core.Infrastructure.Persistence;

internal sealed class CatalogRevisionConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException($"Catalog revision changed from {expectedRevision} to {actualRevision} before the operation could complete.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
