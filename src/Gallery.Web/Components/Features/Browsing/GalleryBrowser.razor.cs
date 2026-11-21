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
    private GalleryResults? results;
    private ImageRecord[]? taggingImages;
    private string tagError = "";
    internal bool TaggingOpen => taggingImages is not null;
    private bool CanEditTags => !TaggingOpen && Active && !State.TrashActive && !Catalog.IsPartial && !Activity.Busy
        && !Presentation.IsOpen && !OverlayOpen && State.LibrarySelection.Count > 0;

    protected override void OnParametersSet()
    {
        if (!Active || State.TrashActive) CloseTagging();
    }

    public async Task HandleGridKeyAsync(string key, string id, bool shift)
    {
        if (TaggingOpen) return;
        if (key == "t") OpenTagging();
        else if (key == "escape") State.ClearSelection();
        else if (Catalog.ImagesById.TryGetValue(id, out var image))
        {
            if (key == "x") State.SelectCard(id, shift);
            else if (GalleryKeyboardCuration.ForKey(Gallery, key, image) is { } update)
                await Curation.CurateAsync([id], update, $"{image.Name}: {GalleryKeyboardCuration.Message(key, image)}");
        }
        StateHasChanged();
    }

    private void OpenTagging()
    {
        if (!CanEditTags) return;
        tagError = "";
        taggingImages = State.LibrarySelection.Select(id => Catalog.ImagesById[id]).ToArray();
    }

    private void CloseTagging() => taggingImages = null;

    private async Task SaveTagsAsync(TagEdit edit)
    {
        tagError = "";
        await Curation.EditTagsAsync(edit);
        tagError = Activity.Error;
    }

    private async Task SaveDialogTagsAsync(TagEdit edit)
    {
        if (!TaggingOpen || Activity.Busy) return;
        await SaveTagsAsync(edit);
        if (tagError.Length == 0) CloseTagging();
    }

    private async Task OpenViewerAsync(string? initialId)
    {
        if (State.InfiniteScroll)
            Presentation.OpenOrderedViewer(State.Filtered, initialId ?? results?.PreferredViewerId);
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

}
