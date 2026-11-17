using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Gallery.Web.Components.Workspace;

public partial class GalleryBrowser
{
    [Inject] private GalleryService Gallery { get; set; } = default!;
    [CascadingParameter] public WorkspaceCommands Commands { get; set; } = default!;
    [Parameter, EditorRequired] public GalleryBrowserState State { get; set; } = default!;
    [Parameter, EditorRequired] public WorkspaceCatalog Catalog { get; set; } = default!;
    [Parameter, EditorRequired] public GalleryActivity Activity { get; set; } = default!;
    [Parameter] public bool Active { get; set; }
    [Parameter] public bool OverlayOpen { get; set; }
    [Parameter] public EventCallback<ImageRecord> Open { get; set; }
    [Parameter] public EventCallback<(ImageRecord Image, int Rating)> Rate { get; set; }
    [Parameter] public EventCallback<ImageRecord> Favorite { get; set; }
    [Parameter] public EventCallback<(string Id, Func<ImageRecordEdit, ImageRecordEdit> Update, string Message)> Curate { get; set; }
    [Parameter] public EventCallback<ImageRecord> Mark { get; set; }
    [Parameter] public EventCallback<string> ShowPrompt { get; set; }
    [Parameter] public EventCallback<string?> Fullscreen { get; set; }
    [Parameter] public EventCallback StartReview { get; set; }
    [Parameter] public EventCallback<string> SetActiveTool { get; set; }
    [Parameter] public EventCallback ToggleToolbox { get; set; }
    [Parameter] public EventCallback ResetToolboxPosition { get; set; }
    [Parameter] public EventCallback Reload { get; set; }
    [Parameter] public EventCallback<LibraryJobStatus> JobChanged { get; set; }
    [Parameter] public EventCallback Refreshed { get; set; }
    [Parameter] public EventCallback PreferencesChanged { get; set; }
    [Parameter] public EventCallback Delete { get; set; }
    [Parameter] public EventCallback Restore { get; set; }
    [Parameter] public IReadOnlyList<RemovalResult> RemovalResults { get; set; } = [];

    private GalleryToolbox? toolbox;
    private LibraryWorkbench? workbench;
    private FilterDrawer? filterDrawer;

    public async Task HandleGridKeyAsync(string key, string id, bool shift)
    {
        if (key == "escape") State.ClearSelection();
        else if (Catalog.ImagesById.TryGetValue(id, out var image))
        {
            if (key == "x") State.SelectCard(id, shift);
            else if (GalleryKeyboardCuration.ForKey(Gallery, key, image) is { } update)
                await Curate.InvokeAsync((id, update, $"{image.Name}: {GalleryKeyboardCuration.Message(key, image)}"));
        }
        StateHasChanged();
    }

    private Task RetryCatalogLoadAsync() { Catalog.RetryLoad(); return Task.CompletedTask; }
    private Task ShowPromptAsync(string prompt) => ShowPrompt.InvokeAsync(prompt);
    private Task SetActiveToolAsync(string value) => SetActiveTool.InvokeAsync(value);
    private Task ToggleToolboxAsync() => ToggleToolbox.InvokeAsync();
    private Task RequestToolboxResetAsync() => ResetToolboxPosition.InvokeAsync();
    private Task FilterChangedAsync() => Commands.RunAsync("Filtering...", State.FilterChangedAsync);
    private Task ResetFilters() => Commands.RunAsync("Filtering...", State.ResetFiltersAsync);
    private Task ApplyFilterPanelAsync(GalleryFilter value) => Commands.RunAsync("Filtering...", () => State.ApplyFilterAsync(value));
    private Task SetSort(string? value) => Commands.RunAsync("Filtering...", () => State.SetSortAsync(value));
    private Task SetDensityAsync(string value) => Commands.RunAsync("Saving thumbnail size...", () => State.SetDensityAsync(value));
    private Task PreviousAsync() => Commands.RunAsync("Loading previews...", async () => { State.Page--; await State.LoadThumbnailsAsync(); });
    private Task NextAsync() => Commands.RunAsync("Loading previews...", async () => { State.Page++; await State.LoadThumbnailsAsync(); });
    private async Task ToolboxMovedAsync((double? X, double? Y) position)
    {
        (State.ToolboxX, State.ToolboxY) = position;
        await PreferencesChanged.InvokeAsync();
    }

    private async Task SuggestionsChangedAsync(IReadOnlyList<OrganizationSuggestion> suggestions)
    {
        await State.SuggestionsChangedAsync(suggestions);
        await Refreshed.InvokeAsync();
    }
    public Task ResetToolboxPositionAsync() => toolbox?.ResetPositionAsync() ?? Task.CompletedTask;
    public Task UpdateSuggestionTagsAsync(TagCatalogChange change) => workbench?.UpdateSuggestionTagsAsync(change) ?? Task.CompletedTask;
    public Task AnalyzeDetailAsync(ImageRecord image) => workbench?.AnalyzeDetailAsync(image) ?? Task.CompletedTask;
    public Task<ImageRecord?> ApproveSuggestionAsync(OrganizationSuggestion suggestion) =>
        workbench?.ApproveSuggestionAsync(suggestion) ?? Task.FromResult<ImageRecord?>(null);
    public Task DismissSuggestionAsync(OrganizationSuggestion suggestion) => workbench?.DismissSuggestionAsync(suggestion) ?? Task.CompletedTask;
}
