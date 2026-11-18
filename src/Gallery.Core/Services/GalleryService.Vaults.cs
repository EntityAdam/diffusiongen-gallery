using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Sessions.Locations;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    private SemaphoreSlim lifecycleGate => galleryLifetime.Gate;
    private VaultCheckout? checkout => galleryLifetime.Current.Checkout;
    private VaultHistory? history => galleryLifetime.History;

    public static string DefaultApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionGenGallery");
    public string VaultName => vaultStore.Name;

    public IReadOnlyList<GalleryLocation> KnownGalleries
    {
        get
        {
            var roots = (history?.ReadGalleries() ?? []).Prepend(VaultRoot)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var galleries = new List<GalleryLocation>();
            foreach (var root in roots)
            {
                try
                {
                    var store = new VaultStore(root);
                    if (store.Exists && File.Exists(Path.Combine(root, CatalogFileName)))
                        galleries.Add(new(root, store.Name));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { }
            }
            return galleries;
        }
    }

    public static string DefaultVaultDirectory() =>
        Path.Combine(DefaultApplicationDirectory, "galleries", Guid.NewGuid().ToString("N"));

    private void EnsureActive() => ObjectDisposedException.ThrowIf(galleryLifetime.IsDisposed, this);
    private void EnsureCheckout()
    {
        EnsureActive();
        galleryLifetime.EnsureCheckout();
    }

    public Task OpenExistingVaultAsync(string directory) => vaultLocations.OpenExistingVaultAsync(directory);
    public Task SelectNewVaultAsync(string directory) => vaultLocations.SelectNewVaultAsync(directory);
    public Task CloseVaultAsync() => vaultLocations.CloseVaultAsync();
    public Task RenameVaultAsync(string name) => vaultManager.RenameVaultAsync(name);
    public Task MoveVaultAsync(string directory) => vaultLocations.MoveVaultAsync(directory);
    public Task PurgeVaultAsync(string confirmation) => vaultLocations.PurgeVaultAsync(confirmation);
}
