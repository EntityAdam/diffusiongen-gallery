using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Ports.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public const long MaxFileBytes = ImportMediaPreparer.MaxFileBytes;
    public const long MaxVideoBytes = ImportMediaPreparer.MaxVideoBytes;
    public static readonly string[] Extensions = IngestionSourceScanner.Extensions;
    public bool VideoToolsAvailable => importWorkflow.VideoToolsAvailable;

    public static bool IsVideoName(string name) => IngestionSourceScanner.IsVideoName(name);

    public Task<ImageRecord> ImportAsync(Stream input, string name, string sourceFolder, string sourcePath = "") =>
        importWorkflow.ImportAsync(input, name, sourceFolder, sourcePath);

    public Task ImportFolderAsync(string folder, FolderSearchScope searchScope, Action<string>? progress = null,
        SourceRetention sourceRetention = SourceRetention.Retain, Func<ImportProgress, Task>? reportProgress = null,
        Action<IngestionTimingReport>? reportTimings = null, IngestionOptions? options = null,
        IngestionScan? scan = null, bool includeImages = true, bool includeVideos = true) =>
        importWorkflow.ImportFolderAsync(folder, searchScope, progress, sourceRetention, reportProgress, reportTimings,
            options, scan, includeImages, includeVideos);

    public Task VerifyImportedAsync(string id) => importWorkflow.VerifyImportedAsync(id);

    public Task<IngestionScan> ScanFolderAsync(string folder, FolderSearchScope searchScope) =>
        importWorkflow.ScanFolderAsync(folder, searchScope);

    public Task<IngestionSpaceEstimate> EstimateIngestionSpaceAsync(IngestionScan scan, bool includeImages, bool includeVideos) =>
        importWorkflow.EstimateIngestionSpaceAsync(scan, includeImages, includeVideos);

    internal Task<List<CatalogImportSource>> ReadImportSourcesAsync() => importWorkflow.ReadImportSourcesAsync();
    internal ValueTask<IAsyncDisposable> OpenCatalogOperationAsync() => importWorkflow.OpenCatalogOperationAsync();
}
