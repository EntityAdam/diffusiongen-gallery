using Gallery.Core.Models;
using Gallery.Core.Services.Processors;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<DuplicateScan?> CachedDuplicateScanAsync() => duplicateScanner.CachedDuplicateScanAsync();

    public Task<DuplicateScan> ScanDuplicatesAsync(IProgress<(int Completed, int Total)>? progress = null) =>
        duplicateScanner.ScanDuplicatesAsync(progress);

    public ImageRecord PickDuplicateKeeper(IEnumerable<ImageRecord> records) => DuplicateRules.PickDuplicateKeeper(records);

    public HashSet<string> InitialDuplicateKeepers(IReadOnlyCollection<ImageRecord> duplicateImageRecords) =>
        DuplicateRules.InitialDuplicateKeepers(duplicateImageRecords);

    /// <summary>Unmarks the kept copies, marks the other copies for deletion, and returns the committed records.</summary>
    public Task<List<ImageRecord>> ApplyDuplicateDecisionAsync(IReadOnlyCollection<string> groupImageIds, IReadOnlySet<string> keep) =>
        recordEditor.ApplyDuplicateDecisionAsync(groupImageIds, keep);
}
