namespace Gallery.Core.Models;

/// <summary>A consistent catalog snapshot, or a first page while the remaining catalog is loading.</summary>
public sealed record GalleryCatalog(List<ImageRecord> Records, long Revision, int TotalRecords,
    bool IsComplete, bool IsCurrent, bool CacheHit);
