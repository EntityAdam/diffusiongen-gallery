using Gallery.Core.Models;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    private IReadOnlyList<SavedView> quickFilters = [];
    private GalleryFilter? quickFilterSnapshot;
    private string quickFilterName = "";
    private bool ReplacesQuickFilter => quickFilters.Any(view => view.Name.Equals(quickFilterName.Trim(), StringComparison.OrdinalIgnoreCase));

    private void BeginSaveQuickFilter()
    {
        if (busy || catalogPartial || GalleryFilterPanel.CountChanged(filter) == 0) return;
        quickFilterName = "";
        quickFilterSnapshot = filter with { };
    }

    private void CloseQuickFilterDialog()
    {
        quickFilterSnapshot = null;
        quickFilterName = "";
    }

    private Task SaveQuickFilterAsync()
    {
        if (busy || quickFilterSnapshot is null || string.IsNullOrWhiteSpace(quickFilterName)) return Task.CompletedTask;
        var snapshot = quickFilterSnapshot;
        var name = quickFilterName.Trim();
        return RunAsync("Saving quick filter...", async () =>
        {
            await Gallery.SaveViewAsync(name, snapshot);
            var saved = await Gallery.LoadViewsAsync();
            if (!Gallery.IsUnlocked) return;
            quickFilters = saved.OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            CloseQuickFilterDialog();
            message = "Quick filter saved.";
        });
    }
}
