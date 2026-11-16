using Gallery.Core.Models;
using System.Collections.Immutable;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    private DuplicateScan? duplicateScan;
    private readonly Dictionary<string, string> duplicateThumbnails = [];
    private int DuplicateGroupsShown = 20;
    private List<DuplicateGroup> reviewGroups = [];
    private int reviewStartGroup;
    private List<DuplicateGroup> VisibleDuplicateGroups = [];
    private void UpdateDuplicateGroups() => VisibleDuplicateGroups = duplicateScan is null ? [] : duplicateScan.Groups
        .Select(group => group with { ImageIds = group.ImageIds.Where(imagesById.ContainsKey).ToImmutableArray() })
        .Where(group => group.ImageIds.Length > 1).ToList();
    private ImageRecord KeeperOf(DuplicateGroup group) => Gallery.PickDuplicateKeeper(group.ImageIds.Select(id => ImageById(id)!));
    private async Task LoadCachedDuplicatesAsync()
    {
        duplicateScan ??= await Gallery.CachedDuplicateScanAsync();
        UpdateDuplicateGroups();
    }
    private async Task LoadDuplicateThumbnailsAsync()
    {
        foreach (var id in VisibleDuplicateGroups.Take(DuplicateGroupsShown).SelectMany(group => group.ImageIds))
            if (!duplicateThumbnails.ContainsKey(id)) duplicateThumbnails[id] = await Gallery.ThumbnailAsync(id);
    }
    private Task ShowMoreDuplicatesAsync() => RunAsync("Loading previews...", async () =>
    {
        DuplicateGroupsShown += 20;
        await LoadDuplicateThumbnailsAsync();
    });
    private Task ScanDuplicatesAsync() => RunAsync("Scanning for duplicates...", async () =>
    {
        var reporter = new InlineProgress<(int Completed, int Total)>(update => InvokeAsync(() =>
        {
            if (disposed || !busy) return;
            progress = $"Scanning for duplicates... {update.Completed:N0} / {update.Total:N0} media items fingerprinted";
            StateHasChanged();
        }));
        duplicateScan = await Gallery.ScanDuplicatesAsync(reporter);
        if (disposed) return;
        duplicateThumbnails.Clear();
        DuplicateGroupsShown = 20;
        await ReloadAsync();
        await LoadDuplicateThumbnailsAsync();
        message = $"Duplicate scan complete: {VisibleDuplicateGroups.Count} group(s) found.";
    });
    private Task MarkDuplicatesAsync(IReadOnlyCollection<DuplicateGroup> groups) => RunAsync("Marking extra copies...", async () =>
    {
        var ids = groups.SelectMany(group =>
        {
            var keeper = KeeperOf(group);
            return group.ImageIds.Where(id => id != keeper.Id);
        }).ToList();
        try { await Gallery.UpdateManyAsync(ids, image => Gallery.SetDeletionMark(image, true)); }
        finally { await ReloadAsync(); }
        message = $"Marked {ids.Count} extra cop{(ids.Count == 1 ? "y" : "ies")} for deletion. Review and remove them from Trash.";
    });
    private void OpenDuplicateReview(IReadOnlyList<DuplicateGroup> groups, int start)
    {
        if (busy || groups.Count == 0) return;
        reviewGroups = groups.ToList();
        reviewStartGroup = Math.Clamp(start, 0, groups.Count - 1);
    }
    private Task CloseDuplicateReviewAsync(string summary)
    {
        reviewGroups = [];
        return RunAsync("Refreshing library...", async () =>
        {
            await ReloadAsync();
            if (workflow == "duplicates") await LoadDuplicateThumbnailsAsync();
            message = summary;
        });
    }
    private Task FindSelectedDuplicatesAsync() => RunAsync("Finding duplicates of selected media...", async () =>
    {
        var reporter = new InlineProgress<(int Completed, int Total)>(update => InvokeAsync(() =>
        {
            if (disposed || !busy) return;
            progress = $"Finding duplicates... {update.Completed:N0} / {update.Total:N0} media items fingerprinted";
            StateHasChanged();
        }));
        duplicateScan = await Gallery.ScanDuplicatesAsync(reporter);
        if (disposed) return;
        duplicateThumbnails.Clear();
        DuplicateGroupsShown = 20;
        UpdateDuplicateGroups();
        var matches = VisibleDuplicateGroups.Where(group => group.ImageIds.Any(librarySelection.Contains)).ToList();
        if (matches.Count == 0)
        {
            message = $"No likely duplicates found for the {librarySelection.Count} selected media item(s).";
            return;
        }
        reviewGroups = matches;
        reviewStartGroup = 0;
    });
}
