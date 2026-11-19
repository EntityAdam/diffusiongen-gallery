using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Reads catalog snapshots and applies browsing projections.</summary>
public interface IGalleryCatalog
{
    Task<GalleryCatalog> LoadInitialPageAsync(int pageSize = 49, bool markedOnly = false, CancellationToken cancellationToken = default);
    Task<GalleryCatalog> LoadCatalogAsync(CancellationToken cancellationToken = default);
    Task<List<ImageRecord>> ReloadRecordsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);
    Task<long> CountRecordsAsync(CancellationToken cancellationToken = default);
    Task<List<ImageRecord>> ListAsync();
    Task<List<ImageRecord>> LoadAsync();
    List<ImageRecord> Filter(IEnumerable<ImageRecord> records, GalleryFilter filter,
        IReadOnlySet<string>? awaitingApprovalIds = null);
    List<PromptGroup> GroupByPrompt(IEnumerable<ImageRecord> records);
    List<DiffusionModelGroup> GroupByDiffusionModel(IEnumerable<ImageRecord> records);
}
