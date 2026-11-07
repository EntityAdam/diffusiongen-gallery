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
}
