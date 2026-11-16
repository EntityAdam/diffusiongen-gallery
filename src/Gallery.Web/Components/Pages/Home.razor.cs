using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using ShadCn.Blazor.Components;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    private string mediaDirectory = "";
    private int accessRevision;

    private bool startWithCreate;
    private Task RunAccessOperationAsync((string Status, Func<Task> Action) operation) =>
        RunAsync(operation.Status, operation.Action);
    private void ClearAccessFeedback() => error = message = "";
    private void GalleryCreated(string phrase) => recoveryPhrase = phrase;
    private void GalleryRestored()
    {
        Lock();
        message = "Backup restored. Sign in to the restored gallery.";
    }

    private string recoveryPhrase = "", error = "", message = "", progress = "";
    private string newPassphrase = "", newConfirm = "";
    private string preview = "", editDescription = "", editUserDescription = "", destination = "", rename = "";
    private string videoUrl = "";
    private bool busy;
    private CollectionCatalog collectionCatalog = CollectionCatalog.Empty;
    private CollectionLocation collectionLocation = new();
    private bool collectionWorking;
    private bool WorkspaceBusy => busy || collectionWorking || OrganizationHistory.IsBusy;
    private bool collectionNavigationOpen;
    private bool? focusCollectionNavigation;
    private void ToggleCollectionNavigation() { collectionNavigationOpen = !collectionNavigationOpen;focusCollectionNavigation = collectionNavigationOpen; }
    private void CloseCollectionNavigation() { collectionNavigationOpen = false;focusCollectionNavigation = false; }
    private void HandleCollectionNavigationKey(KeyboardEventArgs args) { if (args.Key == "Escape" && collectionNavigationOpen) CloseCollectionNavigation(); }
    private Task NavigateCollectionsAsync(CollectionLocation location) => WorkspaceBusy
        ? Task.CompletedTask : CollectionLocationChangedAsync(location);
    private Task CollectionLocationChangedAsync(CollectionLocation location)
    {
        collectionLocation = location;
        collectionNavigationOpen = false;
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
    private bool sidebarCollapsed;
    private LibraryWorkbench? workbench;
    private GalleryToolbox? toolbox;
    private string activeTool = "";
    private bool toolboxVisible = true;
    private double? toolboxX, toolboxY;
    private IReadOnlyDictionary<string, OrganizationSuggestion> aiSuggestions = new Dictionary<string, OrganizationSuggestion>();
    private Guid? progressToast;
    private bool progressToastScheduled;
    private bool TrashActive => activeTool == "trash";
    private bool ToolboxShown => toolboxVisible && workflow == "gallery";
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
    private DotNetObjectReference<Home>? gridReference;
    private DotNetObjectReference<Home>? securityReference;
    private int autoLockMinutes = UiPreferences.DefaultAutoLockMinutes;
    private IEnumerable<string> AllCollections => images.SelectMany(image => image.Collections).Distinct(StringComparer.OrdinalIgnoreCase).Order();
    private ImageRecord? ImageById(string id) => imagesById.GetValueOrDefault(id);
    private Dictionary<string, ImageRecord> imagesById = [];
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
    private bool disposed;
    private async Task RunAsync(string status, Func<Task> action, bool allowDuringImport = false)
    {
        var nested = allowDuringImport && busy && importRunning;
        if (busy && !nested) return;
        var outerProgress = progress;
        busy = true;
        progress = status;
        error = message = "";
        try
        {
            if (!nested) await PauseThumbnailLoadAsync();
            await action();
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            Logger.LogWarning(exception, "Gallery authentication or integrity check failed");
            error = "Incorrect credentials, or encrypted data failed its integrity check. The operation was stopped.";
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Gallery operation failed");
            error = exception is IOException or InvalidOperationException or System.Text.Json.JsonException
                or ImportFolderException
                or HttpRequestException or SixLabors.ImageSharp.UnknownImageFormatException
                or SixLabors.ImageSharp.InvalidImageContentException ? exception.Message
                : "The operation failed. Check the application console for details.";
        }
        finally
        {
            busy = nested;
            progress = nested ? outerProgress : "";
            if (disposed) Gallery.LockVault();
            else if (!nested) ResumeThumbnailLoad();
        }
    }

    protected override void OnInitialized()
    {
        OrganizationHistory.Changed += HandleOrganizationHistoryChanged;
        Gallery.OnAutoLocked += HandleAutoLocked;
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
        message = "Organization restored.";
    });

    private void HandleAutoLocked()
    {
        _ = InvokeAsync(() =>
        {
            if (disposed) return;
            Lock();
            message = "Gallery locked after inactivity. Its checkout was released.";
            StateHasChanged();
        });
    }

    private async Task UnlockAsync((string Credential, bool UseRecovery) input)
    {
        await RunAsync("Unlocking gallery...", async () =>
        {
            try
            {
                if (Gallery.VaultExists)
                {
                    if (input.UseRecovery) await Gallery.UnlockWithRecoveryPhraseAsync(input.Credential);
                    else await Gallery.UnlockWithPassphraseAsync(input.Credential);
                    startWithCreate = false;
                    workflow = importRunning ? "ingest" : "gallery";
                    settingsTab = "overview";
                    ApplyPreferences(await Gallery.LoadPreferencesAsync());
                    Gallery.ConfigureAutoLock(autoLockMinutes);
                    try { await JS.InvokeVoidAsync("gallerySecurity.configureAutoLock", autoLockMinutes); }
                    catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
                    await OpenGalleryAsync();
                }
                else throw new InvalidOperationException("No gallery is selected. Choose Switch gallery to open or create one.");
            }
            catch
            {
                Lock();
                throw;
            }
        }, allowDuringImport: true);
        if (importRunning && Gallery.IsUnlocked) PublishRoute();
        else await ApplyRouteAsync();
    }

    private async Task FinishSetupAsync()
    {
        await RunAsync("Opening gallery...", async () =>
        {
            startWithCreate = false;
            ApplyPreferences(await Gallery.LoadPreferencesAsync());
            Gallery.ConfigureAutoLock(autoLockMinutes);
            try { await JS.InvokeVoidAsync("gallerySecurity.configureAutoLock", autoLockMinutes); }
            catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
            await OpenGalleryAsync();
            recoveryPhrase = "";
        });
        await ApplyRouteAsync();
    }

    private async Task ReloadAsync()
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

    // Thumbnails decrypt in the background so the grid renders immediately with placeholders.
    // Existing previews stay visible while they are refreshed, which avoids flicker after edits.
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
    private sealed class InlineProgress<T>(Func<T, Task> report) : IProgress<T>
    {
        public void Report(T value) => _ = report(value);
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
        message = done;
    }

    private Task MarkRemovalSelectionAsync(bool value) =>
        RunAsync("Updating deletion marks...", () => CurateAsync(removalSelection.ToList(),
            image => Gallery.SetDeletionMark(image, value), value ? "Marked for deletion" : "Deletion marks cleared"));
    [JSInvokable]
    public async Task HandleGridKey(string key, string id, bool shift)
    {
        if (key == "`")
        {
            if (!Gallery.IsUnlocked || selected is not null || removalPlan.Count > 0 || viewerImages.Count > 0 || reviewGroups.Count > 0) return;
            if (busy) return;
            if (shift) await ResetToolboxPositionAsync(); else await ToggleToolboxAsync();
            StateHasChanged();
            return;
        }
        if (!Gallery.IsUnlocked || busy || selected is not null || removalPlan.Count > 0 || viewerImages.Count > 0 || reviewGroups.Count > 0) return;
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
        PublishToasts();
        if (focusCollectionNavigation is { } open)
        {
            focusCollectionNavigation = null;
            await JS.InvokeVoidAsync("galleryViewer.focusCollectionNavigation", open);
        }
        if (!firstRender) return;
        gridReference = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("galleryGrid.attach", gridReference);
        securityReference = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("gallerySecurity.attach", securityReference, autoLockMinutes);
    }
    private void PublishToasts()
    {
        if (!UsesToasts) { DismissProgressToast(); return; }
        if (error.Length > 0) { Toasts.Show("Something went wrong", error, SonnerToastVariant.Destructive, 8000); error = ""; }
        if (message.Length > 0) { Toasts.Show(message, "", SonnerToastVariant.Default, 4000); message = ""; }
        if (!busy) DismissProgressToast();
        else if (progressToast is null && !progressToastScheduled)
        {
            progressToastScheduled = true;
            _ = ShowProgressToastAsync();
        }
    }
    // Only operations that are still running after a short delay get a progress toast, so quick actions stay quiet.
    private async Task ShowProgressToastAsync()
    {
        await Task.Delay(600);
        await InvokeAsync(() =>
        {
            progressToastScheduled = false;
            if (!disposed && busy && UsesToasts && progressToast is null)
                progressToast = Toasts.Show(progress.Length > 0 ? progress : "Working...", "", SonnerToastVariant.Default, 600_000);
        });
    }
    private void DismissProgressToast()
    {
        if (progressToast is not { } id) return;
        progressToast = null;
        Toasts.Dismiss(id);
    }
    private Task PreviousAsync() => RunAsync("Loading previews...", async () => { page--; await LoadThumbnailsAsync(); });
    private Task NextAsync() => RunAsync("Loading previews...", async () => { page++; await LoadThumbnailsAsync(); });

    private Task ChangePassphraseAsync() => RunAsync("Updating passphrase...", async () =>
    {
        EnsureNoLibraryJob();
        try
        {
            await Gallery.ChangePassphraseAsync(newPassphrase, newConfirm);
            message = "Passphrase updated. Your recovery words are unchanged.";
        }
        finally { newPassphrase = newConfirm = ""; }
    });

    private void EnsureNoLibraryJob()
    {
        if (libraryJob.IsRunning) throw new InvalidOperationException("Wait for the running LLM job to finish, or stop it after the current media item, before changing files or gallery settings.");
    }

    private async Task UpdateAutoLockMinutesAsync(string? value)
    {
        if (busy) return;
        if (int.TryParse(value, out var minutes))
        {
            minutes = Math.Clamp(minutes, 0, 1440);
            await RunAsync("Saving auto-lock timer...", () => SavePreferencesAsync(minutes));
            if (error.Length > 0)
            {
                error = "Auto-lock timer could not be saved. The previous timer is still active.";
                return;
            }
            autoLockMinutes = minutes;
            Gallery.ConfigureAutoLock(autoLockMinutes);
            try { await JS.InvokeVoidAsync("gallerySecurity.configureAutoLock", autoLockMinutes); }
            catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
        }
    }

    [JSInvokable]
    public async Task TriggerPanicLock()
    {
        await InvokeAsync(PanicAsync);
    }

    [JSInvokable]
    public async Task TriggerAutoLock()
    {
        await InvokeAsync(() =>
        {
            if (!Gallery.IsUnlocked) return;
            Lock();
            message = "Gallery locked after inactivity. Its checkout was released.";
            StateHasChanged();
        });
    }

    private async Task PanicAsync()
    {
        try { await JS.InvokeVoidAsync("gallerySecurity.purgeAllMedia"); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
        Gallery.Panic();
        Lock();
        message = "Gallery locked and memory purged. Its checkout was released.";
        StateHasChanged();
    }

    private void Lock()
    {
        accessRevision++;
        quickFilters = [];
        CloseQuickFilterDialog();
        workbench?.Dispose();
        OrganizationHistory.Clear();
        collectionWorking = false;
        CancelCatalogLoad();
        catalogPartial = false;
        catalogLoadError = "";
        try { _ = JS.InvokeVoidAsync("gallerySecurity.purgeAllMedia"); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
        Gallery.LockVault();
        images.Clear();
        collectionCatalog = CollectionCatalog.Empty;
        collectionLocation = new();
        collectionNavigationOpen = false;
        browsingProjection = GalleryBrowsingProjection.Empty;
        browsingSource = null;
        StopThumbnailLoad();
        thumbnailTask = null;
        thumbnails.Clear();
        staleThumbnails.Clear();
        thumbnailFailures.Clear();
        CloseDetail();
        recoveryPhrase = newPassphrase = newConfirm = error = message = importPath = "";
        ClearIngestionScan();
        if (!importRunning)
        {
            importFailures.Clear();
            importProgress = null;
            timingReport = null;
        }
        settings = new();
        catalogTags = [];
        filter = new();
        page = 0;
        startWithCreate = false;
        workflow = "gallery";
        activeTool = "";
        toolboxVisible = true;
        (toolboxX, toolboxY) = ((double?)null, (double?)null);
        aiSuggestions = new Dictionary<string, OrganizationSuggestion>();
        DismissProgressToast();
        settingsTab = "overview";
        pendingSettingsAction = null;
        llmEndpointSettings = null;
        storageOverview = null;
        backupOptions = null;
        backupDestinationType = "local";
        backupLocalDirectory = backupAzureContainer = backupAzurePrefix = backupAzureConnectionString = "";
        browsingFilter = null;
        libraryJob = new(false, "", "", 0, false);
        deleteSources = false;
        removalSelection.Clear();
        removalPlan.Clear();
        removalResults.Clear();
        confirmRemoval = false;
        restoreFolder = "";
        librarySelection.Clear();
        viewerImages.Clear();
        viewerInitialId = "";
        viewerInitialPreview = "";
        imagesById = [];
        density = UiPreferences.DetailDensity;
        selectionAnchor = null;
        duplicateScan = null;
        duplicateThumbnails.Clear();
        VisibleDuplicateGroups = [];
        reviewGroups = [];
        DuplicateGroupsShown = 20;
    }

    public void Dispose()
    {
        ClearIngestionScan();
        disposed = true;
        OrganizationHistory.Changed -= HandleOrganizationHistoryChanged;
        Gallery.OnAutoLocked -= HandleAutoLocked;
        Lock();
        gridReference?.Dispose();
        securityReference?.Dispose();
    }
}
