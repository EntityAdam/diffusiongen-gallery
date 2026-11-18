using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<DuplicateScan?> CachedDuplicateScanAsync() => duplicateScanner.CachedDuplicateScanAsync();

    public Task<DuplicateScan> ScanDuplicatesAsync(IProgress<(int Completed, int Total)>? progress = null) =>
        duplicateScanner.ScanDuplicatesAsync(progress);

    /// <summary>Unmarks the kept copies, marks the other copies for deletion, and returns the committed records.</summary>
    public async Task<List<ImageRecord>> ApplyDuplicateDecisionAsync(IReadOnlyCollection<string> groupImageIds, IReadOnlySet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(groupImageIds);
        ArgumentNullException.ThrowIfNull(keep);
        if (keep.Count == 0) throw new InvalidOperationException("Keep at least one media item in the group.");
        var invalidKeeperIds = keep.Where(imageId => !groupImageIds.Contains(imageId)).ToArray();
        if (invalidKeeperIds.Length > 0)
            throw new InvalidOperationException(
                $"Kept media items [{string.Join(", ", invalidKeeperIds)}] must belong to the group. Valid media IDs: [{string.Join(", ", groupImageIds)}].");
        var retainedIds = keep.ToHashSet(StringComparer.Ordinal);
        return await UpdateManyAsync(groupImageIds,
            record => SetDeletionMark(record, !retainedIds.Contains(record.Id)));
    }
}
