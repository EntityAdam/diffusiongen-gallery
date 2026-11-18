using Gallery.Core.Models;
using Gallery.Core.Services.Processors;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<UiPreferences> LoadPreferencesAsync() => gallerySettings.LoadPreferencesAsync();
    public Task SavePreferencesAsync(UiPreferences preferences) => gallerySettings.SavePreferencesAsync(preferences);

    public bool CatalogIsOnNetworkDrive => CatalogDirectoryRules.IsNetworkDirectory(VaultRoot);

    /// <summary>Relocates only the encrypted catalog and header; all encrypted media stays at its existing paths.</summary>
    public Task RelocateCatalogAsync(string directory) => vaultLocations.RelocateCatalogAsync(directory);

    public Task<IngestionOptions> LoadIngestionOptionsAsync() => gallerySettings.LoadIngestionOptionsAsync();
    public Task SaveIngestionOptionsAsync(IngestionOptions options) => gallerySettings.SaveIngestionOptionsAsync(options);
    public Task<MediaStorageOptions> LoadMediaStorageOptionsAsync() => gallerySettings.LoadMediaStorageOptionsAsync();
    public Task SaveMediaStorageOptionsAsync(MediaStorageOptions options) => gallerySettings.SaveMediaStorageOptionsAsync(options);

    public Task<List<SavedView>> LoadViewsAsync() => gallerySettings.LoadViewsAsync();
    public Task SaveViewAsync(string name, GalleryFilter filter) => gallerySettings.SaveViewAsync(name, filter);
    public Task DeleteViewAsync(string name) => gallerySettings.DeleteViewAsync(name);
}
