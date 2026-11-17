namespace Gallery.Web.Components.Workspace;

public partial class GalleryWorkspace
{
    private SettingsWorkspace? settingsWorkspace;
    private string settingsTab = "overview";
    private UnsavedChangesGuardState? guard;
    private UnsavedChangesGuardState Guard => guard ??= new(Activity,
        () => WorkspaceBusy || settingsWorkspace?.IsBusy == true, FlushSavedFeedback);
    private IEnumerable<UnsavedChangesSource> DraftSources
    {
        get
        {
            if (mediaHost?.Draft is { } notes) yield return notes;
            if (workflow == "settings" && settingsWorkspace is not null) yield return settingsWorkspace.Draft;
            if (workflow == "ingest") yield return new("settings", () => ingestion.SettingsDirty,
                () => Activity.Busy, async () =>
                {
                    await RunAsync("Saving ingestion settings...", () => ingestion.SaveOptionsAsync(Gallery));
                    return !ingestion.SettingsDirty && Activity.Error.Length == 0;
                }, () => { ingestion.ApplyProfile(ingestion.SavedOptions); return Task.CompletedTask; });
        }
    }
    private Task RequestNavigationAsync(Func<Task> action) => Guard.RequestAsync(DraftSources, () =>
    {
        Presentation.CloseDetail();
        return action();
    });
    private void FlushSavedFeedback()
    {
        if (Activity.Message.Length == 0) return;
        Toasts.Show(Activity.Message, "", ShadCn.Blazor.Components.SonnerToastVariant.Default, 4000);
        Activity.Message = "";
    }
}
