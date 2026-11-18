using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Validators;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public const long MaxFileBytes = ImportMediaPreparer.MaxFileBytes;
    public const long MaxVideoBytes = ImportMediaPreparer.MaxVideoBytes;
    public static readonly string[] Extensions = IngestionSourceScanner.Extensions;
    public bool VideoToolsAvailable => importMediaPreparer.VideoToolsAvailable;

    public static bool IsVideoName(string name) => IngestionSourceScanner.IsVideoName(name);

    public async Task<ImageRecord> ImportAsync(Stream input, string name, string sourceFolder, string sourcePath = "")
    {
        GalleryIngestion.ValidateImportName(name);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryStore;
        await using var catalogOperation = await store.OpenOperationAsync(key.Bytes);
        var ingestion = new GalleryIngestion(store, importMediaPreparer);
        await ingestion.PrepareFingerprintIndexAsync(key.Bytes);
        var mediaRoot = await MediaDirectoryWithKeyAsync(key.Bytes);
        return await ingestion.ImportAsync(input, name, sourceFolder, sourcePath, key.Bytes, mediaRoot);
    }

    public async Task ImportFolderAsync(string folder, FolderSearchScope searchScope, Action<string>? progress = null,
        SourceRetention sourceRetention = SourceRetention.Retain, Func<ImportProgress, Task>? reportProgress = null,
        Action<IngestionTimingReport>? reportTimings = null, IngestionOptions? options = null,
        IngestionScan? scan = null, bool includeImages = true, bool includeVideos = true)
    {
        ValidateOperationChoice(searchScope);
        ValidateOperationChoice(sourceRetention);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryStore;
        await using var catalogOperation = await store.OpenOperationAsync(key.Bytes);
        options ??= await store.ReadSettingsAsync<IngestionOptions>("ingestion-options", "ingestion-options:v1", key.Bytes)
            ?? new IngestionOptions();
        IngestionOptionsValidator.Validate(options);
        var mediaRoot = await MediaDirectoryWithKeyAsync(key.Bytes);
        var ingestion = new GalleryIngestion(store, importMediaPreparer);
        await ingestion.ImportFolderAsync(folder, searchScope, progress, sourceRetention, reportProgress, reportTimings,
            options, scan, includeImages, includeVideos, VaultRoot, mediaRoot, key.Bytes);
    }

    public async Task VerifyImportedAsync(string id)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var ingestion = new GalleryIngestion(galleryStore, importMediaPreparer);
        await ingestion.VerifyImportedAsync(id, key.Bytes);
    }

    public async Task<IngestionScan> ScanFolderAsync(string folder, FolderSearchScope searchScope)
    {
        ValidateOperationChoice(searchScope);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var catalogRoot = VaultRoot;
        var mediaRoot = await MediaDirectoryWithKeyAsync(key.Bytes);
        return await IngestionSourceScanner.ScanAsync(folder, searchScope, catalogRoot, mediaRoot);
    }

    public async Task<IngestionSpaceEstimate> EstimateIngestionSpaceAsync(
        IngestionScan scan, bool includeImages, bool includeVideos)
    {
        ArgumentNullException.ThrowIfNull(scan);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var mediaRoot = await MediaDirectoryWithKeyAsync(key.Bytes);
        IngestionSourceScanner.ValidateScan(scan, scan.Folder, scan.SearchScope, VaultRoot, mediaRoot);
        var files = scan.SelectFiles(includeImages, includeVideos);
        return await Task.Run(() => IngestionSourceScanner.MeasureSpace(scan, files));
    }
}
