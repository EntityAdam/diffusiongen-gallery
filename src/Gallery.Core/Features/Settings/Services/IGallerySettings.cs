using Gallery.Core.Models;

namespace Gallery.Core.Services;

public interface IGallerySettings
{
    Task<UiPreferences> LoadPreferencesAsync();
    Task SavePreferencesAsync(UiPreferences preferences);
    Task<IngestionOptions> LoadIngestionOptionsAsync();
    Task SaveIngestionOptionsAsync(IngestionOptions options);
    Task<MediaStorageOptions> LoadMediaStorageOptionsAsync();
    Task SaveMediaStorageOptionsAsync(MediaStorageOptions options);
    Task<List<SavedView>> LoadViewsAsync();
    Task SaveViewAsync(string name, GalleryFilter filter);
    Task DeleteViewAsync(string name);
}
