using Gallery.Core.Models;
using Gallery.Web.Components.Features.Backups;
using Gallery.Web.Components.Features.Duplicates;
using Gallery.Web.Components.Features.Vision;
using Gallery.Web.Components.Workspace;
using Microsoft.AspNetCore.Components;

namespace Gallery.Web.Components.Features.Settings;

public partial class SettingsWorkspace
{
    [CascadingParameter] public WorkspaceCommands Commands { get; set; } = default!;
    [Parameter] public bool Active { get; set; }
    [Parameter] public string Tab { get; set; } = "overview";
    [Parameter] public EventCallback<string> TabChanged { get; set; }
    [Parameter, EditorRequired] public GalleryActivity Activity { get; set; } = default!;
    [Parameter, EditorRequired] public WorkspaceCatalog Catalog { get; set; } = default!;
    [Parameter, EditorRequired] public DuplicateWorkflowState Duplicates { get; set; } = default!;
    [Parameter] public bool JobRunning { get; set; }
    [Parameter] public EventCallback Locked { get; set; }
    [Parameter] public EventCallback Purged { get; set; }
    [Parameter] public EventCallback Panic { get; set; }
    [Parameter] public EventCallback FindDuplicates { get; set; }
    [Parameter] public EventCallback Reload { get; set; }
    [Parameter, EditorRequired] public WorkspacePreferences Preferences { get; set; } = default!;
    [CascadingParameter] public WorkspaceNavigationState? Navigation { get; set; }

    internal static readonly (string Id, string Name)[] Tabs =
    [
        ("overview", "Overview"), ("gallery", "Manage gallery"), ("backups", "Backups"),
        ("endpoints", "LLM endpoints"), ("tags", "Tag import/export"), ("security", "Gallery security"),
        ("experimental", "Experimental features")
    ];
    private LlmEndpointSettings? llmEndpointSettings;
    private readonly BackupDraft backup = new();
    private VisionSettings settings = new();
    private StorageOverview? storageOverview;
    private string? loadedTab;
    private bool disposed;
    public bool IsBusy => llmEndpointSettings?.IsBusy == true;
    public bool HasUnsavedChanges => Active && (Tab == "endpoints" && llmEndpointSettings?.HasUnsavedChanges == true
        || Tab == "backups" && backup.HasChanges);

    protected override void OnInitialized() => Navigation?.AddDraftSource(GetDraft);

    private UnsavedChangesSource? GetDraft() => Active ? Draft : null;

    private Task UpdateTimerAsync(string? value) => Preferences.UpdateAutoLockAsync(value, Commands);

    private Task SetExperimentAsync(ExperimentalFeature feature, bool enabled) =>
        Commands.RunAsync("Saving experimental features...", () => Preferences.SetExperimentAsync(feature, enabled));

    protected override async Task OnParametersSetAsync()
    {
        if (!Active) { loadedTab = null; return; }
        if (loadedTab == Tab) return;
        await LoadAsync(Tab);
    }

    private async Task LoadAsync(string tab)
    {
        if (disposed || Activity.Busy) return;
        // Set before awaiting: shared feedback can render the parent while loading.
        loadedTab = tab;
        await Commands.RunAsync("Loading gallery settings...", async () =>
        {
            if (tab == "endpoints")
            {
                var value = await GalleryVision.LoadSettingsAsync();
                if (value.Lanes.IsDefaultOrEmpty)
                {
                    var checkpoint = await GalleryVision.OpenVisionCheckpointAsync();
                    value = value with { Lanes = value.GetLanes(checkpoint.State.AutoAcceptVisionAnalysis) };
                }
                if (!disposed) settings = value;
            }
            else if (tab == "backups" && backup.Saved is null)
            {
                var value = await GalleryBackup.LoadBackupOptionsAsync();
                if (!disposed) backup.Load(value);
            }
            else if (tab == "overview")
            {
                var value = await GalleryStorage.CachedStorageOverviewAsync();
                if (!disposed) storageOverview = value;
                if (!disposed) await Duplicates.LoadSummaryAsync();
            }
        });
    }

    public UnsavedChangesSource Draft => new("settings", () => HasUnsavedChanges, () => IsBusy,
        SaveAsync, () => { Discard(); return Task.CompletedTask; });

    public void Discard()
    {
        if (Tab == "endpoints") llmEndpointSettings?.DiscardDraft();
        else if (Tab == "backups") backup.Discard();
    }
    public async Task<bool> SaveAsync()
    {
        if (Tab == "backups") await Commands.RunAsync("Saving backup destination...", () => backup.SaveAsync(GalleryBackup));
        else if (Tab == "endpoints" && llmEndpointSettings is not null && !await llmEndpointSettings.SaveDraftAsync())
        {
            Activity.Error = llmEndpointSettings.ErrorMessage;
            return false;
        }
        return !HasUnsavedChanges && Activity.Error.Length == 0;
    }
    public void Dispose()
    {
        disposed = true;
        Navigation?.RemoveDraftSource(GetDraft);
    }
}
