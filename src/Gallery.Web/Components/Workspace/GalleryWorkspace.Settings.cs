using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Workspace;

public partial class GalleryWorkspace
{
    private LlmEndpointSettings? llmEndpointSettings;
    private Func<Task>? pendingSettingsAction;
    private bool SettingsDirty => workflow == "ingest" && ingestion.SettingsDirty
        || workflow == "settings" && (settingsTab == "endpoints" && llmEndpointSettings?.HasUnsavedChanges == true
            || settingsTab == "backups" && backup.HasChanges);

    private Task RequestSettingsActionAsync(Func<Task> action)
    {
        if (Activity.Busy || llmEndpointSettings?.IsBusy == true) return Task.CompletedTask;
        if (!SettingsDirty) return action();
        Activity.Error = "";
        pendingSettingsAction = action;
        return Task.CompletedTask;
    }

    private void KeepEditingSettings()
    {
        if (!Activity.Busy && llmEndpointSettings?.IsBusy != true) pendingSettingsAction = null;
    }

    private async Task DiscardSettingsAndContinueAsync()
    {
        if (Activity.Busy || llmEndpointSettings?.IsBusy == true || pendingSettingsAction is not { } action) return;
        if (workflow == "ingest") ingestion.ApplyProfile(ingestion.SavedOptions);
        else if (settingsTab == "endpoints") llmEndpointSettings?.DiscardDraft();
        else if (settingsTab == "backups") backup.Discard();
        pendingSettingsAction = null;
        await action();
    }

    private async Task SaveSettingsAndContinueAsync()
    {
        if (Activity.Busy || llmEndpointSettings?.IsBusy == true || pendingSettingsAction is not { } action) return;
        if (workflow == "ingest") await RunAsync("Saving ingestion settings...", () => ingestion.SaveOptionsAsync(Gallery));
        else if (settingsTab == "backups") await RunAsync("Saving backup destination...", () => backup.SaveAsync(Gallery));
        else if (settingsTab == "endpoints" && llmEndpointSettings is not null)
        {
            if (!await llmEndpointSettings.SaveDraftAsync())
            {
                Activity.Error = llmEndpointSettings.ErrorMessage;
                return;
            }
        }
        if (pendingSettingsAction != action || SettingsDirty || Activity.Error.Length > 0) return;
        pendingSettingsAction = null;
        if (Activity.Message.Length > 0)
        {
            Toasts.Show(Activity.Message, "", ShadCn.Blazor.Components.SonnerToastVariant.Default, 4000);
            Activity.Message = "";
        }
        await action();
    }

    private BackupDraft backup = new();
    private string settingsTab = "overview";
    private StorageOverview? storageOverview;

}
