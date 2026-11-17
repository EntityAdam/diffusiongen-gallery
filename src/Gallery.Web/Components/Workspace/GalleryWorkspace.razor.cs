using Microsoft.AspNetCore.Components;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Workspace;

public partial class GalleryWorkspace
{
    [Parameter] public GalleryActivity Activity { get; set; } = new();
    [Parameter] public EventCallback Locked { get; set; }
    [Parameter] public EventCallback Purged { get; set; }
    [Parameter] public EventCallback Panic { get; set; }
    [Parameter] public EventCallback ImportChanged { get; set; }
    [Parameter] public EventCallback ImportCompleted { get; set; }
    [Parameter] public CancellationToken Lifetime { get; set; }
    private Task PanicAsync() => Panic.InvokeAsync();
    private WorkspaceCommands commands = default!;
    private Task RunOperationAsync((string Status, Func<Task> Action) operation) =>
        RunAsync(operation.Status, operation.Action);
    private MediaDetailDialog? detailDialog;
    private string preview = "";
    private string videoUrl = "";
    private CollectionLocation collectionLocation = new();
    private bool collectionWorking;
    private bool WorkspaceBusy => Activity.Busy || collectionWorking || OrganizationHistory.IsBusy;
    private GalleryWorkspaceLayout? layout;
    private Task NavigateCollectionsAsync(CollectionLocation location) => WorkspaceBusy
        ? Task.CompletedTask : CollectionLocationChangedAsync(location);
    private Task CollectionLocationChangedAsync(CollectionLocation location)
    {
        collectionLocation = location;
        layout?.ResetNavigation();
        workflow = location.Unorganized ? "unorganized" : "collections";
        PublishRoute();
        return Task.CompletedTask;
    }
    private CollectionsWorkspace? collectionsWorkspace;
    private IReadOnlyList<ImageRecord>? detailViewerImages;
    private async Task OpenCollectionDetailsAsync((ImageRecord[] Images, string InitialId) request)
    {
        var image = request.Images.First(item => item.Id == request.InitialId);
        await OpenAsync(image);
        if (selected?.Id == image.Id) detailViewerImages = request.Images;
    }
    // Action feedback uses toasts across workflows; native dialogs retain inline validation.
    private bool UsesToasts => selected is null && removalPlan.Count == 0 && pendingSettingsAction is null;
    private LibraryJobStatus libraryJob = new(false, "", "", 0, false);
    private List<ImageRecord> viewerImages = [];
    private string viewerInitialId = "";
    private string viewerInitialPreview = "";
    private bool reviewMode;
    private DotNetObjectReference<GalleryWorkspace>? gridReference;
    private int autoLockMinutes = UiPreferences.DefaultAutoLockMinutes;
    private DuplicateCatalog duplicates = new();
    private DuplicateFinder? duplicateFinder;
    private bool DuplicateReviewOpen => workflow == "duplicates" && duplicateFinder?.ReviewOpen == true;
    private ImageRecord? ImageById(string id) => Catalog.ImagesById.GetValueOrDefault(id);
    private async Task TagsManagedAsync(TagCatalogChange change)
    {
        if (change.PreviousName is not null && change.Name is not null)
            Browser.Filter = Browser.Filter.WithRenamedTag(change.PreviousName, change.Name);
        if (galleryBrowser is not null) await galleryBrowser.UpdateSuggestionTagsAsync(change);
        await ReloadAsync();
    }
    private Task ViewTagAsync(string tag) => RequestSettingsActionAsync(async () =>
    {
        if (WorkspaceBusy || Catalog.IsPartial) return;
        Browser.FilterInputRevision++;
        Browser.Filter = new() { Tag = tag };
        Browser.BrowsingFilter = null;
        Browser.ActiveTool = "filters";
        Browser.ToolboxVisible = true;
        await SwitchWorkflowCoreAsync("gallery");
        await SaveToolboxStateAsync();
    });
    private ImageRecord? selected;
    private IngestionSession ingestion => Activity.Ingestion;
    private void RefreshWorkspace() { if (!disposed) StateHasChanged(); }
    private bool disposed;
    private bool initialized;
    private Task RunAsync(string status, Func<Task> action, bool allowDuringImport = false) =>
        Activity.RunAsync(status, async () =>
        {
            try
            {
                await Browser.PauseAsync();
                await action();
            }
            finally
            {
                if (Lifetime.IsCancellationRequested) Gallery.LockVault();
                else if (!disposed && Gallery.IsUnlocked) Browser.Resume();
            }
        }, Logger, allowDuringImport);

    protected override async Task OnInitializedAsync()
    {
        commands = new(Activity, EventCallback.Factory.Create<(string, Func<Task>)>(this, RunOperationAsync), EnsureNoLibraryJob);
        OrganizationHistory.Changed += HandleOrganizationHistoryChanged;
        await RunAsync("Opening gallery...", async () =>
        {
            try
            {
                var preferences = await Gallery.LoadPreferencesAsync();
                autoLockMinutes = preferences.AutoLockMinutes;
                await Browser.InitializeAsync(preferences);
                ingestion.LoadOptions(await Gallery.LoadIngestionOptionsAsync());
                await Catalog.OpenAsync(Browser.PageSize, Browser.TrashActive);
            }
            catch
            {
                await Locked.InvokeAsync();
                throw;
            }
        }, allowDuringImport: true);
        initialized = !disposed && Gallery.IsUnlocked;
    }

    private void HandleOrganizationHistoryChanged()
    {
        if (!disposed) _ = InvokeAsync(StateHasChanged);
    }

    private Task UndoOrganizationAsync() => WorkspaceBusy ? Task.CompletedTask : RunAsync("Restoring organization...", async () =>
    {
        EnsureNoLibraryJob();
        await OrganizationHistory.UndoAsync();
        await ReloadAsync();
        if (collectionLocation.CollectionId is { } collectionId && !Catalog.Collections.Collections.Any(item => item.Id == collectionId))
            collectionLocation = new();
        else if (collectionLocation.SetId is { } setId && !Catalog.Collections.Sets.Any(item => item.Id == setId))
            collectionLocation = new(collectionLocation.CollectionId);
        Activity.Message = "Organization restored.";
    });

    private Task SavePreferencesAsync(int? autoLockMinutesOverride = null) => Gallery.SavePreferencesAsync(new UiPreferences
    {
        GridDensity = Browser.Density, AutoLockMinutes = autoLockMinutesOverride ?? autoLockMinutes, ToolboxVisible = Browser.ToolboxVisible,
        ToolboxX = Browser.ToolboxX, ToolboxY = Browser.ToolboxY, ToolboxTool = Browser.ActiveTool
    });
    private Task SaveToolboxStateAsync() => Browser.SaveToolboxStateAsync();
    private void LibraryJobChanged(LibraryJobStatus status) => libraryJob = status;
    private Task<ImageRecord?> ApproveSuggestionAsync(OrganizationSuggestion suggestion) =>
        galleryBrowser?.ApproveSuggestionAsync(suggestion) ?? Task.FromResult<ImageRecord?>(null);
    private Task DismissSuggestionAsync(OrganizationSuggestion suggestion) =>
        galleryBrowser?.DismissSuggestionAsync(suggestion) ?? Task.CompletedTask;
    private void ClearSelection() => Browser.ClearSelection();
    private void ViewerImageChanged(ImageRecord updated)
    {
        Catalog.Update(updated);
        if (selected?.Id == updated.Id) selected = updated;
        if (detailViewerImages is not null)
            detailViewerImages = detailViewerImages.Select(image => image.Id == updated.Id ? updated : image).ToArray();
    }
    private Task CurateFromBrowserAsync((string Id, Func<ImageRecordEdit, ImageRecordEdit> Update, string Message) request) =>
        RunAsync("Saving...", () => CurateAsync([request.Id], request.Update, request.Message));
    private Task RateFromBrowserAsync((ImageRecord Image, int Rating) request) => RateImageAsync(request.Image, request.Rating);

    private async Task CurateAsync(IReadOnlyCollection<string> ids, Func<ImageRecordEdit, ImageRecordEdit> update, string done)
    {
        if (ids.Count == 0) return;
        List<ImageRecord> updated;
        try { updated = await Gallery.UpdateManyAsync(ids, update); }
        catch (AggregateException)
        {
            await ReloadAsync();
            throw;
        }
        foreach (var image in updated) ViewerImageChanged(image);
        Browser.Page = Math.Min(Browser.Page, Browser.PageCount - 1);
        await Browser.LoadMissingThumbnailsAsync();
        Activity.Message = done;
    }

    private Task MarkRemovalSelectionAsync(bool value) =>
        RunAsync("Updating deletion marks...", () => CurateAsync(Browser.RemovalSelection.ToList(),
            image => Gallery.SetDeletionMark(image, value), value ? "Marked for deletion" : "Deletion marks cleared"));
    [JSInvokable]
    public async Task HandleGridKey(string key, string id, bool shift)
    {
        if (key == "`")
        {
            if (!Gallery.IsUnlocked || selected is not null || removalPlan.Count > 0 || viewerImages.Count > 0 || DuplicateReviewOpen) return;
            if (Activity.Busy) return;
            if (shift) await ResetToolboxPositionAsync(); else await ToggleToolboxAsync();
            StateHasChanged();
            return;
        }
        if (!Gallery.IsUnlocked || Activity.Busy || selected is not null || removalPlan.Count > 0 || viewerImages.Count > 0 || DuplicateReviewOpen) return;
        if (workflow is "collections" or "unorganized" && collectionsWorkspace is not null)
        {
            await collectionsWorkspace.HandleGridKeyAsync(key, id, shift);
            StateHasChanged();
            return;
        }
        if (workflow == "gallery" && galleryBrowser is not null)
            await galleryBrowser.HandleGridKeyAsync(key, id, shift);
        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        gridReference = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("galleryGrid.attach", gridReference);
    }

    private void EnsureNoLibraryJob()
    {
        if (libraryJob.IsRunning) throw new InvalidOperationException("Wait for the running LLM job to finish, or stop it after the current media item, before changing files or gallery settings.");
    }

    private async Task UpdateAutoLockMinutesAsync(string? value)
    {
        if (Activity.Busy) return;
        if (int.TryParse(value, out var minutes))
        {
            minutes = Math.Clamp(minutes, 0, 1440);
            await RunAsync("Saving auto-lock timer...", () => SavePreferencesAsync(minutes));
            if (Activity.Error.Length > 0)
            {
                Activity.Error = "Auto-lock timer could not be saved. The previous timer is still active.";
                return;
            }
            autoLockMinutes = minutes;
            Gallery.ConfigureAutoLock(autoLockMinutes);
            try { await JS.InvokeVoidAsync("gallerySecurity.configureAutoLock", autoLockMinutes); }
            catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        disposed = true;
        OrganizationHistory.Changed -= HandleOrganizationHistoryChanged;
        catalog?.Dispose();
        browser?.Dispose();
        previousCollections = CollectionCatalog.Empty;
        _ = ReleaseVideoAsync();
        if (gridReference is not null) _ = DetachGridAsync(gridReference);
    }

    private async Task DetachGridAsync(DotNetObjectReference<GalleryWorkspace> reference)
    {
        try { await JS.InvokeVoidAsync("galleryGrid.detach"); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
        finally { reference.Dispose(); }
    }
}
