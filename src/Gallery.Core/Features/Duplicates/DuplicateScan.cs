using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record DuplicateScan(ImmutableArray<DuplicateGroup> Groups, int ScannedCount, DateTimeOffset ScannedAt)
{
    public int DuplicateCount => Groups.Sum(duplicateGroup => duplicateGroup.ImageIds.Length - 1);
    public long ReclaimableBytes => Groups.Sum(duplicateGroup => duplicateGroup.ReclaimableBytes);
}
