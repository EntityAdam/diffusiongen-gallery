using Microsoft.AspNetCore.Components;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using ShadCn.Blazor.Components;

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
    private CollectionCatalog collectionCatalog = CollectionCatalog.Empty;
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
    private LibraryWorkbench? workbench;
    private GalleryToolbox? toolbox;
    private string activeTool = "";
    private bool toolboxVisible = true;
    private double? toolboxX, toolboxY;
    private IReadOnlyDictionary<string, OrganizationSuggestion> aiSuggestions = new Dictionary<string, OrganizationSuggestion>();
    private bool TrashActive => activeTool == "trash";
    // Action feedback uses toasts across workflows; native dialogs retain inline validation.
    private bool UsesToasts => selected is null && removalPlan.Count == 0 && pendingSettingsAction is null;
    private LibraryJobStatus libraryJob = new(false, "", "", 0, false);
    private GalleryFilter? browsingFilter;
    private bool ShowsGallery => workflow == "gallery";
    private readonly HashSet<string> librarySelection = [];
    private List<ImageRecord> viewerImages = [];
    private string viewerInitialId = "";
    private string viewerInitialPreview = "";
    private bool reviewMode;
    private bool reviewRandomOrder;
    private int reviewGoal = 10;
    private IEnumerable<ImageRecord> ReviewPool => Filtered.Where(image => !image.IsReviewed && !image.IsMarkedForDeletion);
    private int ReviewPoolCount => ReviewPool.Count();
    private int ReviewGoal
    {
        get => Math.Clamp(reviewGoal, ReviewPoolCount > 0 ? 1 : 0, ReviewPoolCount);
        set => reviewGoal = Math.Clamp(value, ReviewPoolCount > 0 ? 1 : 0, ReviewPoolCount);
    }
    private int page;
    private int PageSize => density == "compact" ? 49 : 12;
    private string density = UiPreferences.DetailDensity;
    private string? selectionAnchor;
    private DotNetObjectReference<GalleryWorkspace>? gridReference;
    private int autoLockMinutes = UiPreferences.DefaultAutoLockMinutes;
    private DuplicateCatalog duplicates = new();
    private DuplicateFinder? duplicateFinder;
    private bool DuplicateReviewOpen => workflow == "duplicates" && duplicateFinder?.ReviewOpen == true;
    private ImageRecord? ImageById(string id) => imagesById.GetValueOrDefault(id);
    private Dictionary<string, ImageRecord> imagesById = [];
    private FilterDrawer? filterDrawer;
    private IReadOnlyList<SavedView> quickFilters = [];
    private GalleryFilter filter = new();
    private long filterInputRevision;
    private VisionSettings settings = new();
    private IReadOnlyList<string> catalogTags = [];
    private IEnumerable<string> AllTags => catalogTags.Concat(images.SelectMany(image => image.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
    private async Task TagsManagedAsync(TagCatalogChange change)
    {
        if (change.PreviousName is not null && change.Name is not null)
            filter = filter.WithRenamedTag(change.PreviousName, change.Name);
        if (workbench is not null) await workbench.UpdateSuggestionTagsAsync(change);
        await ReloadAsync();
    }
    private Task ViewTagAsync(string tag) => RequestSettingsActionAsync(async () =>
    {
        if (WorkspaceBusy || catalogPartial) return;
        filterInputRevision++;
        filter = new() { Tag = tag };
        browsingFilter = null;
        activeTool = "filters";
        toolboxVisible = true;
        await SwitchWorkflowCoreAsync("gallery");
        await SaveToolboxStateAsync();
    });
    private List<ImageRecord> images = [];
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
                await Thumbnails.PauseAsync();
                await action();
            }
            finally
            {
                if (Lifetime.IsCancellationRequested) Gallery.LockVault();
                else if (!disposed && Gallery.IsUnlocked) Thumbnails.Resume(PageImages);
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
                ApplyPreferences(await Gallery.LoadPreferencesAsync());
                await OpenGalleryAsync();
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
        if (collectionLocation.CollectionId is { } collectionId && !collectionCatalog.Collections.Any(item => item.Id == collectionId))
            collectionLocation = new();
        else if (collectionLocation.SetId is { } setId && !collectionCatalog.Sets.Any(item => item.Id == setId))
            collectionLocation = new(collectionLocation.CollectionId);
        Activity.Message = "Organization restored.";
    });

    internal async Task ReloadAsync()
    {
        CancelCatalogLoad();
        var generation = catalogGeneration;
        if (catalogPartial)
        {
            var records = await Gallery.ReloadRecordsAsync(images.Select(image => image.Id));
            if (!IsCatalogGenerationCurrent(generation)) return;
            images = records;
            await ApplyLoadedRecordsAsync();
            StartCatalogLoad();
        }
        else
        {
            var snapshot = await Gallery.LoadCatalogAsync();
            if (!IsCatalogGenerationCurrent(generation)) return;
            images = snapshot.Records;
            catalogPartial = !snapshot.IsCurrent;
            await ApplyLoadedRecordsAsync();
            if (catalogPartial) StartCatalogLoad();
        }
    }

    private void ApplyPreferences(UiPreferences preferences)
    {
        density = preferences.GridDensity;
        autoLockMinutes = preferences.AutoLockMinutes;
        toolboxVisible = preferences.ToolboxVisible;
        (toolboxX, toolboxY) = (preferences.ToolboxX, preferences.ToolboxY);
        activeTool = preferences.ToolboxTool;
        browsingFilter = null;
        filterInputRevision++;
        filter = TrashActive ? new() { Deletion = "marked" } : new();
        if (TrashActive) browsingFilter = new();
    }
    private Task SavePreferencesAsync(int? autoLockMinutesOverride = null) => Gallery.SavePreferencesAsync(new UiPreferences
    {
        GridDensity = density, AutoLockMinutes = autoLockMinutesOverride ?? autoLockMinutes, ToolboxVisible = toolboxVisible,
        ToolboxX = toolboxX, ToolboxY = toolboxY, ToolboxTool = activeTool
    });
    // Toolbox state is a convenience; failing to persist it must not interrupt browsing.
    private async Task SaveToolboxStateAsync()
    {
        if (!Gallery.IsUnlocked) return;
        try { await SavePreferencesAsync(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            Logger.LogWarning(exception, "Toolbox preferences were not saved");
        }
    }
    private void LibraryJobChanged(LibraryJobStatus status) => libraryJob = status;
    private async Task SuggestionsChanged(IReadOnlyList<OrganizationSuggestion> suggestions)
    {
        aiSuggestions = suggestions.ToDictionary(suggestion => suggestion.Id);
        if (filter.Analysis != GalleryFilter.AwaitingApproval) return;
        browsingSource = null;
        page = Math.Min(page, PageCount - 1);
        await LoadThumbnailsAsync();
    }
    private Task<ImageRecord?> ApproveSuggestionAsync(OrganizationSuggestion suggestion) =>
        workbench?.ApproveSuggestionAsync(suggestion) ?? Task.FromResult<ImageRecord?>(null);
    private Task DismissSuggestionAsync(OrganizationSuggestion suggestion) =>
        workbench?.DismissSuggestionAsync(suggestion) ?? Task.CompletedTask;
    private void ClearSelection() { librarySelection.Clear(); removalSelection.Clear(); selectionAnchor = null; }
    private void ViewerImageChanged(ImageRecord updated)
    {
        catalogTags = AllTags.Concat(updated.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        imagesById[updated.Id] = updated;
        images = images.Select(image => image.Id == updated.Id ? updated : image).ToList();
        if (selected?.Id == updated.Id) selected = updated;
        if (detailViewerImages is not null)
            detailViewerImages = detailViewerImages.Select(image => image.Id == updated.Id ? updated : image).ToArray();
        browsingSource = null;
        if (catalogPartial) StartCatalogLoad();
    }
    private void SelectCard(string id, bool range)
    {
        var selection = TrashActive ? removalSelection : librarySelection;
        var ordered = Filtered.Select(image => image.Id).ToList();
        var anchor = selectionAnchor is null ? -1 : ordered.IndexOf(selectionAnchor);
        var target = ordered.IndexOf(id);
        if (range && anchor >= 0 && target >= 0)
        {
            var (from, to) = anchor <= target ? (anchor, target) : (target, anchor);
            selection.UnionWith(ordered.Skip(from).Take(to - from + 1));
        }
        else if (!selection.Remove(id)) selection.Add(id);
        selectionAnchor = id;
    }

    private Task SetDensityAsync(string value) => RunAsync("Saving thumbnail size...", async () =>
    {
        if (density == value) return;
        var first = page * PageSize;
        density = value;
        if (catalogPartial)
        {
            CancelCatalogLoad();
            var generation = catalogGeneration;
            var initial = await Gallery.LoadInitialPageAsync(PageSize, TrashActive);
            if (!IsCatalogGenerationCurrent(generation)) return;
            catalogPartial = !initial.IsComplete || !initial.IsCurrent;
            images = initial.Records;
            imagesById = images.ToDictionary(image => image.Id);
            browsingSource = null;
            if (catalogPartial) StartCatalogLoad();
        }
        page = Math.Min(first / PageSize, PageCount - 1);
        await LoadThumbnailsAsync();
        await SavePreferencesAsync();
    });

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
        page = Math.Min(page, PageCount - 1);
        await LoadMissingThumbnailsAsync();
        Activity.Message = done;
    }

    private Task MarkRemovalSelectionAsync(bool value) =>
        RunAsync("Updating deletion marks...", () => CurateAsync(removalSelection.ToList(),
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
        if (!ShowsGallery) return;
        if (key == "escape") { ClearSelection(); selectionAnchor = null; StateHasChanged(); return; }
        if (!imagesById.TryGetValue(id, out var image)) return;
        if (key == "x")
        {
            SelectCard(id, shift);
            StateHasChanged();
            return;
        }
        var update = GalleryKeyboardCuration.ForKey(Gallery, key, image);
        if (update is null) return;
        var done = GalleryKeyboardCuration.Message(key, image);
        await RunAsync("Saving...", () => CurateAsync([id], update, $"{image.Name}: {done}"));
        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        gridReference = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("galleryGrid.attach", gridReference);
    }
    private Task PreviousAsync() => RunAsync("Loading previews...", async () => { page--; await LoadThumbnailsAsync(); });
    private Task NextAsync() => RunAsync("Loading previews...", async () => { page++; await LoadThumbnailsAsync(); });

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
        CancelCatalogLoad();
        thumbnailCache?.Clear();
        _ = ReleaseVideoAsync();
        images.Clear();
        imagesById.Clear();
        browsingProjection = GalleryBrowsingProjection.Empty;
        browsingSource = null;
        if (gridReference is not null) _ = DetachGridAsync(gridReference);
    }

    private async Task DetachGridAsync(DotNetObjectReference<GalleryWorkspace> reference)
    {
        try { await JS.InvokeVoidAsync("galleryGrid.detach"); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
        finally { reference.Dispose(); }
    }
}
