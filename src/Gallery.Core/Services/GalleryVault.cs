using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Sessions;
using GalleryLocation = Gallery.Core.Models.GalleryLocation;

namespace Gallery.Core.Services;

internal sealed class GalleryVault(GalleryLifetime galleryLifetime, VaultSession vaultSession, VaultAccess vaultAccess,
    VaultManager vaultManager, VaultLocationCoordinator vaultLocations) : IGalleryVault
{
    public bool CatalogIsOnNetworkDrive => CatalogDirectoryRules.IsNetworkDirectory(VaultRoot);

    /// <summary>Relocates only the encrypted catalog and header; all encrypted media stays at its existing paths.</summary>

    public Task RelocateCatalogAsync(string directory) => vaultLocations.RelocateCatalogAsync(directory);

    public bool IsUnlocked => vaultSession.IsUnlocked;

    public bool VaultExists => galleryLifetime.Current.VaultStore.Exists;

    public string VaultRoot => galleryLifetime.Current.VaultStore.Root;

    public Task<string> CreateVaultAsync(string passphrase, string confirmation, string? name = null,
     MediaStorageOptions? storageOptions = null) =>
     vaultManager.CreateVaultAsync(passphrase, confirmation, name, storageOptions);

    public Task UnlockWithPassphraseAsync(string passphrase) => vaultAccess.UnlockWithPassphraseAsync(passphrase);

    public Task UnlockWithRecoveryPhraseAsync(string recoveryPhrase) => vaultAccess.UnlockWithRecoveryPhraseAsync(recoveryPhrase);

    public Task ChangePassphraseAsync(string passphrase, string confirmation) =>
        vaultManager.ChangePassphraseAsync(passphrase, confirmation);

    public event Action? OnAutoLocked
    {
        add => vaultAccess.AutoLocked += value;
        remove => vaultAccess.AutoLocked -= value;
    }

    public void ConfigureAutoLock(int minutes) =>
        vaultSession.InactivityTimeout = minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;

    public void ConfigureInactivityTimeout(TimeSpan? timeout) =>
        vaultSession.InactivityTimeout = timeout;

    public void RecordActivity() => vaultSession.RecordActivity();

    public void Panic() => LockVault();

    public void LockVault() => vaultAccess.LockVault();

    public string VaultName => galleryLifetime.Current.VaultStore.Name;

    public IReadOnlyList<GalleryLocation> KnownGalleries => vaultLocations.KnownGalleries;

    public Task OpenExistingVaultAsync(string directory) => vaultLocations.OpenExistingVaultAsync(directory);

    public Task SelectNewVaultAsync(string directory) => vaultLocations.SelectNewVaultAsync(directory);

    public Task CloseVaultAsync() => vaultLocations.CloseVaultAsync();

    public Task RenameVaultAsync(string name) => vaultManager.RenameVaultAsync(name);

    public Task MoveVaultAsync(string directory) => vaultLocations.MoveVaultAsync(directory);

    public Task PurgeVaultAsync(string confirmation) => vaultLocations.PurgeVaultAsync(confirmation);
}
