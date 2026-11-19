using Gallery.Core.Features.Backups;
using Gallery.Core.Features.Ingestion.Video;
using Gallery.Core.Features.Ingestion;
using Gallery.Core.Features.Media.Export;
using Gallery.Core.Features.Tags;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Features.Vision.Endpoints;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using GalleryLocation = Gallery.Core.Models.GalleryLocation;

namespace Gallery.Core.Services;

/// <summary>Compatibility adapter for the original gallery API. Use <see cref="GalleryScope"/> for feature contracts.</summary>
public sealed class GalleryService : IDisposable
{
    internal GalleryScope Scope { get; }

    /// <summary>Adapts a scope to the original API. Disposing the adapter also disposes the scope.</summary>
    public GalleryService(GalleryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scope = scope;
    }

    internal GalleryService(IGalleryStore galleryStore, VaultStore vaultStore, VaultSession vaultSession, VideoTools? videoTools = null,
        VisionManager? visionManager = null, IGalleryStoreFactory? storeFactory = null, HttpClient? httpClient = null)
        : this(new GalleryScope(galleryStore, vaultStore, vaultSession, videoTools, visionManager, storeFactory, httpClient))
    {
    }

    /// <summary>Creates a gallery and transfers ownership of the supplied HTTP client.</summary>
    public static GalleryService Create(string vaultRoot, GalleryRuntimeOptions galleryRuntimeOptions, HttpClient ownedHttpClient) =>
        new(GalleryScope.Create(vaultRoot, galleryRuntimeOptions, ownedHttpClient));

    public void Dispose() => Scope.Dispose();


    public const string BackupActivitySourceName = BackupArchive.ActivitySourceName;

    public const long MaxFileBytes = ImportMediaPreparer.MaxFileBytes;

    public const long MaxVideoBytes = ImportMediaPreparer.MaxVideoBytes;

    public static readonly string[] Extensions = IngestionSourceScanner.Extensions;

    public static bool IsVideoName(string name) => IngestionSourceScanner.IsVideoName(name);

    public const long MaxExportZipBytes = ExportComposer.MaxZipBytes;

    public static byte[] CopyExportContent(ExportFile file) => file.CopyContentBytes();

    public static void ClearExportContent(ExportFile file) => file.ClearContent();

    public static string SafeFileName(string name) => ExportComposer.SafeFileName(name);

    public static string FormatDuration(ImageRecord image) => GalleryDisplay.FormatDuration(image);

    public static string FormatBytes(long bytes) => GalleryDisplay.FormatBytes(bytes);

    public static string FormatModelFilename(string model) => GalleryDisplay.FormatModelFilename(model);

    public const long MaxTagCsvBytes = TagCatalogManager.MaxTagCsvBytes;

    public static string DefaultApplicationDirectory => GalleryPaths.DefaultApplicationDirectory;
    public static string DefaultVaultDirectory() => GalleryPaths.DefaultVaultDirectory();

    internal TimeSpan DriveRetryDelay
    {
        get => Scope.DriveRetryDelay;
        set => Scope.DriveRetryDelay = value;
    }

    internal Task<List<CatalogImportSource>> ReadImportSourcesAsync() => Scope.ReadImportSourcesAsync();

    internal ValueTask<IAsyncDisposable> OpenCatalogOperationAsync() => Scope.OpenCatalogOperationAsync();

    public const string VaultOpenActivitySourceName = VaultOpenTelemetry.SourceName;
    public const string CatalogMeterName = CatalogMetrics.MeterName;

    public Task<BackupOptions> LoadBackupOptionsAsync() => Scope.Backup.LoadBackupOptionsAsync();
    public Task<BackupOptions> SaveBackupOptionsAsync(
        string destinationType, string localDirectory, string azureContainer,
        string azurePrefix, string azureConnectionString) =>
        Scope.Backup.SaveBackupOptionsAsync(destinationType, localDirectory, azureContainer, azurePrefix, azureConnectionString);
    public Task<BackupRunResult> CreateFullBackupAsync() => Scope.Backup.CreateFullBackupAsync();
    public Task<BackupRunResult> CreateIncrementalBackupAsync() => Scope.Backup.CreateIncrementalBackupAsync();

    public Task<GalleryCatalog> LoadInitialPageAsync(int pageSize = 49, bool markedOnly = false, CancellationToken cancellationToken = default) =>
        Scope.Catalog.LoadInitialPageAsync(pageSize, markedOnly, cancellationToken);
    public Task<GalleryCatalog> LoadCatalogAsync(CancellationToken cancellationToken = default) =>
        Scope.Catalog.LoadCatalogAsync(cancellationToken);
    public Task<List<ImageRecord>> ReloadRecordsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) =>
        Scope.Catalog.ReloadRecordsAsync(ids, cancellationToken);
    public Task<long> CountRecordsAsync(CancellationToken cancellationToken = default) =>
        Scope.Catalog.CountRecordsAsync(cancellationToken);
    public Task<List<ImageRecord>> ListAsync() => Scope.Catalog.ListAsync();
    public Task<List<ImageRecord>> LoadAsync() => Scope.Catalog.LoadAsync();
    public List<ImageRecord> Filter(IEnumerable<ImageRecord> records, GalleryFilter filter,
        IReadOnlySet<string>? awaitingApprovalIds = null) =>
        Scope.Catalog.Filter(records, filter, awaitingApprovalIds);
    public List<PromptGroup> GroupByPrompt(IEnumerable<ImageRecord> records) => Scope.Catalog.GroupByPrompt(records);
    public List<DiffusionModelGroup> GroupByDiffusionModel(IEnumerable<ImageRecord> records) =>
        Scope.Catalog.GroupByDiffusionModel(records);

    public Task<CollectionCatalog> LoadCollectionCatalogAsync() => Scope.Collections.LoadCollectionCatalogAsync();
    public Task<CollectionDefinition> CreateCollectionAsync(string name) => Scope.Collections.CreateCollectionAsync(name);
    public Task<MediaSet> CreateSetAsync(string collectionId, string name) =>
        Scope.Collections.CreateSetAsync(collectionId, name);
    public Task<CollectionMutation> RenameCollectionAsync(string id, string name) =>
        Scope.Collections.RenameCollectionAsync(id, name);
    public Task<CollectionMutation> RenameSetAsync(string id, string name) => Scope.Collections.RenameSetAsync(id, name);
    public Task<CollectionMutation> MoveMediaToSetAsync(IEnumerable<string> mediaIds, string? setId) =>
        Scope.Collections.MoveMediaToSetAsync(mediaIds, setId);
    public Task<CollectionMutation> MoveMediaToDestinationAsync(IEnumerable<string> mediaIds, string collectionName,
        string setName, string? collectionId = null, string? setId = null) =>
        Scope.Collections.MoveMediaToDestinationAsync(mediaIds, collectionName, setName, collectionId, setId);
    public Task<CollectionMutation> DeleteContainerAsync(string id, bool isSet) =>
        Scope.Collections.DeleteContainerAsync(id, isSet);
    public Task<CollectionMutation> MoveSetAsync(string setId, string collectionId) =>
        Scope.Collections.MoveSetAsync(setId, collectionId);
    public Task<CollectionMutation> ReorderSetAsync(string id, int offset) =>
        Scope.Collections.ReorderSetAsync(id, offset);
    public Task<CollectionMutation> ReorderMediaAsync(string setId, string mediaId, int offset) =>
        Scope.Collections.ReorderMediaAsync(setId, mediaId, offset);
    public Task UndoCollectionChangeAsync(CollectionUndo undo) => Scope.Collections.UndoCollectionChangeAsync(undo);

    public Task<UiPreferences> LoadPreferencesAsync() => Scope.Settings.LoadPreferencesAsync();
    public Task SavePreferencesAsync(UiPreferences preferences) => Scope.Settings.SavePreferencesAsync(preferences);
    public Task<IngestionOptions> LoadIngestionOptionsAsync() => Scope.Settings.LoadIngestionOptionsAsync();
    public Task SaveIngestionOptionsAsync(IngestionOptions options) => Scope.Settings.SaveIngestionOptionsAsync(options);
    public Task<MediaStorageOptions> LoadMediaStorageOptionsAsync() => Scope.Settings.LoadMediaStorageOptionsAsync();
    public Task SaveMediaStorageOptionsAsync(MediaStorageOptions options) =>
        Scope.Settings.SaveMediaStorageOptionsAsync(options);
    public Task<List<SavedView>> LoadViewsAsync() => Scope.Settings.LoadViewsAsync();
    public Task SaveViewAsync(string name, GalleryFilter filter) => Scope.Settings.SaveViewAsync(name, filter);
    public Task DeleteViewAsync(string name) => Scope.Settings.DeleteViewAsync(name);

    public event Action? DriveQueueChanged
    {
        add => Scope.DriveOperations.DriveQueueChanged += value;
        remove => Scope.DriveOperations.DriveQueueChanged -= value;
    }
    public DriveQueueStatus DriveQueueStatus => Scope.DriveOperations.DriveQueueStatus;
    public Task PauseDriveOperationsAsync() => Scope.DriveOperations.PauseDriveOperationsAsync();
    public void ResumeDriveOperations() => Scope.DriveOperations.ResumeDriveOperations();
    public Task CancelDriveOperationsAsync() => Scope.DriveOperations.CancelDriveOperationsAsync();
    public Task RetryFailedDriveOperationsAsync() => Scope.DriveOperations.RetryFailedDriveOperationsAsync();
    public Task WaitForDriveOperationsAsync() => Scope.DriveOperations.WaitForDriveOperationsAsync();
    public Task RefreshDriveQueueStatusAsync() => Scope.DriveOperations.RefreshDriveQueueStatusAsync();

    public Task<DuplicateScan?> CachedDuplicateScanAsync() => Scope.Duplicates.CachedDuplicateScanAsync();
    public Task<DuplicateScan> ScanDuplicatesAsync(IProgress<(int Completed, int Total)>? progress = null) =>
        Scope.Duplicates.ScanDuplicatesAsync(progress);
    public ImageRecord PickDuplicateKeeper(IEnumerable<ImageRecord> records) =>
        Scope.Duplicates.PickDuplicateKeeper(records);
    public HashSet<string> InitialDuplicateKeepers(IReadOnlyCollection<ImageRecord> duplicateImageRecords) =>
        Scope.Duplicates.InitialDuplicateKeepers(duplicateImageRecords);
    public Task<List<ImageRecord>> ApplyDuplicateDecisionAsync(IReadOnlyCollection<string> groupImageIds, IReadOnlySet<string> keep) =>
        Scope.Duplicates.ApplyDuplicateDecisionAsync(groupImageIds, keep);

    public ImageRecordEdit ApplyReviewDecision(ImageRecordEdit image, ReviewAction action, int rating = 0) =>
        Scope.Records.ApplyReviewDecision(image, action, rating);
    public ReviewState CaptureReviewState(ImageRecord image) => Scope.Records.CaptureReviewState(image);
    public ReviewState CaptureReviewState(ImageRecordEdit image) => Scope.Records.CaptureReviewState(image);
    public Task<ImageRecord> RestoreReviewStateAsync(string id, ReviewState state) =>
        Scope.Records.RestoreReviewStateAsync(id, state);
    public ImageRecordEdit SetCuration(ImageRecordEdit image, int? rating, bool? reviewed) =>
        Scope.Records.SetCuration(image, rating, reviewed);
    public ImageRecordEdit SetDeletionMark(ImageRecordEdit image, bool markedForDeletion) =>
        Scope.Records.SetDeletionMark(image, markedForDeletion);
    public ImageRecordEdit SetOrganization(ImageRecordEdit image, int rating, bool reviewed, string? collection) =>
        Scope.Records.SetOrganization(image, rating, reviewed, collection);
    public ImageRecordEdit AddCollection(ImageRecordEdit image, string collection) =>
        Scope.Records.AddCollection(image, collection);
    public ImageRecordEdit RemoveCollection(ImageRecordEdit image, string collection) =>
        Scope.Records.RemoveCollection(image, collection);
    public ImageRecord ApplyEdit(ImageRecord image, Func<ImageRecordEdit, ImageRecordEdit> update) =>
        Scope.Records.ApplyEdit(image, update);
    public string MergeTags(string existing, string additions) => Scope.Records.MergeTags(existing, additions);
    public string RemoveTag(string existing, string tag) => Scope.Records.RemoveTag(existing, tag);
    public Task<ImageRecord> UpdateAsync(string id, Func<ImageRecordEdit, ImageRecordEdit> update) =>
        Scope.Records.UpdateAsync(id, update);
    public Task<List<ImageRecord>> UpdateManyAsync(IEnumerable<string> ids, Func<ImageRecordEdit, ImageRecordEdit> update) =>
        Scope.Records.UpdateManyAsync(ids, update);

    public bool VideoToolsAvailable => Scope.Ingestion.VideoToolsAvailable;
    public Task<ImageRecord> ImportAsync(Stream input, string name, string sourceFolder, string sourcePath = "") =>
        Scope.Ingestion.ImportAsync(input, name, sourceFolder, sourcePath);
    public Task ImportFolderAsync(string folder, FolderSearchScope searchScope, Action<string>? progress = null,
        SourceRetention sourceRetention = SourceRetention.Retain, Func<ImportProgress, Task>? reportProgress = null,
        Action<IngestionTimingReport>? reportTimings = null, IngestionOptions? options = null,
        IngestionScan? scan = null, bool includeImages = true, bool includeVideos = true) =>
        Scope.Ingestion.ImportFolderAsync(folder, searchScope, progress, sourceRetention, reportProgress, reportTimings, options, scan, includeImages, includeVideos);
    public Task VerifyImportedAsync(string id) => Scope.Ingestion.VerifyImportedAsync(id);
    public Task<IngestionScan> ScanFolderAsync(string folder, FolderSearchScope searchScope) =>
        Scope.Ingestion.ScanFolderAsync(folder, searchScope);
    public Task<IngestionSpaceEstimate> EstimateIngestionSpaceAsync(IngestionScan scan, bool includeImages, bool includeVideos) =>
        Scope.Ingestion.EstimateIngestionSpaceAsync(scan, includeImages, includeVideos);

    public bool CatalogIsOnNetworkDrive => Scope.Vault.CatalogIsOnNetworkDrive;
    public Task RelocateCatalogAsync(string directory) => Scope.Vault.RelocateCatalogAsync(directory);
    public bool IsUnlocked => Scope.Vault.IsUnlocked;
    public bool VaultExists => Scope.Vault.VaultExists;
    public string VaultRoot => Scope.Vault.VaultRoot;
    public Task<string> CreateVaultAsync(string passphrase, string confirmation, string? name = null,
     MediaStorageOptions? storageOptions = null) =>
        Scope.Vault.CreateVaultAsync(passphrase, confirmation, name, storageOptions);
    public Task UnlockWithPassphraseAsync(string passphrase) => Scope.Vault.UnlockWithPassphraseAsync(passphrase);
    public Task UnlockWithRecoveryPhraseAsync(string recoveryPhrase) =>
        Scope.Vault.UnlockWithRecoveryPhraseAsync(recoveryPhrase);
    public Task ChangePassphraseAsync(string passphrase, string confirmation) =>
        Scope.Vault.ChangePassphraseAsync(passphrase, confirmation);
    public event Action? OnAutoLocked
    {
        add => Scope.Vault.OnAutoLocked += value;
        remove => Scope.Vault.OnAutoLocked -= value;
    }
    public void ConfigureAutoLock(int minutes) => Scope.Vault.ConfigureAutoLock(minutes);
    public void ConfigureInactivityTimeout(TimeSpan? timeout) => Scope.Vault.ConfigureInactivityTimeout(timeout);
    public void RecordActivity() => Scope.Vault.RecordActivity();
    public void Panic() => Scope.Vault.Panic();
    public void LockVault() => Scope.Vault.LockVault();
    public string VaultName => Scope.Vault.VaultName;
    public IReadOnlyList<GalleryLocation> KnownGalleries => Scope.Vault.KnownGalleries;
    public Task OpenExistingVaultAsync(string directory) => Scope.Vault.OpenExistingVaultAsync(directory);
    public Task SelectNewVaultAsync(string directory) => Scope.Vault.SelectNewVaultAsync(directory);
    public Task CloseVaultAsync() => Scope.Vault.CloseVaultAsync();
    public Task RenameVaultAsync(string name) => Scope.Vault.RenameVaultAsync(name);
    public Task MoveVaultAsync(string directory) => Scope.Vault.MoveVaultAsync(directory);
    public Task PurgeVaultAsync(string confirmation) => Scope.Vault.PurgeVaultAsync(confirmation);

    public Task<string> ThumbnailAsync(string id) => Scope.Media.ThumbnailAsync(id);
    public Task<string> PreviewAsync(string id, ImageResolution resolution = ImageResolution.Fit) =>
        Scope.Media.PreviewAsync(id, resolution);
    public Task<string> ViewerImageAsync(string id, ImageResolution resolution) =>
        Scope.Media.ViewerImageAsync(id, resolution);
    public Task<(Stream Content, string ContentType)> OpenMediaAsync(string id) => Scope.Media.OpenMediaAsync(id);
    public Task<ExportFile> ExportAsync(string id) => Scope.Media.ExportAsync(id);
    public Task<ExportFile> ExportZipAsync(IEnumerable<string> ids) => Scope.Media.ExportZipAsync(ids);
    public Task<ExportFile?> WorkflowAsync(string id) => Scope.Media.WorkflowAsync(id);

    public Task MoveAsync(string id, string folder, string name) => Scope.MediaMaintenance.MoveAsync(id, folder, name);
    public Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids) =>
        Scope.MediaMaintenance.PrepareRemovalAsync(ids);
    public Task RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval = null) =>
        Scope.MediaMaintenance.RemoveAsync(removalItems, existingAction, missingAction, restoreFolder, reportRemoval);

    public Task<IReadOnlyList<string>> ListRestoreBackupsAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString) =>
        Scope.Restore.ListRestoreBackupsAsync(sourceType, localDirectory, azureContainer, azurePrefix, connectionString);
    public Task<IReadOnlyList<string>> ListRestoreBackupsFromSasAsync(string sasUrl, string azurePrefix = "") =>
        Scope.Restore.ListRestoreBackupsFromSasAsync(sasUrl, azurePrefix);
    public Task RestoreVaultAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix,
        string connectionString, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null) =>
        Scope.Restore.RestoreVaultAsync(sourceType, localDirectory, azureContainer, azurePrefix, connectionString, latestBackupName, destinationDirectory, credential, useRecovery, name);
    public Task RestoreGalleryFromSasAsync(
        string sasUrl, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null, string azurePrefix = "") =>
        Scope.Restore.RestoreGalleryFromSasAsync(sasUrl, latestBackupName, destinationDirectory, credential, useRecovery, name, azurePrefix);

    public Task RebuildStorageIndexAsync() => Scope.Storage.RebuildStorageIndexAsync();
    public Task<StorageOverview?> CachedStorageOverviewAsync() => Scope.Storage.CachedStorageOverviewAsync();
    public Task<StorageOverview> RefreshStorageOverviewAsync() => Scope.Storage.RefreshStorageOverviewAsync();

    public Task<IReadOnlyList<TagDefinition>> LoadTagCatalogAsync() => Scope.Tags.LoadTagCatalogAsync();
    public Task<IReadOnlyList<string>> LoadTagNamesAsync() => Scope.Tags.LoadTagNamesAsync();
    public Task AddTagAsync(string name) => Scope.Tags.AddTagAsync(name);
    public Task RenameTagAsync(string name, string replacement) => Scope.Tags.RenameTagAsync(name, replacement);
    public Task DeleteTagAsync(string name) => Scope.Tags.DeleteTagAsync(name);
    public Task<(int Added, int Skipped)> ImportTagsCsvAsync(Stream input) => Scope.Tags.ImportTagsCsvAsync(input);
    public Task<ExportFile> ExportTagsCsvAsync() => Scope.Tags.ExportTagsCsvAsync();

    public Task<MediaSet?> SuggestDestinationSetAsync(string collectionId, IEnumerable<string> mediaIds) =>
        Scope.Vision.SuggestDestinationSetAsync(collectionId, mediaIds);
    public Task<VisionSettings> SettingsAsync() => Scope.Vision.LoadSettingsAsync();
    public Task SaveSettingsAsync(VisionSettings settings) => Scope.Vision.SaveSettingsAsync(settings);
    public Task<VisionCheckpointSession> OpenVisionCheckpointAsync() => Scope.Vision.OpenVisionCheckpointAsync();
    public Task<IReadOnlyList<string>> GetVisionModelsAsync(VisionSettings settings) =>
        Scope.Vision.GetVisionModelsAsync(settings);
    public Task<(string Description, string Tags)> AnalyzeImageAsync(
        VisionSettings settings, string imageData, CancellationToken cancellationToken = default) =>
        Scope.Vision.AnalyzeImageAsync(settings, imageData, cancellationToken);
    public Task<VisionAnalysisResult> AnalyzeForDraftAsync(
        string id, VisionSettings settings, CancellationToken token = default) =>
        Scope.Vision.AnalyzeForDraftAsync(id, settings, token);
    public Task<ImageRecord> ApproveVisionAsync(string id, string description, string tags, string model,
        VisionAnalysis? analysis, bool tagsOnly = false, bool useExistingTags = true, bool suggestNewTags = true) =>
        Scope.Vision.ApproveVisionAsync(id, description, tags, model, analysis, tagsOnly, useExistingTags, suggestNewTags);
    public Task<VisionAnalysis?> ReadVisionAnalysisAsync(string id) => Scope.Vision.ReadVisionAnalysisAsync(id);
    public Task<ExportFile> ExportVisionAnalysisAsync(string id) => Scope.Vision.ExportVisionAnalysisAsync(id);
}
