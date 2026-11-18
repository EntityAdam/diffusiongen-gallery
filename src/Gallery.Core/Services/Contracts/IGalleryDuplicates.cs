using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Scans for duplicate media and applies keeper decisions.</summary>
public interface IGalleryDuplicates
{
    Task<DuplicateScan?> CachedDuplicateScanAsync();
    Task<DuplicateScan> ScanDuplicatesAsync(IProgress<(int Completed, int Total)>? progress = null);
    ImageRecord PickDuplicateKeeper(IEnumerable<ImageRecord> records);
    HashSet<string> InitialDuplicateKeepers(IReadOnlyCollection<ImageRecord> duplicateImageRecords);
    Task<List<ImageRecord>> ApplyDuplicateDecisionAsync(IReadOnlyCollection<string> groupImageIds, IReadOnlySet<string> keep);
}
