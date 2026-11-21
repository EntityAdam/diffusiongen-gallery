using Gallery.Core.Features.Ingestion.Gateways;
using Gallery.Core.Features.Settings;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Shared.Validation;

namespace Gallery.Core.Features.Ingestion;

internal sealed class GalleryImportWorkflow(GalleryLifetime galleryLifetime, VaultSession vaultSession, VideoTools? videoTools) : IGalleryIngestion
{
    private readonly ImportMediaPreparer importMediaPreparer = new(videoTools);

    public bool VideoToolsAvailable => importMediaPreparer.VideoToolsAvailable;

    public async Task<ImageRecord> ImportAsync(Stream input, string name, string sourceFolder, string sourcePath = "")
    {
        MediaImporter.ValidateImportName(name);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var location = galleryLifetime.Current;
        var store = location.GalleryStore;
        await using var catalogOperation = await store.OpenOperationAsync(key.Bytes);
        var importer = new MediaImporter(store, store.Gate, importMediaPreparer);
        await importer.PrepareFingerprintIndexAsync(key.Bytes);
        var mediaRoot = await GallerySettings.ReadMediaDirectoryAsync(location, key.Bytes);
        return await importer.ImportAsync(input, name, sourceFolder, sourcePath, key.Bytes, mediaRoot);
    }

    public async Task ImportFolderAsync(string folder, FolderSearchScope searchScope, Action<string>? progress = null,
        SourceRetention sourceRetention = SourceRetention.Retain, Func<ImportProgress, Task>? reportProgress = null,
        Action<IngestionTimingReport>? reportTimings = null, IngestionOptions? options = null,
        IngestionScan? scan = null, bool includeImages = true, bool includeVideos = true)
    {
        OperationChoiceValidator.Validate(searchScope);
        OperationChoiceValidator.Validate(sourceRetention);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var location = galleryLifetime.Current;
        var store = location.GalleryStore;
        await using var catalogOperation = await store.OpenOperationAsync(key.Bytes);
        options ??= await store.ReadSettingsAsync<IngestionOptions>(IngestionOptions.SettingId, IngestionOptions.SettingPurpose, key.Bytes)
            ?? new IngestionOptions();
        IngestionOptionsValidator.Validate(options);
        var mediaRoot = await GallerySettings.ReadMediaDirectoryAsync(location, key.Bytes);
        var importer = new MediaImporter(store, store.Gate, importMediaPreparer);
        await importer.ImportFolderAsync(folder, searchScope, progress, sourceRetention, reportProgress, reportTimings,
            options, scan, SelectMedia(includeImages, includeVideos), location.VaultStore.Root, mediaRoot, key.Bytes);
    }

    public async Task VerifyImportedAsync(string id)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        var importer = new MediaImporter(store, store.Gate, importMediaPreparer);
        await importer.VerifyImportedAsync(id, key.Bytes);
    }

    public async Task<IngestionScan> ScanFolderAsync(string folder, FolderSearchScope searchScope)
    {
        OperationChoiceValidator.Validate(searchScope);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var location = galleryLifetime.Current;
        var mediaRoot = await GallerySettings.ReadMediaDirectoryAsync(location, key.Bytes);
        return await IngestionSourceScanner.ScanAsync(folder, searchScope, location.VaultStore.Root, mediaRoot);
    }

    public async Task<IngestionSpaceEstimate> EstimateIngestionSpaceAsync(
        IngestionScan scan, bool includeImages, bool includeVideos)
    {
        ArgumentNullException.ThrowIfNull(scan);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var location = galleryLifetime.Current;
        var mediaRoot = await GallerySettings.ReadMediaDirectoryAsync(location, key.Bytes);
        IngestionSourceScanner.ValidateScan(scan, scan.Folder, scan.SearchScope, location.VaultStore.Root, mediaRoot);
        var files = scan.SelectFiles(SelectMedia(includeImages, includeVideos));
        return await Task.Run(() => IngestionSourceScanner.MeasureSpace(scan, files));
    }

    private static IngestionMediaSelection SelectMedia(bool includeImages, bool includeVideos) =>
        (includeImages ? IngestionMediaSelection.Images : IngestionMediaSelection.None)
        | (includeVideos ? IngestionMediaSelection.Videos : IngestionMediaSelection.None);

    internal async Task<List<CatalogImportSource>> ReadImportSourcesAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryLifetime.Current.GalleryStore.ReadImportSourcesAsync(key.Bytes);
    }

    // Stress recovery makes many independent verification calls. Bind their shared resources to a
    // real vault-key lease so lock, close and relocation still drain all keyed handles first.
    internal async ValueTask<IAsyncDisposable> OpenCatalogOperationAsync()
    {
        var key = vaultSession.AcquireUnlockedSession().Borrow();
        try { return new LeasedCatalogOperation(await galleryLifetime.Current.GalleryStore.OpenOperationAsync(key.Bytes), key); }
        catch { key.Dispose(); throw; }
    }

    private sealed class LeasedCatalogOperation(IAsyncDisposable operation, KeyLease key) : IAsyncDisposable
    {
        private int disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await operation.DisposeAsync(); }
            finally { key.Dispose(); }
        }
    }
}
