namespace Gallery.Web.Components.Workspace;

public partial class GalleryWorkspace
{
    private SettingsWorkspace? settingsWorkspace;
    private Func<Task>? pendingSettingsAction;
    private bool SettingsDirty => workflow == "ingest" && ingestion.SettingsDirty
        || workflow == "settings" && settingsWorkspace?.HasUnsavedChanges == true;

    private Task RequestSettingsActionAsync(Func<Task> action)
    {
        if (Activity.Busy || settingsWorkspace?.IsBusy == true) return Task.CompletedTask;
        if (!SettingsDirty) return action();
        Activity.Error = "";
        pendingSettingsAction = action;
        return Task.CompletedTask;
    }

    private void KeepEditingSettings()
    {
        if (!Activity.Busy && settingsWorkspace?.IsBusy != true) pendingSettingsAction = null;
    }

    private async Task DiscardSettingsAndContinueAsync()
    {
        if (Activity.Busy || settingsWorkspace?.IsBusy == true || pendingSettingsAction is not { } action) return;
        if (workflow == "ingest") ingestion.ApplyProfile(ingestion.SavedOptions);
        else settingsWorkspace?.Discard();
        pendingSettingsAction = null;
        await action();
    }

    private async Task SaveSettingsAndContinueAsync()
    {
        if (Activity.Busy || settingsWorkspace?.IsBusy == true || pendingSettingsAction is not { } action) return;
        if (workflow == "ingest") await RunAsync("Saving ingestion settings...", () => ingestion.SaveOptionsAsync(Gallery));
        else if (settingsWorkspace is not null && !await settingsWorkspace.SaveAsync()) return;
        if (pendingSettingsAction != action || SettingsDirty || Activity.Error.Length > 0) return;
        pendingSettingsAction = null;
        if (Activity.Message.Length > 0)
        {
            Toasts.Show(Activity.Message, "", ShadCn.Blazor.Components.SonnerToastVariant.Default, 4000);
            Activity.Message = "";
        }
        await action();
    }

    private string settingsTab = "overview";

}
