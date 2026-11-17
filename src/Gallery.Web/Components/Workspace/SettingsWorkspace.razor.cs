using Gallery.Core.Models;
using Microsoft.AspNetCore.Components;

namespace Gallery.Web.Components.Workspace;

public partial class SettingsWorkspace
{
    [CascadingParameter] public WorkspaceCommands Commands { get; set; } = default!;
    [Parameter] public bool Active { get; set; }
    [Parameter] public string Tab { get; set; } = "overview";
    [Parameter] public EventCallback<string> TabChanged { get; set; }
    [Parameter, EditorRequired] public GalleryActivity Activity { get; set; } = default!;
    [Parameter, EditorRequired] public WorkspaceCatalog Catalog { get; set; } = default!;
    [Parameter, EditorRequired] public DuplicateCatalog Duplicates { get; set; } = default!;
    [Parameter] public bool JobRunning { get; set; }
    [Parameter] public EventCallback Locked { get; set; }
    [Parameter] public EventCallback Purged { get; set; }
    [Parameter] public EventCallback Panic { get; set; }
    [Parameter] public EventCallback FindDuplicates { get; set; }
    [Parameter] public EventCallback Reload { get; set; }
    [Parameter] public int AutoLockMinutes { get; set; }
    [Parameter] public EventCallback<string?> UpdateTimer { get; set; }

    internal static readonly (string Id, string Name)[] Tabs =
    [
        ("overview", "Overview"), ("gallery", "Manage gallery"), ("backups", "Backups"),
        ("endpoints", "LLM endpoints"), ("tags", "Tag import/export"), ("security", "Gallery security")
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

    protected override async Task OnParametersSetAsync()
    {
        if (!Active) { loadedTab = null; return; }
        if (loadedTab == Tab) return;
        await LoadAsync(Tab);
    }

    public async Task LoadAsync(string tab)
    {
        if (disposed || Activity.Busy) return;
        // Set before awaiting: shared feedback can render the parent while loading.
        loadedTab = tab;
        await Commands.RunAsync("Loading gallery settings...", async () =>
        {
            if (tab == "endpoints")
            {
                var value = await Gallery.SettingsAsync();
                if (!disposed) settings = value;
            }
            else if (tab == "backups" && backup.Saved is null)
            {
                var value = await Gallery.LoadBackupOptionsAsync();
                if (!disposed) backup.Load(value);
            }
            else if (tab == "overview")
            {
                var value = await Gallery.CachedStorageOverviewAsync();
                if (!disposed) storageOverview = value;
                if (!disposed) await Duplicates.LoadAsync(Gallery, Catalog.ImagesById);
            }
        });
    }

    public void Discard()
    {
        if (Tab == "endpoints") llmEndpointSettings?.DiscardDraft();
        else if (Tab == "backups") backup.Discard();
    }
    public async Task<bool> SaveAsync()
    {
        if (Tab == "backups") await Commands.RunAsync("Saving backup destination...", () => backup.SaveAsync(Gallery));
        else if (Tab == "endpoints" && llmEndpointSettings is not null && !await llmEndpointSettings.SaveDraftAsync())
        {
            Activity.Error = llmEndpointSettings.ErrorMessage;
            return false;
        }
        return !HasUnsavedChanges && Activity.Error.Length == 0;
    }
    public void Dispose() => disposed = true;
}
