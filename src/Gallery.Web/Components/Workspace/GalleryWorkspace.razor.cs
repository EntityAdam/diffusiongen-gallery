using Gallery.Core.Models;
using Gallery.Web.Components.Features.Browsing;
using Gallery.Web.Components.Features.Collections;
using Gallery.Web.Components.Features.Duplicates;
using Gallery.Web.Components.Features.Media;
using Microsoft.AspNetCore.Components;

namespace Gallery.Web.Components.Workspace;

public partial class GalleryWorkspace
{
    [Parameter, EditorRequired] public WorkspaceSession Session { get; set; } = default!;
    [Parameter] public string? Route { get; set; }
    [Parameter] public NavigationManager? Router { get; set; }
    [Parameter] public EventCallback Locked { get; set; }
    [Parameter] public EventCallback Purged { get; set; }
    [Parameter] public EventCallback Panic { get; set; }

    private GalleryActivity Activity => Session.Activity;
    private WorkspaceCatalog Catalog => Session.Catalog;
    private WorkspaceNavigationState Navigation => Session.Navigation;
    private MediaPresentationState Presentation => Session.Presentation;
    private GalleryBrowserState Browser => Session.Browser;
    private string Section => Navigation.Section;
    private bool UsesToasts => !Presentation.DetailsOpen && !Session.Removal.IsOpen && !Session.Guard.IsPending;
    private bool JobRunning => Session.Analysis.Status.IsRunning;

    private GalleryBrowser? galleryBrowser;
    private CollectionsWorkspace? collectionsWorkspace;
    private DuplicateFinder? duplicateFinder;
    private bool disposed;

    protected override void OnInitialized() => Session.Changed += RefreshWorkspace;

    protected override Task OnParametersSetAsync() => Navigation.ReceiveRouteAsync(Route, Router);

    private void RefreshWorkspace()
    {
        if (!disposed) StateHasChanged();
    }

    private Task OpenCollectionDetailsAsync((ImageRecord[] Images, string InitialId) request) =>
        Presentation.OpenAsync(request.Images.First(image => image.Id == request.InitialId), request.Images);

    private bool GridBlocked() => !Presentation.CanPresent || Activity.Busy || Presentation.IsOpen
        || Session.Removal.IsOpen || Section == "duplicates" && duplicateFinder?.ReviewOpen == true;

    private Task HandleGridShortcutAsync((string Key, string Id, bool Shift) shortcut) => Section switch
    {
        "collections" or "unorganized" => collectionsWorkspace?.HandleGridKeyAsync(shortcut.Key, shortcut.Id, shortcut.Shift) ?? Task.CompletedTask,
        "gallery" => galleryBrowser?.HandleGridKeyAsync(shortcut.Key, shortcut.Id, shortcut.Shift) ?? Task.CompletedTask,
        _ => Task.CompletedTask
    };

    private Task RetryCatalogLoadAsync()
    {
        Catalog.RetryLoad();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        disposed = true;
        Session.Changed -= RefreshWorkspace;
    }
}
