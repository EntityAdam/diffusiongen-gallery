using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Sessions;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    private readonly GalleryLifetime galleryLifetime;
    private readonly VaultManager vaultManager;
    private readonly RemovalManager removalManager;
    private readonly GalleryMediaReader mediaReader;
    private readonly VaultSession vaultSession;
    private readonly GalleryImportWorkflow importWorkflow;
    private readonly GallerySettings gallerySettings;
    private readonly GalleryRecordEditor recordEditor;
    private readonly VaultAccess vaultAccess;
    private readonly GalleryVisionWorkflow visionWorkflow;
    private readonly CollectionOrganizer collectionOrganizer;
    private readonly GalleryBackupWriter backupWriter;
    private readonly DuplicateScanner duplicateScanner;
    private readonly GalleryDriveQueue driveQueue;
    private readonly MediaStorageIndex mediaStorageIndex;
    private readonly TagCatalogManager tagCatalogManager;
    private readonly GalleryCatalogLoader catalogLoader;
    private readonly StoredMediaMover storedMediaMover;
    private readonly VaultLocationCoordinator vaultLocations;

    internal GalleryService(IGalleryStore galleryStore, VaultStore vaultStore, VaultSession vaultSession, VideoTools? videoTools = null,
        VisionManager? visionManager = null, IGalleryStoreFactory? storeFactory = null, HttpClient? httpClient = null)
    {
        galleryLifetime = new GalleryLifetime(galleryStore, vaultStore, storeFactory);
        this.vaultSession = vaultSession;
        vaultManager = new VaultManager(galleryLifetime, vaultSession);
        mediaReader = new GalleryMediaReader(galleryLifetime, vaultSession);
        importWorkflow = new GalleryImportWorkflow(galleryLifetime, vaultSession, videoTools);
        gallerySettings = new GallerySettings(galleryLifetime, vaultSession);
        recordEditor = new GalleryRecordEditor(galleryLifetime, vaultSession);
        vaultAccess = new VaultAccess(galleryLifetime, vaultSession);
        collectionOrganizer = new CollectionOrganizer(galleryLifetime, vaultSession);
        visionWorkflow = new GalleryVisionWorkflow(galleryLifetime, vaultSession, visionManager, collectionOrganizer);
        backupWriter = new GalleryBackupWriter(galleryLifetime, vaultSession, httpClient);
        duplicateScanner = new DuplicateScanner(galleryLifetime, vaultSession);
        driveQueue = new GalleryDriveQueue(galleryLifetime, vaultSession);
        mediaStorageIndex = new MediaStorageIndex(galleryLifetime, vaultSession, driveQueue);
        removalManager = new RemovalManager(galleryLifetime, vaultSession, driveQueue.Start);
        tagCatalogManager = new TagCatalogManager(galleryLifetime, vaultSession);
        catalogLoader = new GalleryCatalogLoader(galleryLifetime, vaultSession, mediaStorageIndex, driveQueue);
        storedMediaMover = new StoredMediaMover(galleryLifetime, vaultSession, driveQueue);
        vaultLocations = new VaultLocationCoordinator(galleryLifetime, vaultSession, driveQueue, vaultAccess);
    }

    public Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids) =>
        removalManager.PrepareRemovalAsync(ids);

    public Task RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval = null) =>
        removalManager.RemoveAsync(removalItems, existingAction, missingAction, restoreFolder, reportRemoval);
}
