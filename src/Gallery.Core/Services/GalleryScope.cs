using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Ports.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Sessions;
using Gallery.Core.Services.Sessions.Locations;

namespace Gallery.Core.Services;

/// <summary>Owns one vault session and its gallery capabilities. Dispose the scope to lock and release the vault.</summary>
public sealed class GalleryScope : IDisposable
{
    private readonly GalleryLifetime galleryLifetime;
    private readonly VaultSession vaultSession;
    private readonly VaultAccess vaultAccess;
    private readonly GalleryDriveQueue driveQueue;
    private readonly GalleryImportWorkflow importWorkflow;
    private readonly HttpClient? ownedHttpClient;

    public IGalleryBackup Backup { get; }
    public IGalleryCatalog Catalog { get; }
    public IGalleryCollections Collections { get; }
    public IGallerySettings Settings { get; }
    public IGalleryDriveOperations DriveOperations { get; }
    public IGalleryDuplicates Duplicates { get; }
    public IGalleryRecordEditor Records { get; }
    public IGalleryIngestion Ingestion { get; }
    public IGalleryVault Vault { get; }
    public IGalleryMedia Media { get; }
    public IGalleryMediaMaintenance MediaMaintenance { get; }
    public IGalleryRestore Restore { get; }
    public IGalleryStorage Storage { get; }
    public IGalleryTags Tags { get; }
    public IGalleryVision Vision { get; }

    internal GalleryScope(IGalleryStore galleryStore, VaultStore vaultStore, VaultSession vaultSession, VideoTools? videoTools = null,
        VisionManager? visionManager = null, IGalleryStoreFactory? storeFactory = null, HttpClient? httpClient = null)
        : this(galleryStore, vaultStore, vaultSession, videoTools, visionManager, storeFactory, httpClient, ownsHttpClient: false)
    {
    }

    private GalleryScope(IGalleryStore galleryStore, VaultStore vaultStore, VaultSession vaultSession, VideoTools? videoTools,
        VisionManager? visionManager, IGalleryStoreFactory? storeFactory, HttpClient? httpClient, bool ownsHttpClient)
    {
        galleryLifetime = new GalleryLifetime(galleryStore, vaultStore, storeFactory);
        this.vaultSession = vaultSession;
        ownedHttpClient = ownsHttpClient ? httpClient : null;
        vaultAccess = new VaultAccess(galleryLifetime, vaultSession);
        driveQueue = new GalleryDriveQueue(galleryLifetime, vaultSession);
        importWorkflow = new GalleryImportWorkflow(galleryLifetime, vaultSession, videoTools);

        var mediaStorageIndex = new MediaStorageIndex(galleryLifetime, vaultSession, driveQueue);
        var recordEditor = new GalleryRecordEditor(galleryLifetime, vaultSession);
        var collectionOrganizer = new CollectionOrganizer(galleryLifetime, vaultSession);
        var vaultManager = new VaultManager(galleryLifetime, vaultSession);
        var vaultLocations = new VaultLocationCoordinator(galleryLifetime, vaultSession, driveQueue, vaultAccess);

        Backup = new GalleryBackupWriter(galleryLifetime, vaultSession, httpClient);
        Catalog = new GalleryCatalogLoader(galleryLifetime, vaultSession, mediaStorageIndex, driveQueue);
        Collections = collectionOrganizer;
        Settings = new GallerySettings(galleryLifetime, vaultSession);
        DriveOperations = new GalleryDriveControls(driveQueue);
        Duplicates = new GalleryDuplicates(new DuplicateScanner(galleryLifetime, vaultSession), recordEditor);
        Records = recordEditor;
        Ingestion = importWorkflow;
        Vault = new GalleryVault(galleryLifetime, vaultSession, vaultAccess, vaultManager, vaultLocations);
        Media = new GalleryMediaReader(galleryLifetime, vaultSession);
        MediaMaintenance = new GalleryMediaMaintenance(
            new RemovalManager(galleryLifetime, vaultSession, driveQueue.Start),
            new StoredMediaMover(galleryLifetime, vaultSession, driveQueue));
        Restore = new GalleryRestore(vaultLocations, ownedHttpClient);
        Storage = mediaStorageIndex;
        Tags = new TagCatalogManager(galleryLifetime, vaultSession);
        Vision = new GalleryVisionWorkflow(galleryLifetime, vaultSession, visionManager, collectionOrganizer);
    }

    /// <summary>Creates an owned gallery scope and transfers ownership of the supplied HTTP client.</summary>
    public static GalleryScope Create(string vaultRoot, GalleryRuntimeOptions galleryRuntimeOptions, HttpClient ownedHttpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        ArgumentNullException.ThrowIfNull(galleryRuntimeOptions);
        ArgumentNullException.ThrowIfNull(ownedHttpClient);

        VaultSession? vaultSession = null;
        try
        {
            var history = new VaultHistory(galleryRuntimeOptions.StateDirectory ?? GalleryPaths.DefaultApplicationDirectory);
            var toolOptions = VideoToolsOptionsNormalizer.Normalize(
                galleryRuntimeOptions.FFmpegPath, galleryRuntimeOptions.ToolSearchPaths);
            var tools = new VideoTools(toolOptions.ConfiguredPath, toolOptions.SearchPaths);
            var root = history.Read() ?? vaultRoot;
            var storeFactory = new GalleryStoreFactory(galleryRuntimeOptions.CatalogBackend);
            var store = storeFactory.Create(root);
            var vaultStore = new VaultStore(root);
            vaultSession = new VaultSession();
            var gallery = new GalleryScope(store, vaultStore, vaultSession, tools,
                new VisionManager(ownedHttpClient), storeFactory, ownedHttpClient, ownsHttpClient: true);
            gallery.galleryLifetime.InitializeHistory(history);
            return gallery;
        }
        catch
        {
            vaultSession?.Dispose();
            ownedHttpClient.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!galleryLifetime.TryBeginDisposal()) return;
        driveQueue.Dispose();
        vaultSession.Dispose();
        _ = vaultAccess.ReleaseCheckoutAfterOperationsAsync();
        ownedHttpClient?.Dispose();
    }

    internal TimeSpan DriveRetryDelay
    {
        get => driveQueue.DriveRetryDelay;
        set => driveQueue.DriveRetryDelay = value;
    }

    internal Task<List<CatalogImportSource>> ReadImportSourcesAsync() => importWorkflow.ReadImportSourcesAsync();
    internal ValueTask<IAsyncDisposable> OpenCatalogOperationAsync() => importWorkflow.OpenCatalogOperationAsync();
}
