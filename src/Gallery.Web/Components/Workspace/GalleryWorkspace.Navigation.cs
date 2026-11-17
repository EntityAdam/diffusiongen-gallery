using Gallery.Core.Models;

namespace Gallery.Web.Components.Workspace;

public partial class GalleryWorkspace
{
    private static readonly (string Id, string Name)[] Workflows =
    [
        ("gallery", "Gallery"), ("collections", "Collections"), ("review", "Review"), ("unorganized", "Unorganized"), ("tags", "Tags"), ("duplicates", "Duplicates"), ("ingest", "Import media"), ("settings", "Settings")
    ];
    private bool SidebarItemDisabled(string id) => WorkspaceBusy
        || catalogPartial && id is "filters" or "organize" or "review" or "intelligence" or "ingest" or "duplicates"
        || libraryJob.IsRunning && id == "ingest";

    private Task NavigateSidebarAsync(string id)
    {
        if (SidebarItemDisabled(id)) return Task.CompletedTask;
        if (id == "gallery")
            return RequestSettingsActionAsync(async () =>
            {
                if (workflow != "gallery") await SwitchWorkflowCoreAsync("gallery");
                await SetActiveToolAsync("");
            });
        if (id is not ("views" or "filters" or "organize" or "review" or "intelligence")) return SwitchWorkflowAsync(id);
        return RequestSettingsActionAsync(async () =>
        {
            if (workflow != "gallery") await SwitchWorkflowCoreAsync("gallery");
            await SetActiveToolAsync(activeTool == id ? "" : id);
        });
    }

    private static readonly (string Id, string Name)[] SettingsTabs =
    [
        ("overview", "Overview"), ("gallery", "Manage gallery"), ("backups", "Backups"), ("endpoints", "LLM endpoints"), ("tags", "Tag import/export"), ("security", "Gallery security")
    ];
    private Task FilterChangedAsync() => RunAsync("Filtering...", async () => { page = 0; ClearSelection(); await LoadThumbnailsAsync(); });
    private Task ResetFilters() { filterInputRevision++; filter = TrashActive ? new() { Deletion = "marked" } : new(); return FilterChangedAsync(); }
    private Task ApplyFilterPanelAsync(GalleryFilter value) { if (catalogPartial) return Task.CompletedTask; filter = value; return FilterChangedAsync(); }
    private Task SetSort(string? value)
    {
        if (catalogPartial) return Task.CompletedTask;
        filter = filter with { Sort = value ?? "newest" };
        return FilterChangedAsync();
    }
    private string ToolBadge(string tool) => tool switch
    {
        "filters" when !TrashActive && GalleryFilterPanel.CountChanged(filter) is > 0 and var count => count.ToString(),
        "trash" when !catalogPartial && images.Count(image => image.IsMarkedForDeletion) is > 0 and var count => count.ToString(),
        "intelligence" when aiSuggestions.Count > 0 => aiSuggestions.Count.ToString(),
        _ => ""
    };
    private async Task SetActiveToolAsync(string value)
    {
        if (Activity.Busy) return;
        if (catalogPartial && value is not ("" or "views")) return;
        value = UiPreferences.ToolboxTools.Contains(value) ? value : "";
        if (activeTool == value) return;
        var wasTrash = TrashActive;
        activeTool = value;
        if (TrashActive != wasTrash)
        {
            filterInputRevision++;
            // Trash temporarily swaps the browsing filter for marked images and restores it when another tool is chosen.
            if (TrashActive) { browsingFilter = filter with { }; filter = new() { Deletion = "marked" }; }
            else { filter = browsingFilter ?? new(); browsingFilter = null; }
            ClearSelection();
            if (catalogPartial) await OpenGalleryAsync();
            await FilterChangedAsync();
        }
        await SaveToolboxStateAsync();
    }
    private async Task ToggleToolboxAsync()
    {
        if (Activity.Busy) return;
        if (workflow != "gallery")
        {
            toolboxVisible = true;
            await SwitchWorkflowAsync("gallery");
        }
        else toolboxVisible = !toolboxVisible;
        await SaveToolboxStateAsync();
    }
    private Task ToolboxMovedAsync((double? X, double? Y) position)
    {
        (toolboxX, toolboxY) = position;
        return SaveToolboxStateAsync();
    }
    private async Task ResetToolboxPositionAsync()
    {
        toolboxVisible = true;
        (toolboxX, toolboxY) = ((double?)null, (double?)null);
        if (toolbox is not null) await toolbox.ResetPositionAsync();
        await SaveToolboxStateAsync();
    }
    private async Task OpenTrashAsync()
    {
        toolboxVisible = true;
        await SwitchWorkflowAsync("gallery");
        await SetActiveToolAsync("trash");
    }
    private Task SwitchWorkflowAsync(string value) => value == workflow
        ? Task.CompletedTask : RequestSettingsActionAsync(() => SwitchWorkflowCoreAsync(value));

    private async Task SwitchWorkflowCoreAsync(string value)
    {
        await ApplyWorkflowAsync(value);
        PublishRoute();
    }

    private Task ApplyWorkflowAsync(string value)
    {
        if (WorkspaceBusy && value != workflow) return Task.CompletedTask;
        layout?.ResetNavigation();
        if (value == "review")
        {
            workflow = "gallery";
            activeTool = "review";
            return FilterChangedAsync();
        }
        workflow = value;
        if (value == "collections") collectionLocation = new();
        if (value == "unorganized") collectionLocation = new(Unorganized: true);
        ClearSelection();
        if (value == "duplicates")
        {
            return RunAsync("Loading duplicates...", async () =>
            {
                await duplicates.LoadAsync(Gallery, imagesById);
                await duplicates.LoadThumbnailsAsync(Gallery);
            });
        }
        if (value == "settings")
        {
            settingsTab = "overview";
            return RunAsync("Loading gallery overview...", async () =>
            {
                page = 0;
                await LoadThumbnailsAsync();
                storageOverview = await Gallery.CachedStorageOverviewAsync();
                await duplicates.LoadAsync(Gallery, imagesById);
            });
        }
        return FilterChangedAsync();
    }
    private Task SwitchSettingsTabAsync(string value) => value == settingsTab
        ? Task.CompletedTask : RequestSettingsActionAsync(() => SwitchSettingsTabCoreAsync(value));

    private async Task SwitchSettingsTabCoreAsync(string value)
    {
        await ApplySettingsTabAsync(value);
        PublishRoute();
    }

    private Task ApplySettingsTabAsync(string value)
    {
        if (value == "endpoints")
            return RunAsync("Loading LLM settings...", async () =>
            {
                settings = await Gallery.SettingsAsync();
                settingsTab = value;
            });
        settingsTab = value;
        return value == "overview"
            ? RunAsync("Loading gallery overview...", async () => storageOverview = await Gallery.CachedStorageOverviewAsync())
            : value == "backups" && backup.Saved is null
                ? RunAsync("Loading backup settings...", async () =>
                {
                    backup.Load(await Gallery.LoadBackupOptionsAsync());
                })
            : Task.CompletedTask;
    }

    private string workflow = "gallery";
}
