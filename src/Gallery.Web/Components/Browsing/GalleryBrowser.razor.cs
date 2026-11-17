using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.AspNetCore.Components;
using Gallery.Web.Components.Media;
using Gallery.Web.Components.Workspace;
using Gallery.Web.Components.Analysis;

namespace Gallery.Web.Components.Browsing;

public partial class GalleryBrowser
{
    [Inject] private GalleryService Gallery { get; set; } = default!;
    [CascadingParameter] public WorkspaceCommands Commands { get; set; } = default!;
    [Parameter, EditorRequired] public GalleryBrowserState State { get; set; } = default!;
    [Parameter, EditorRequired] public WorkspaceCatalog Catalog { get; set; } = default!;
    [CascadingParameter] public WorkspaceNavigationState Navigation { get; set; } = default!;
    [Parameter, EditorRequired] public MediaPresentationState Presentation { get; set; } = default!;
    [Parameter, EditorRequired] public MediaCurationCommands Curation { get; set; } = default!;
    [Parameter, EditorRequired] public RemovalWorkflowState Removal { get; set; } = default!;
    [Parameter, EditorRequired] public LibraryAnalysis Analysis { get; set; } = default!;
    [Parameter] public bool Active { get; set; }
    [Parameter] public EventCallback Refreshed { get; set; }

    private GalleryActivity Activity => Commands.Activity;
    private bool OverlayOpen => Presentation.DetailsOpen || Removal.IsOpen;
    private GalleryToolbox? toolbox;

    public async Task HandleGridKeyAsync(string key, string id, bool shift)
    {
        if (key == "escape") State.ClearSelection();
        else if (Catalog.ImagesById.TryGetValue(id, out var image))
        {
            if (key == "x") State.SelectCard(id, shift);
            else if (GalleryKeyboardCuration.ForKey(Gallery, key, image) is { } update)
                await Curation.CurateAsync([id], update, $"{image.Name}: {GalleryKeyboardCuration.Message(key, image)}");
        }
        StateHasChanged();
    }

    private Task RetryCatalogLoadAsync() { Catalog.RetryLoad(); return Task.CompletedTask; }
    private async Task SetActiveToolAsync(string value)
    {
        await Navigation.SetActiveToolAsync(value);
        await Refreshed.InvokeAsync();
    }

    private Task ToggleToolboxAsync() => Navigation.ToggleToolboxAsync();

    private async Task OpenViewerAsync(string? initialId)
    {
        Presentation.OpenViewer(State.PageImages, initialId);
        await Refreshed.InvokeAsync();
    }

    private async Task StartReviewAsync()
    {
        Presentation.StartReview(State.ReviewPool, State.ReviewGoal, State.ReviewRandomOrder);
        await Refreshed.InvokeAsync();
    }

    private Task DeleteAsync() => Removal.PrepareAsync(State.RemovalSelection);
    private Task RestoreAsync() => Curation.RestoreAsync(State.RemovalSelection.ToArray());

    private Task FilterChangedAsync() => Commands.RunAsync("Filtering...", State.FilterChangedAsync);
    private Task ResetFilters() => Commands.RunAsync("Filtering...", State.ResetFiltersAsync);
    private Task ApplyFilterPanelAsync(GalleryFilter value) => Commands.RunAsync("Filtering...", () => State.ApplyFilterAsync(value));
    private Task SetSort(string? value) => Commands.RunAsync("Filtering...", () => State.SetSortAsync(value));
    private Task SetDensityAsync(string value) => Commands.RunAsync("Saving thumbnail size...", () => State.SetDensityAsync(value));
    private Task PreviousAsync() => Commands.RunAsync("Loading previews...", async () => { State.Page--; await State.LoadThumbnailsAsync(); });
    private Task NextAsync() => Commands.RunAsync("Loading previews...", async () => { State.Page++; await State.LoadThumbnailsAsync(); });
    private async Task ToolboxMovedAsync((double? X, double? Y) position)
    {
        if ((State.ToolboxX, State.ToolboxY) == position) return;
        (State.ToolboxX, State.ToolboxY) = position;
        await State.SaveToolboxStateAsync();
    }

    private async Task SuggestionsChangedAsync(IReadOnlyList<OrganizationSuggestion> suggestions)
    {
        await State.SuggestionsChangedAsync(suggestions);
        await Refreshed.InvokeAsync();
    }
    internal async Task ResetToolboxPositionAsync()
    {
        State.ToolboxVisible = true;
        (State.ToolboxX, State.ToolboxY) = (null, null);
        if (toolbox is not null) await toolbox.ResetPositionAsync();
        await State.SaveToolboxStateAsync();
    }
}
