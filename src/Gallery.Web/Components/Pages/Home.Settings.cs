using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    private LlmEndpointSettings? llmEndpointSettings;
    private Func<Task>? pendingSettingsAction;
    private bool SettingsDirty => workflow == "ingest" && IngestionSettingsDirty
        || workflow == "settings" && (settingsTab == "endpoints" && llmEndpointSettings?.HasUnsavedChanges == true
            || settingsTab == "backups" && BackupSettingsDirty);

    private Task RequestSettingsActionAsync(Func<Task> action)
    {
        if (busy || llmEndpointSettings?.IsBusy == true) return Task.CompletedTask;
        if (!SettingsDirty) return action();
        error = "";
        pendingSettingsAction = action;
        return Task.CompletedTask;
    }

    private void KeepEditingSettings()
    {
        if (!busy && llmEndpointSettings?.IsBusy != true) pendingSettingsAction = null;
    }

    private async Task DiscardSettingsAndContinueAsync()
    {
        if (busy || llmEndpointSettings?.IsBusy == true || pendingSettingsAction is not { } action) return;
        if (workflow == "ingest") ApplyIngestionProfile(savedIngestionOptions);
        else if (settingsTab == "endpoints") llmEndpointSettings?.DiscardDraft();
        else if (settingsTab == "backups" && backupOptions is not null)
        {
            backupDestinationType = backupOptions.DestinationType;
            backupLocalDirectory = backupOptions.LocalDirectory;
            backupAzureContainer = backupOptions.AzureContainer;
            backupAzurePrefix = backupOptions.AzurePrefix;
            backupAzureConnectionString = "";
        }
        pendingSettingsAction = null;
        await action();
    }

    private async Task SaveSettingsAndContinueAsync()
    {
        if (busy || llmEndpointSettings?.IsBusy == true || pendingSettingsAction is not { } action) return;
        if (workflow == "ingest") await SaveIngestionOptionsAsync();
        else if (settingsTab == "backups") await SaveBackupOptionsAsync();
        else if (settingsTab == "endpoints" && llmEndpointSettings is not null)
        {
            if (!await llmEndpointSettings.SaveDraftAsync())
            {
                error = llmEndpointSettings.ErrorMessage;
                return;
            }
        }
        if (pendingSettingsAction != action || SettingsDirty || error.Length > 0) return;
        pendingSettingsAction = null;
        if (message.Length > 0)
        {
            Toasts.Show(message, "", ShadCn.Blazor.Components.SonnerToastVariant.Default, 4000);
            message = "";
        }
        await action();
    }

    private Task RenameGalleryAsync(string galleryRename) => RunAsync("Renaming gallery...", async () =>
    {
        await Gallery.RenameVaultAsync(galleryRename);
        message = "Gallery renamed.";
    });

    private Task SaveMediaStorageAsync(string directory) => RunAsync("Saving media location...", async () =>
    {
        await Gallery.SaveMediaStorageOptionsAsync(new MediaStorageOptions { MediaDirectory = directory });
        mediaDirectory = directory;
        message = "Media location saved for future imports. Existing files were not moved.";
    });

    private Task RelocateCatalogAsync(string catalogDirectory) => RunAsync("Relocating catalog...", async () =>
    {
        EnsureNoLibraryJob();
        try { await Gallery.RelocateCatalogAsync(catalogDirectory); }
        finally { if (!Gallery.IsUnlocked) Lock(); }
        message = "Catalog relocated to local storage. Media was not moved. Sign in at the new catalog location.";
    });

    private Task MoveGalleryAsync(string moveGalleryDirectory) => RunAsync("Moving gallery...", async () =>
    {
        EnsureNoLibraryJob();
        try { await Gallery.MoveVaultAsync(moveGalleryDirectory); }
        finally { if (!Gallery.IsUnlocked) Lock(); }
        message = "Gallery moved. Sign in at the new location.";
    });

    private Task PurgeGalleryAsync(string purgeConfirmation) => RunAsync("Purging gallery...", async () =>
    {
        EnsureNoLibraryJob();
        try { await Gallery.PurgeVaultAsync(purgeConfirmation); }
        finally { if (!Gallery.IsUnlocked) Lock(); }
        startWithCreate = true;
        message = "All gallery media and data were permanently destroyed. Backups and original sources were retained.";
    });

    private string settingsTab = "overview";
    private StorageOverview? storageOverview;
    private bool refreshingStorageOverview;
    private BackupOptions? backupOptions;
    private string backupDestinationType = "local", backupLocalDirectory = "", backupAzureContainer = "", backupAzurePrefix = "", backupAzureConnectionString = "";
    private bool BackupSettingsDirty => backupOptions is not null &&
        (backupAzureConnectionString.Length > 0 || backupOptions.DestinationType != backupDestinationType
        || backupOptions.LocalDirectory != backupLocalDirectory.Trim()
        || backupOptions.AzureContainer != backupAzureContainer.Trim()
        || backupOptions.AzurePrefix != backupAzurePrefix.Trim().Trim('/'));
    private bool BackupDestinationReady => backupOptions is not null
        && backupAzureConnectionString.Length == 0
        && backupOptions.DestinationType == backupDestinationType
        && backupOptions.LocalDirectory == backupLocalDirectory.Trim()
        && backupOptions.AzureContainer == backupAzureContainer.Trim()
        && backupOptions.AzurePrefix == backupAzurePrefix.Trim().Trim('/')
        && (backupDestinationType == "local"
            ? backupLocalDirectory.Trim().Length > 0
            : backupAzureContainer.Trim().Length > 0 && backupOptions.HasAzureCredentials);

    private Task SaveBackupOptionsAsync() => RunAsync("Saving backup destination...", async () =>
    {
        backupOptions = await Gallery.SaveBackupOptionsAsync(backupDestinationType, backupLocalDirectory,
            backupAzureContainer, backupAzurePrefix, backupAzureConnectionString);
        backupAzureConnectionString = "";
        backupDestinationType = backupOptions.DestinationType;
        backupLocalDirectory = backupOptions.LocalDirectory;
        backupAzureContainer = backupOptions.AzureContainer;
        backupAzurePrefix = backupOptions.AzurePrefix;
        message = "Backup destination saved in the encrypted gallery.";
    });

    private Task CreateFullBackupAsync() => RunAsync("Creating full backup...", async () =>
    {
        var result = await Gallery.CreateFullBackupAsync();
        backupOptions = await Gallery.LoadBackupOptionsAsync();
        message = $"Full backup created: {result.MediaFilesCopied:N0} encrypted media file(s), {GalleryService.FormatBytes(result.BytesWritten)}.";
    });

    private Task CreateIncrementalBackupAsync() => RunAsync("Creating incremental backup...", async () =>
    {
        var result = await Gallery.CreateIncrementalBackupAsync();
        backupOptions = await Gallery.LoadBackupOptionsAsync();
        message = result.WasSkipped
            ? $"No changes since the last backup ({result.BackupName}); no incremental backup was created."
            : $"Incremental backup created: {result.MediaFilesCopied:N0} changed media file(s) of {result.TotalMediaFiles:N0}, {GalleryService.FormatBytes(result.BytesWritten)}.";
    });

    private Task RebuildStorageIndexAsync() => RunAsync("Scheduling file index rebuild...", async () =>
    {
        await Gallery.RebuildStorageIndexAsync();
        storageOverview = await Gallery.CachedStorageOverviewAsync();
        message = "The file index is being rebuilt in the background. Refresh the overview when it finishes.";
    });

    private async Task RefreshStorageOverviewAsync()
    {
        if (busy) return;
        refreshingStorageOverview = true;
        try
        {
            await RunAsync("Measuring gallery storage...", async () =>
            {
                // SQLite reads/decryption can run synchronously before the service's first yield.
                // Keep that work off the renderer so the busy state is visible throughout the refresh.
                storageOverview = await Task.Run(Gallery.RefreshStorageOverviewAsync);
                message = "Gallery overview refreshed.";
            });
        }
        finally { refreshingStorageOverview = false; }
    }
}
