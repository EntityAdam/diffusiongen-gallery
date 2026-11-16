using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    [Parameter] public string? Route { get; set; }
    [Parameter] public NavigationManager? Router { get; set; }

    private string? lastRoute;
    private string CurrentPath => workflow == "settings" && settingsTab != "overview"
        ? $"settings/{settingsTab}" : workflow;
    private bool HasUnsavedChanges => SettingsDirty || NotesDirty;

    protected override async Task OnParametersSetAsync()
    {
        if (Route == lastRoute) return;
        lastRoute = Route;
        await ApplyRouteAsync();
    }

    private async Task ApplyRouteAsync()
    {
        if (Route is null || !Gallery.IsUnlocked || recoveryPhrase.Length > 0) return;
        var path = NormalizePath(Route);
        if (path.Length == 0) path = "gallery";
        var parts = path.Split('/');
        if (!Workflows.Any(item => item.Id == parts[0])) return;
        if (workflow != parts[0]) await ApplyWorkflowAsync(parts[0]);
        var tab = parts.Length == 2 ? parts[1] : "overview";
        if (workflow == "settings" && settingsTab != tab && SettingsTabs.Any(item => item.Id == tab))
            await ApplySettingsTabAsync(tab);
        PublishRoute();
    }

    private static string NormalizePath(string route) =>
        route.Split('?', '#')[0].Trim('/').ToLowerInvariant();

    private void PublishRoute()
    {
        if (Router is not null)
        {
            var path = NormalizePath(Router.ToBaseRelativePath(Router.Uri));
            if (path != CurrentPath) Router.NavigateTo(CurrentPath, replace: path.Length == 0);
        }
    }

    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (Router is null) return;
        var path = NormalizePath(Router.ToBaseRelativePath(Router.ToAbsoluteUri(context.TargetLocation).AbsoluteUri));
        if (!Gallery.IsUnlocked || path == CurrentPath) return;
        if (WorkspaceBusy || llmEndpointSettings?.IsBusy == true ||
            (libraryJob.IsRunning && path.Split('/')[0] is "ingest" or "settings"))
        {
            context.PreventNavigation();
            return;
        }
        if (HasUnsavedChanges)
        {
            context.PreventNavigation();
            await RequestDetailActionAsync(() => RequestSettingsActionAsync(() =>
            {
                Router.NavigateTo(context.TargetLocation);
                return Task.CompletedTask;
            }));
            StateHasChanged();
            return;
        }
        if (selected is not null) CloseDetail();
    }
}
