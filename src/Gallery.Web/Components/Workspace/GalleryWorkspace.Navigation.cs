using Gallery.Core.Models;

namespace Gallery.Web.Components.Workspace;

public partial class GalleryWorkspace
{
    private static readonly (string Id, string Name)[] Workflows =
    [
        ("gallery", "Gallery"), ("collections", "Collections"), ("review", "Review"), ("unorganized", "Unorganized"), ("tags", "Tags"), ("duplicates", "Duplicates"), ("ingest", "Import media"), ("settings", "Settings")
    ];
    private bool SidebarItemDisabled(string id) => WorkspaceBusy
        || Catalog.IsPartial && id is "filters" or "organize" or "review" or "intelligence" or "ingest" or "duplicates"
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
            await SetActiveToolAsync(Browser.ActiveTool == id ? "" : id);
        });
    }

    private Task SetActiveToolAsync(string value) => Activity.Busy ? Task.CompletedTask : Browser.SetActiveToolAsync(value);
    private Task FilterChangedAsync() => RunAsync("Filtering...", Browser.FilterChangedAsync);

    private async Task ToggleToolboxAsync()
    {
        if (Activity.Busy) return;
        if (workflow != "gallery")
        {
            Browser.ToolboxVisible = true;
            await SwitchWorkflowAsync("gallery");
        }
        else Browser.ToolboxVisible = !Browser.ToolboxVisible;
        await SaveToolboxStateAsync();
    }
    private async Task ResetToolboxPositionAsync()
    {
        Browser.ToolboxVisible = true;
        (Browser.ToolboxX, Browser.ToolboxY) = ((double?)null, (double?)null);
        if (galleryBrowser is not null) await galleryBrowser.ResetToolboxPositionAsync();
        await SaveToolboxStateAsync();
    }
    private async Task OpenTrashAsync()
    {
        Browser.ToolboxVisible = true;
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
            Browser.ActiveTool = "review";
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
                await duplicates.LoadAsync(Gallery, Catalog.ImagesById);
                await duplicates.LoadThumbnailsAsync(Gallery);
            });
        }
        if (value == "settings") settingsTab = "overview";
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
        settingsTab = value;
        return settingsWorkspace?.LoadAsync(value) ?? Task.CompletedTask;
    }

    private string workflow = "gallery";
}
