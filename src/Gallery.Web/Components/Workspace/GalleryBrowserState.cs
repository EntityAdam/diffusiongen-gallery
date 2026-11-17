using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Workspace;

// Browsing state survives workflow switches, but never an unlocked session.
public sealed class GalleryBrowserState : IDisposable
{
    private readonly GalleryService gallery;
    private readonly WorkspaceCatalog catalog;
    private readonly Func<Task> savePreferences;
    private readonly ILogger logger;
    private bool disposed;
    internal ThumbnailCache Thumbnails { get; }
    public GalleryBrowserState(GalleryService gallery, WorkspaceCatalog catalog, ILogger logger, Action changed, Func<Task> savePreferences)
    {
        this.gallery = gallery;
        this.catalog = catalog;
        this.savePreferences = savePreferences;
        this.logger = logger;
        Thumbnails = new(gallery, logger, changed);
    }

    public GalleryFilter Filter { get; set; } = new();
    public GalleryFilter? BrowsingFilter { get; set; }
    public long FilterInputRevision { get; set; }
    public string Grouping { get; set; } = "none";
    public string Density { get; set; } = UiPreferences.DetailDensity;
    public int Page { get; set; }
    public int PageSize => Density == "compact" ? 49 : 12;
    public int PageCount => Math.Max(1, (Filtered.Count + PageSize - 1) / PageSize);
    public IReadOnlyList<ImageRecord> Filtered => Browsing.Images;
    public IEnumerable<ImageRecord> PageImages => Filtered.Skip(Page * PageSize).Take(PageSize);
    public HashSet<string> LibrarySelection { get; } = [];
    public HashSet<string> RemovalSelection { get; } = [];
    public string? SelectionAnchor { get; set; }
    public IReadOnlyList<SavedView> QuickFilters { get; set; } = [];
    public IReadOnlyDictionary<string, OrganizationSuggestion> Suggestions { get; private set; } = new Dictionary<string, OrganizationSuggestion>();
    public string ActiveTool { get; set; } = "";
    public bool TrashActive => ActiveTool == "trash";
    public bool ToolboxVisible { get; set; } = true;
    public double? ToolboxX { get; set; }
    public double? ToolboxY { get; set; }
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
                browsingProjection = GalleryBrowsingProjector.Create(gallery, catalog.Images, Filter, grouping, Suggestions.Keys.ToHashSet());
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
        ToolboxVisible = preferences.ToolboxVisible;
        (ToolboxX, ToolboxY) = (preferences.ToolboxX, preferences.ToolboxY);
        ActiveTool = preferences.ToolboxTool;
        BrowsingFilter = TrashActive ? new() : null;
        FilterInputRevision++;
        Filter = TrashActive ? new() { Deletion = "marked" } : new();
        var views = await gallery.LoadViewsAsync();
        if (!disposed && gallery.IsUnlocked) QuickFilters = views.OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void ClearSelection()
    {
        LibrarySelection.Clear();
        RemovalSelection.Clear();
        SelectionAnchor = null;
    }

    public void SelectCard(string id, bool range)
    {
        var selection = TrashActive ? RemovalSelection : LibrarySelection;
        var ordered = Filtered.Select(image => image.Id).ToList();
        var anchor = SelectionAnchor is null ? -1 : ordered.IndexOf(SelectionAnchor);
        var target = ordered.IndexOf(id);
        if (range && anchor >= 0 && target >= 0)
        {
            var (from, to) = anchor <= target ? (anchor, target) : (target, anchor);
            selection.UnionWith(ordered.Skip(from).Take(to - from + 1));
        }
        else if (!selection.Remove(id)) selection.Add(id);
        SelectionAnchor = id;
    }

    public Task CatalogChangedAsync()
    {
        RemovalSelection.IntersectWith(catalog.Images.Where(image => image.IsMarkedForDeletion).Select(image => image.Id));
        LibrarySelection.IntersectWith(catalog.Images.Select(image => image.Id));
        Page = Math.Min(Page, PageCount - 1);
        return LoadThumbnailsAsync();
    }

    public Task FilterChangedAsync()
    {
        Page = 0;
        ClearSelection();
        return LoadThumbnailsAsync();
    }
    public Task ResetFiltersAsync()
    {
        FilterInputRevision++;
        Filter = TrashActive ? new() { Deletion = "marked" } : new();
        return FilterChangedAsync();
    }
    public Task ApplyFilterAsync(GalleryFilter value)
    {
        if (catalog.IsPartial) return Task.CompletedTask;
        Filter = value;
        return FilterChangedAsync();
    }
    public Task SetSortAsync(string? value) => ApplyFilterAsync(Filter with { Sort = value ?? "newest" });
    public async Task SetActiveToolAsync(string value)
    {
        if (catalog.IsPartial && value is not ("" or "views")) return;
        value = UiPreferences.ToolboxTools.Contains(value) ? value : "";
        if (ActiveTool == value) return;
        var wasTrash = TrashActive;
        ActiveTool = value;
        if (TrashActive != wasTrash)
        {
            FilterInputRevision++;
            if (TrashActive) { BrowsingFilter = Filter with { }; Filter = new() { Deletion = "marked" }; }
            else { Filter = BrowsingFilter ?? new(); BrowsingFilter = null; }
            ClearSelection();
            if (catalog.IsPartial) await catalog.OpenAsync(PageSize, TrashActive);
            await FilterChangedAsync();
        }
        await SaveToolboxStateAsync();
    }
    public async Task SetDensityAsync(string value)
    {
        if (Density == value) return;
        var first = Page * PageSize;
        Density = value;
        if (catalog.IsPartial) await catalog.OpenAsync(PageSize, TrashActive);
        Page = Math.Min(first / PageSize, PageCount - 1);
        await LoadThumbnailsAsync();
        await savePreferences();
    }
    public async Task SaveToolboxStateAsync()
    {
        if (disposed || !gallery.IsUnlocked) return;
        try { await savePreferences(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning(exception, "Toolbox preferences were not saved");
        }
    }
    public Task SuggestionsChangedAsync(IReadOnlyList<OrganizationSuggestion> suggestions)
    {
        if (disposed) return Task.CompletedTask;
        Suggestions = suggestions.ToDictionary(suggestion => suggestion.Id);
        browsingSource = null;
        Page = Math.Min(Page, PageCount - 1);
        return Filter.Analysis == GalleryFilter.AwaitingApproval ? LoadThumbnailsAsync() : Task.CompletedTask;
    }
    public string ToolBadge(string tool) => tool switch
    {
        "filters" when !TrashActive && GalleryFilterPanel.CountChanged(Filter) is > 0 and var count => count.ToString(),
        "trash" when !catalog.IsPartial && catalog.Images.Count(image => image.IsMarkedForDeletion) is > 0 and var count => count.ToString(),
        "intelligence" when Suggestions.Count > 0 => Suggestions.Count.ToString(),
        _ => ""
    };
    public Task LoadThumbnailsAsync() => Thumbnails.ReloadAsync(PageImages);
    public Task LoadMissingThumbnailsAsync() => Thumbnails.LoadMissingAsync(PageImages);
    public Task PauseAsync() => Thumbnails.PauseAsync();
    public void Resume() => Thumbnails.Resume(PageImages);
    public void Dispose()
    {
        disposed = true;
        Thumbnails.Clear();
        ClearSelection();
        QuickFilters = [];
        Suggestions = new Dictionary<string, OrganizationSuggestion>();
        Filter = new();
        BrowsingFilter = null;
        browsingProjection = GalleryBrowsingProjection.Empty;
        browsingSource = null;
    }
}
