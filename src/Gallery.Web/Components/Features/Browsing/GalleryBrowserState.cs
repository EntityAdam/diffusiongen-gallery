using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Features.Settings;
using Gallery.Web.Components.Features.Tags;
using Gallery.Web.Components.Features.Vision;
using Gallery.Web.Components.Workspace;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Features.Browsing;

// Browsing state survives workflow switches, but never an unlocked session.
public sealed class GalleryBrowserState : IDisposable
{
    private readonly IGalleryVault vault;
    private readonly IGalleryCatalog galleryCatalog;
    private readonly IGallerySettings settings;
    private readonly WorkspaceCatalog catalog;
    private readonly WorkspacePreferences preferences;
    private CollectionCatalog previousCollections = CollectionCatalog.Empty;
    private readonly ILogger logger;
    private bool disposed;
    internal ThumbnailCache Thumbnails { get; }
    public GalleryBrowserState(IGalleryVault vault, IGalleryCatalog galleryCatalog, IGallerySettings settings, IGalleryMedia media,
        WorkspaceCatalog catalog, ILogger logger, Action changed, WorkspacePreferences preferences, IJSRuntime js)
    {
        this.vault = vault;
        this.galleryCatalog = galleryCatalog;
        this.settings = settings;
        this.catalog = catalog;
        this.preferences = preferences;
        this.logger = logger;
        Thumbnails = new(vault, media, logger, () =>
        {
            if (InfiniteScroll) ViewportThumbnailsChanged?.Invoke();
            else changed();
        }, js);
    }

    public GalleryFilter Filter { get; private set; } = new();
    public GalleryFilter? BrowsingFilter { get; private set; }
    public long FilterInputRevision { get; private set; }
    public string Grouping { get; private set; } = "none";
    public string Density { get; private set; } = UiPreferences.DetailDensity;
    public int Page { get; private set; }
    public int PageSize => Density == "compact" ? 49 : 12;
    internal int InitialFetchSize => InfiniteScroll ? 49 : PageSize;
    internal bool InfiniteScroll { get; private set; }
    internal int ThumbnailScale => preferences.ThumbnailScale;
    internal long ViewReset { get; private set; }
    private bool galleryActive = true;
    private (string Id, int Index)? viewerReturn;
    private long viewportOwner;
    internal event Action? ViewportThumbnailsChanged;
    public int PageCount => Math.Max(1, (Filtered.Count + PageSize - 1) / PageSize);
    public IReadOnlyList<ImageRecord> Filtered => Browsing.Images;
    public IEnumerable<ImageRecord> PageImages => Filtered.Skip(Page * PageSize).Take(PageSize);
    private readonly HashSet<string> librarySelection = [];
    private readonly HashSet<string> removalSelection = [];
    public IReadOnlySet<string> LibrarySelection => librarySelection;
    public IReadOnlySet<string> RemovalSelection => removalSelection;
    public string? SelectionAnchor { get; private set; }
    public IReadOnlyList<SavedView> QuickFilters { get; private set; } = [];
    public IReadOnlyDictionary<string, OrganizationSuggestion> Suggestions { get; private set; } = new Dictionary<string, OrganizationSuggestion>();
    public string ActiveTool { get; private set; } = "";
    public bool TrashActive => ActiveTool == "trash";
    public bool ReviewRandomOrder { get; set; }
    private int reviewGoal = 10;
    public IEnumerable<ImageRecord> ReviewPool => Filtered.Where(image => !image.IsReviewed && !image.IsMarkedForDeletion);
    public int ReviewPoolCount => ReviewPool.Count();
    public int ReviewGoal
    {
        get => Math.Clamp(reviewGoal, ReviewPoolCount > 0 ? 1 : 0, ReviewPoolCount);
        set => reviewGoal = Math.Clamp(value, ReviewPoolCount > 0 ? 1 : 0, ReviewPoolCount);
    }

    private GalleryBrowsingProjection browsingProjection = GalleryBrowsingProjection.Empty;
    private IReadOnlyList<ImageRecord>? browsingSource;
    private GalleryFilter? projectedFilter;
    private string? projectedGrouping;
    internal GalleryBrowsingProjection Browsing
    {
        get
        {
            var grouping = catalog.IsPartial ? "none" : Grouping;
            if (!ReferenceEquals(browsingSource, catalog.Images) || projectedFilter != Filter || projectedGrouping != grouping)
            {
                var previousReviewProjection = grouping == "prompt" && Filter.Review == "pending"
                    && projectedFilter == Filter && projectedGrouping == grouping ? browsingProjection : null;
                browsingProjection = GalleryBrowsingProjector.Create(galleryCatalog, catalog.Images, Filter, grouping,
                    Suggestions.Keys.ToHashSet(), previousReviewProjection);
                browsingSource = catalog.Images;
                projectedFilter = Filter;
                projectedGrouping = grouping;
            }
            return browsingProjection;
        }
    }

    public async Task InitializeAsync(UiPreferences preferences)
    {
        Density = preferences.GridDensity;
        SetGalleryActive(true);
        ActiveTool = preferences.ActiveTool;
        BrowsingFilter = TrashActive ? new() : null;
        FilterInputRevision++;
        Filter = TrashActive ? new() { Deletion = "marked" } : new();
        var views = await settings.LoadViewsAsync();
        if (!disposed && vault.IsUnlocked) QuickFilters = views.OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void ClearSelection()
    {
        librarySelection.Clear();
        removalSelection.Clear();
        SelectionAnchor = null;
    }

    public void SelectCard(string id, bool range)
    {
        var selection = TrashActive ? removalSelection : librarySelection;
        var ordered = Filtered.Select(image => image.Id).ToList();
        SelectionAnchor = MediaRangeSelection.Select(selection, ordered, SelectionAnchor, id, range);
    }

    public Task CatalogChangedAsync()
    {
        Filter = UpdateCollectionName(Filter);
        if (BrowsingFilter is { } saved) BrowsingFilter = UpdateCollectionName(saved);
        previousCollections = catalog.Collections;
        removalSelection.IntersectWith(catalog.Images.Where(image => image.IsMarkedForDeletion).Select(image => image.Id));
        librarySelection.IntersectWith(catalog.Images.Select(image => image.Id));
        Page = Math.Min(Page, PageCount - 1);
        return LoadThumbnailsAsync();
    }

    private GalleryFilter UpdateCollectionName(GalleryFilter filter)
    {
        var previous = previousCollections.Collections.FirstOrDefault(item =>
            item.Name.Equals(filter.Collection, StringComparison.OrdinalIgnoreCase));
        var current = previous is null ? null : catalog.Collections.Collections.FirstOrDefault(item => item.Id == previous.Id);
        return current is null ? filter : filter with { Collection = current.Name };
    }

    internal Task RecordsChangedAsync(IReadOnlyList<ImageRecord> records, bool reloadThumbnails)
    {
        Page = Math.Min(Page, PageCount - 1);
        LoadMissingThumbnailsAsync();
        if (reloadThumbnails) Thumbnails.Invalidate(records.Select(image => image.Id));
        return Task.CompletedTask;
    }

    internal void RenameTag(TagCatalogChange change)
    {
        if (change.PreviousName is not null && change.Name is not null)
        {
            Filter = Filter.WithRenamedTag(change.PreviousName, change.Name);
            if (BrowsingFilter is { } saved) BrowsingFilter = saved.WithRenamedTag(change.PreviousName, change.Name);
        }
    }

    internal async Task ViewTagAsync(string tag)
    {
        FilterInputRevision++;
        Filter = new() { Tag = tag };
        BrowsingFilter = null;
        ActiveTool = "filters";
        await ResetPageAndSelectionAsync();
        await SaveBrowserStateAsync();
    }

    internal Task ResetPageAndSelectionAsync()
    {
        ResetPageAndSelection();
        return LoadThumbnailsAsync();
    }

    private void ResetPageAndSelection()
    {
        Page = 0;
        ViewReset++;
        if (InfiniteScroll) Thumbnails.Clear();
        ClearSelection();
    }

    public Task GoToPageAsync(int page)
    {
        if (catalog.IsPartial || page < 0 || page >= PageCount || page == Page)
            return Task.CompletedTask;

        Page = page;
        return LoadThumbnailsAsync();
    }

    public Task SetGroupingAsync(string grouping)
    {
        if (catalog.IsPartial) return Task.CompletedTask;
        Grouping = grouping;
        return ResetPageAndSelectionAsync();
    }

    internal void SetQuickFilters(IReadOnlyList<SavedView> views) => QuickFilters = views;

    public Task ResetFiltersAsync()
    {
        FilterInputRevision++;
        Filter = TrashActive ? new() { Deletion = "marked" } : new();
        return ResetPageAndSelectionAsync();
    }
    public Task ApplyFilterAsync(GalleryFilter value)
    {
        if (catalog.IsPartial) return Task.CompletedTask;
        Filter = value;
        return ResetPageAndSelectionAsync();
    }
    public Task SetSortAsync(string? value) => ApplyFilterAsync(Filter with { Sort = value ?? "newest" });
    internal Task OpenReviewAsync() => ChangeActiveToolAsync("review", resetView: true);

    internal Task OpenTrashAsync()
    {
        return SetActiveToolAsync("trash");
    }

    public async Task SetActiveToolAsync(string value)
    {
        if (catalog.IsPartial && value is not ("" or "views")) return;
        if (await ChangeActiveToolAsync(value)) await SaveBrowserStateAsync();
    }

    private async Task<bool> ChangeActiveToolAsync(string value, bool resetView = false)
    {
        value = UiPreferences.ActiveTools.Contains(value) ? value : "";
        var changed = ActiveTool != value;
        var wasTrash = TrashActive;
        ActiveTool = value;
        var trashChanged = TrashActive != wasTrash;
        if (trashChanged)
        {
            FilterInputRevision++;
            if (TrashActive)
            {
                BrowsingFilter = Filter;
                Filter = new() { Deletion = "marked" };
            }
            else
            {
                Filter = BrowsingFilter ?? new();
                BrowsingFilter = null;
            }
        }

        if (!resetView && !trashChanged) return changed;

        ResetPageAndSelection();
        if (trashChanged && catalog.IsPartial) await catalog.OpenAsync(InitialFetchSize, TrashActive);
        await LoadThumbnailsAsync();
        return changed;
    }

    public async Task SetDensityAsync(string value)
    {
        if (Density == value) return;
        var first = Page * PageSize;
        Density = value;
        if (catalog.IsPartial) await catalog.OpenAsync(PageSize, TrashActive);
        Page = Math.Min(first / PageSize, PageCount - 1);
        await LoadThumbnailsAsync();
        await SavePreferencesAsync();
    }
    private async Task SaveBrowserStateAsync()
    {
        if (disposed || !vault.IsUnlocked) return;
        try { await SavePreferencesAsync(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning(exception, "Browser preferences were not saved");
        }
    }
    private Task SavePreferencesAsync() =>
        preferences.SaveBrowserAsync(Density, ActiveTool);

    public Task SuggestionsChangedAsync(IReadOnlyList<OrganizationSuggestion> suggestions)
    {
        if (disposed) return Task.CompletedTask;
        Suggestions = suggestions.ToDictionary(suggestion => suggestion.Id);
        browsingSource = null;
        Page = Math.Min(Page, PageCount - 1);
        return Filter.Analysis == GalleryFilter.AwaitingApproval ? LoadThumbnailsAsync() : Task.CompletedTask;
    }

    internal void SetGalleryActive(bool active)
    {
        galleryActive = active;
        if (!active)
        {
            viewportOwner++;
            Thumbnails.Clear();
            viewerReturn = null;
            return;
        }
        InfiniteScroll = preferences.IsEnabled(ExperimentalFeatures.InfiniteScroll);
    }

    internal void RestoreViewerPosition((string Id, int Index) position)
    {
        if (!disposed && galleryActive && InfiniteScroll && Filtered.Count > 0) viewerReturn = position;
    }

    internal (string Id, int Index)? TakeViewerReturn()
    {
        var position = viewerReturn;
        viewerReturn = null;
        return position;
    }

    internal Task SetThumbnailScaleAsync(int scale) =>
        preferences.SetThumbnailScaleAsync(Math.Clamp(scale, UiPreferences.MinThumbnailScale, UiPreferences.MaxThumbnailScale));

    internal long AcquireViewport() => ++viewportOwner;

    internal void ReleaseViewport(long owner)
    {
        if (owner != viewportOwner) return;
        viewportOwner++;
        Thumbnails.Clear();
    }

    internal void SetViewport(long owner, string[] ids, int visibleCount, bool immediate)
    {
        if (!disposed && galleryActive && InfiniteScroll && owner == viewportOwner)
            Thumbnails.SetWindow(ids, visibleCount, immediate, virtualized: true);
    }

    public Task LoadThumbnailsAsync()
    {
        if (InfiniteScroll) return Task.CompletedTask;
        LoadMissingThumbnailsAsync();
        Thumbnails.Invalidate(PageImages.Select(image => image.Id));
        return Task.CompletedTask;
    }

    public Task LoadMissingThumbnailsAsync()
    {
        if (galleryActive && !InfiniteScroll)
        {
            var ids = PageImages.Select(image => image.Id).ToArray();
            Thumbnails.SetWindow(ids, ids.Length, immediate: true, virtualized: false);
        }
        return Task.CompletedTask;
    }
    public Task PauseAsync() => Thumbnails.PauseAsync();
    public void Resume() => Thumbnails.Resume();
    public void Dispose()
    {
        disposed = true;
        Thumbnails.Clear();
        ClearSelection();
        QuickFilters = [];
        Suggestions = new Dictionary<string, OrganizationSuggestion>();
        Filter = new();
        BrowsingFilter = null;
        previousCollections = CollectionCatalog.Empty;
        browsingProjection = GalleryBrowsingProjection.Empty;
        browsingSource = null;
    }
}
