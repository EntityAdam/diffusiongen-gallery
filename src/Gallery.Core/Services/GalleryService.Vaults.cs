using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public static string DefaultApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionGenGallery");
    public string VaultName => galleryLifetime.Current.VaultStore.Name;

    public IReadOnlyList<GalleryLocation> KnownGalleries => vaultLocations.KnownGalleries;

    public static string DefaultVaultDirectory() =>
        Path.Combine(DefaultApplicationDirectory, "galleries", Guid.NewGuid().ToString("N"));

    public Task OpenExistingVaultAsync(string directory) => vaultLocations.OpenExistingVaultAsync(directory);
    public Task SelectNewVaultAsync(string directory) => vaultLocations.SelectNewVaultAsync(directory);
    public Task CloseVaultAsync() => vaultLocations.CloseVaultAsync();
    public Task RenameVaultAsync(string name) => vaultManager.RenameVaultAsync(name);
    public Task MoveVaultAsync(string directory) => vaultLocations.MoveVaultAsync(directory);
    public Task PurgeVaultAsync(string confirmation) => vaultLocations.PurgeVaultAsync(confirmation);
}
