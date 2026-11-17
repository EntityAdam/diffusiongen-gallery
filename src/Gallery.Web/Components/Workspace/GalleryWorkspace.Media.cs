using Gallery.Core.Models;

namespace Gallery.Web.Components.Workspace;

public partial class GalleryWorkspace
{
    private MediaPresentationState? presentation;
    private MediaPresentationHost? mediaHost;
    private RemovalWorkflowState? removal;
    private MediaCurationCommands? curation;
    private MediaPresentationState Presentation => presentation ??= new(Gallery, JS, commands);
    private RemovalWorkflowState Removal => removal ??= new(Gallery, commands, Logger, async () =>
    {
        Presentation.CloseDetail();
        await ReloadAsync();
    });
    private MediaCurationCommands Curation => curation ??= new(Gallery, ReloadAsync);

    private Task OpenAsync(ImageRecord image) => Presentation.OpenAsync(image);
    private void OpenViewer(string? initialId = null) => Presentation.OpenViewer(Browser.PageImages, initialId);
    private void StartReview() => Presentation.StartReview(Browser.ReviewPool, Browser.ReviewGoal, Browser.ReviewRandomOrder);
    private Task PrepareBulkRemovalAsync() => Removal.PrepareAsync(Browser.RemovalSelection);
    private Task PrepareRemovalAsync(IReadOnlyCollection<string> ids) => Removal.PrepareAsync(ids);

    private Task RateFromBrowserAsync((ImageRecord Image, int Rating) request) => RunAsync("Saving rating...", async () =>
        await ApplyCurationAsync([await Curation.RateAsync(request.Image, request.Rating)], reloadThumbnails: true));
    private Task FavoriteAsync(ImageRecord image) => RunAsync("Saving favorite...", async () =>
        await ApplyCurationAsync([await Curation.FavoriteAsync(image)], reloadThumbnails: true));
    private Task MarkAsync(ImageRecord image) => RunAsync("Saving deletion mark...", async () =>
        await ApplyCurationAsync([await Curation.MarkAsync(image)], reloadThumbnails: true));
    private Task CurateFromBrowserAsync((string Id, Func<ImageRecordEdit, ImageRecordEdit> Update, string Message) request) =>
        RunAsync("Saving...", () => CurateAsync([request.Id], request.Update, request.Message));
    private Task CurateAsync(IReadOnlyCollection<string> ids, Func<ImageRecordEdit, ImageRecordEdit> update, string done) =>
        ApplyBulkCurationAsync(() => Curation.UpdateManyAsync(ids, update), done);
    private Task RestoreRemovalSelectionAsync() => RunAsync("Updating deletion marks...", () =>
        ApplyBulkCurationAsync(() => Curation.MarkManyAsync(Browser.RemovalSelection.ToArray(), false), "Deletion marks cleared"));
    private async Task ApplyBulkCurationAsync(Func<Task<IReadOnlyList<ImageRecord>>> update, string done)
    {
        var updated = await update();
        if (updated.Count == 0 || disposed) return;
        await ApplyCurationAsync(updated, reloadThumbnails: false);
        Activity.Message = done;
    }
    private async Task ApplyCurationAsync(IReadOnlyList<ImageRecord> updated, bool reloadThumbnails)
    {
        if (disposed) return;
        foreach (var image in updated) ViewerImageChanged(image);
        if (reloadThumbnails) await Browser.LoadThumbnailsAsync();
        else
        {
            Browser.Page = Math.Min(Browser.Page, Browser.PageCount - 1);
            await Browser.LoadMissingThumbnailsAsync();
        }
    }

    private async Task ShowPromptAsync(string prompt)
    {
        if (Catalog.IsPartial) return;
        Presentation.CloseDetail();
        if (workflow != "gallery") await SwitchWorkflowAsync("gallery");
        Browser.Filter = Browser.Filter with { Prompt = prompt };
        await FilterChangedAsync();
    }
    private Task AnalyzeAsync(ImageRecord image) => RunAsync("Analyzing with your local vision model...", async () =>
    {
        EnsureNoLibraryJob();
        if (galleryBrowser is null) return;
        await galleryBrowser.AnalyzeDetailAsync(image);
        if (Gallery.IsUnlocked) Activity.Message = "Review available suggestions in the AI analysis drawer. Unfinished analysis can be resumed there.";
    });
}
