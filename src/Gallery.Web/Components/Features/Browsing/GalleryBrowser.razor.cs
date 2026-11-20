using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Features.Curation;
using Gallery.Web.Components.Features.Media.Removal;
using Gallery.Web.Components.Features.Media;
using Gallery.Web.Components.Features.Tags;
using Gallery.Web.Components.Features.Vision;
using Gallery.Web.Components.Workspace;
using Microsoft.AspNetCore.Components;

namespace Gallery.Web.Components.Features.Browsing;

public partial class GalleryBrowser
{
    [Inject] private IGalleryRecordEditor Gallery { get; set; } = default!;
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
    private VirtualGalleryGrid? virtualGrid;
    private sealed record QuickTagEdit(string[] Ids, string OriginalTags)
    {
        public bool AddOnly => Ids.Length > 1;
    }

    private QuickTagEdit? quickTags;
    private string quickTagError = "";
    internal bool QuickTagsOpen => quickTags is not null;
    private bool CanOpenQuickTags => Active && !State.TrashActive && !Catalog.IsPartial && !Activity.Busy
        && !Presentation.IsOpen && !OverlayOpen && State.LibrarySelection.Count > 0;

    protected override void OnParametersSet()
    {
        if (!Active || State.TrashActive) CloseQuickTags();
    }

    public async Task HandleGridKeyAsync(string key, string id, bool shift)
    {
        if (QuickTagsOpen) return;
        if (key == "t") OpenQuickTags();
        else if (key == "escape") State.ClearSelection();
        else if (Catalog.ImagesById.TryGetValue(id, out var image))
        {
            if (key == "x") State.SelectCard(id, shift);
            else if (GalleryKeyboardCuration.ForKey(Gallery, key, image) is { } update)
                await Curation.CurateAsync([id], update, $"{image.Name}: {GalleryKeyboardCuration.Message(key, image)}");
        }
        StateHasChanged();
    }

    private void OpenQuickTags()
    {
        if (QuickTagsOpen || !CanOpenQuickTags) return;
        var ids = State.LibrarySelection.ToArray();
        quickTagError = "";
        quickTags = new(ids, ids.Length == 1 ? Catalog.ImagesById[ids[0]].Tags : "");
    }

    private void CloseQuickTags() => quickTags = null;

    private async Task SaveQuickTagsAsync(string tags)
    {
        if (quickTags is not { } edit || Activity.Busy) return;
        quickTagError = "";
        await Curation.CurateAsync(edit.Ids, image => image with
        {
            Tags = edit.AddOnly
                ? TagSelection.AddMissing(Gallery, image.Tags, tags)
                : TagSelection.Apply(Gallery, image.Tags, edit.OriginalTags, tags)
        }, "");
        if (Activity.Error.Length == 0) CloseQuickTags();
        else quickTagError = Activity.Error;
    }

    private Task RetryCatalogLoadAsync() { Catalog.RetryLoad(); return Task.CompletedTask; }
    private async Task SetActiveToolAsync(string value)
    {
        await Navigation.SetActiveToolAsync(value);
        await Refreshed.InvokeAsync();
    }

    private async Task OpenViewerAsync(string? initialId)
    {
        if (State.InfiniteScroll)
            Presentation.OpenOrderedViewer(State.Filtered, initialId ?? virtualGrid?.PreferredViewerId);
        else Presentation.OpenViewer(State.PageImages, initialId);
        await Refreshed.InvokeAsync();
    }

    private async Task StartReviewAsync()
    {
        Presentation.StartReview(State.ReviewPool, State.ReviewGoal, State.ReviewRandomOrder);
        await Refreshed.InvokeAsync();
    }

    private Task DeleteAsync() => Removal.PrepareAsync(State.RemovalSelection);
    private Task RestoreAsync() => Curation.RestoreAsync(State.RemovalSelection.ToArray());

    private Task SetGroupingAsync(string value) => Commands.RunAsync("Filtering...", () => State.SetGroupingAsync(value));
    private Task ResetFilters() => Commands.RunAsync("Filtering...", State.ResetFiltersAsync);
    private Task ApplyFilterPanelAsync(GalleryFilter value) => Commands.RunAsync("Filtering...", () => State.ApplyFilterAsync(value));
    private Task SetSort(string? value) => Commands.RunAsync("Filtering...", () => State.SetSortAsync(value));
    private Task SetDensityAsync(string value) => Commands.RunAsync("Saving thumbnail size...", () => State.SetDensityAsync(value));
    private Task SetThumbnailScaleAsync(int value) => Commands.RunAsync("Saving thumbnail size...", () => State.SetThumbnailScaleAsync(value));
    private Task PreviousAsync() => Commands.RunAsync("Loading previews...", () => State.GoToPageAsync(State.Page - 1));
    private Task NextAsync() => Commands.RunAsync("Loading previews...", () => State.GoToPageAsync(State.Page + 1));
}
