using Gallery.Web.Components.Features.Browsing;
using Gallery.Web.Components.Features.Collections;
using Gallery.Web.Components.Features.Duplicates;
using Gallery.Web.Components.Features.Media;
using Gallery.Web.Components.Features.Settings;
using Gallery.Web.Components.Features.Vision;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components;

namespace Gallery.Web.Components.Workspace;

public sealed class WorkspaceNavigationState(
    GalleryBrowserState browser, WorkspaceCatalog catalog, DuplicateWorkflowState duplicates,
    CollectionBrowsingState collections, LibraryAnalysis analysis, MediaPresentationState presentation,
    WorkspaceCommands commands, UnsavedChangesGuardState guard, Func<bool> isBusy)
{
    internal static readonly (string Id, string Name)[] Sections =
    [
        ("gallery", "Gallery"), ("collections", "Collections"), ("review", "Review"),
        ("unorganized", "Unorganized"), ("tags", "Tags"), ("duplicates", "Duplicates"),
        ("ingest", "Import media"), ("settings", "Settings")
    ];

    private readonly List<Func<UnsavedChangesSource?>> draftSources = [];
    private NavigationManager? router;
    private string? lastRoute;

    public string Section { get; private set; } = "gallery";
    public string SettingsTab { get; private set; } = "overview";
    internal bool HasUnsavedChanges => Drafts.Any(source => source.HasUnsavedChanges());
    private IEnumerable<UnsavedChangesSource> Drafts => draftSources.Select(source => source()).OfType<UnsavedChangesSource>();
    private string CurrentPath => Section == "settings" && SettingsTab != "overview" ? $"settings/{SettingsTab}" : Section;
    private GalleryActivity Activity => commands.Activity;

    internal void AddDraftSource(Func<UnsavedChangesSource?> source) => draftSources.Add(source);
    internal void RemoveDraftSource(Func<UnsavedChangesSource?> source) => draftSources.Remove(source);
    internal void ClearDraftSources() => draftSources.Clear();

    internal bool SidebarItemDisabled(string id) => isBusy()
        || catalog.IsPartial && id is "filters" or "organize" or "review" or "intelligence" or "ingest" or "duplicates"
        || analysis.Status.IsRunning && id is "ingest" or "settings";

    internal Task NavigateSidebarAsync(string id)
    {
        if (SidebarItemDisabled(id)) return Task.CompletedTask;
        if (id == "gallery") return InGalleryAsync(() => SetActiveToolAsync(""));
        return id is "views" or "filters" or "organize" or "review" or "intelligence"
            ? InGalleryAsync(() => SetActiveToolAsync(browser.ActiveTool == id ? "" : id))
            : SwitchWorkflowAsync(id);
    }

    internal Task SetActiveToolAsync(string tool) =>
        Activity.Busy ? Task.CompletedTask : browser.SetActiveToolAsync(tool);

    internal Task ToggleToolboxAsync() => Activity.Busy ? Task.CompletedTask
        : Section == "gallery" ? browser.ToggleToolboxAsync() : InGalleryAsync(browser.ShowToolboxAsync);

    internal Task OpenTrashAsync() => InGalleryAsync(async () =>
    {
        browser.ToolboxVisible = true;
        await SetActiveToolAsync("trash");
    });

    internal Task ViewTagAsync(string tag) =>
        catalog.IsPartial ? Task.CompletedTask : InGalleryAsync(() => commands.RunAsync("Filtering...", () => browser.ViewTagAsync(tag)));

    internal Task ShowPromptAsync(string prompt) => catalog.IsPartial ? Task.CompletedTask
        : InGalleryAsync(() => commands.RunAsync("Filtering...", () => browser.ApplyFilterAsync(browser.Filter with { Prompt = prompt })));

    internal Task SwitchWorkflowAsync(string section) => section == Section || SidebarItemDisabled(section)
        ? Task.CompletedTask : RequestAsync(async () =>
        {
            await ApplySectionAsync(section);
            PublishRoute();
        });

    internal Task SwitchSettingsTabAsync(string tab) =>
        tab == SettingsTab ? Task.CompletedTask : RequestAsync(() =>
        {
            SettingsTab = tab;
            PublishRoute();
            return Task.CompletedTask;
        });

    internal Task NavigateCollectionsAsync(CollectionLocation location) => RequestAsync(() =>
    {
        collections.Navigate(location);
        Section = location.Unorganized ? "unorganized" : "collections";
        duplicates.Active = false;
        PublishRoute();
        return Task.CompletedTask;
    });

    private Task InGalleryAsync(Func<Task> action) => RequestAsync(async () =>
    {
        if (Section != "gallery") await ApplySectionAsync("gallery");
        await action();
        PublishRoute();
    });

    private Task RequestAsync(Func<Task> action) => guard.RequestAsync(Drafts, () =>
    {
        presentation.CloseDetail();
        return action();
    });

    private Task ApplySectionAsync(string section, string tab = "overview")
    {
        if (section == "review")
        {
            Section = "gallery";
            duplicates.Active = false;
            browser.ActiveTool = "review";
            return commands.RunAsync("Filtering...", browser.FilterChangedAsync);
        }

        Section = section;
        duplicates.Active = section == "duplicates";
        if (section == "collections") collections.Navigate(new());
        if (section == "unorganized") collections.Navigate(new(Unorganized: true));
        if (section == "settings") SettingsTab = tab;
        browser.ClearSelection();
        return section == "duplicates"
            ? commands.RunAsync("Loading duplicates...", duplicates.OpenAsync)
            : commands.RunAsync("Filtering...", browser.FilterChangedAsync);
    }

    internal async Task ReceiveRouteAsync(string? route, NavigationManager? navigationManager)
    {
        router = navigationManager;
        if (route == lastRoute || !presentation.CanPresent) return;
        lastRoute = route;
        if (Activity.Ingestion.IsRunning)
        {
            Section = "ingest";
            duplicates.Active = false;
            PublishRoute();
            return;
        }
        if (route is null) return;

        var path = NormalizePath(route);
        var parts = (path.Length == 0 ? "gallery" : path).Split('/');
        if (!Sections.Any(item => item.Id == parts[0])) return;
        var tab = parts.Length == 2 && SettingsWorkspace.Tabs.Any(item => item.Id == parts[1]) ? parts[1] : "overview";
        if (Section != parts[0] && !isBusy()) await ApplySectionAsync(parts[0], tab);
        else if (Section == "settings" && parts[0] == Section && !isBusy()) SettingsTab = tab;
        PublishRoute();
    }

    internal async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (router is not { } navigation) return;
        var path = NormalizePath(navigation.ToBaseRelativePath(navigation.ToAbsoluteUri(context.TargetLocation).AbsoluteUri));
        if (!presentation.CanPresent || path == CurrentPath) return;
        if (guard.IsBusy || guard.IsPending || Drafts.Any(source => source.IsBusy())
            || (analysis.Status.IsRunning && path.Split('/')[0] is "ingest" or "settings"))
        {
            context.PreventNavigation();
            return;
        }
        if (HasUnsavedChanges)
        {
            context.PreventNavigation();
            await RequestAsync(() =>
            {
                navigation.NavigateTo(context.TargetLocation);
                return Task.CompletedTask;
            });
            return;
        }
        presentation.CloseDetail();
    }

    private static string NormalizePath(string route) => route.Split('?', '#')[0].Trim('/').ToLowerInvariant();

    private void PublishRoute()
    {
        if (router is not { } navigation || !presentation.CanPresent) return;
        var path = NormalizePath(navigation.ToBaseRelativePath(navigation.Uri));
        if (path != CurrentPath) navigation.NavigateTo(CurrentPath, replace: path.Length == 0);
    }
}
