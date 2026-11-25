using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Scans, imports, and verifies media in the active gallery.</summary>
public interface IGalleryIngestion
{
    bool VideoToolsAvailable { get; }
    Task<ImageRecord> ImportAsync(Stream input, string name, string sourceFolder, string sourcePath = "");
    Task ImportFolderAsync(string folder, FolderSearchScope searchScope, Action<string>? progress = null,
        SourceRetention sourceRetention = SourceRetention.Retain, Func<ImportProgress, Task>? reportProgress = null,
        Action<IngestionTimingReport>? reportTimings = null, IngestionOptions? options = null,
        IngestionScan? scan = null, bool includeImages = true, bool includeVideos = true);
    Task VerifyImportedAsync(string id);
    Task<IngestionScan> ScanFolderAsync(string folder, FolderSearchScope searchScope);
    Task<IngestionSpaceEstimate> EstimateIngestionSpaceAsync(IngestionScan scan, bool includeImages, bool includeVideos);
}
