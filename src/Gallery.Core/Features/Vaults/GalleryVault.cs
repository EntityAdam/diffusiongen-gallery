using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Models;
using Gallery.Core.Services;
using GalleryLocation = Gallery.Core.Models.GalleryLocation;

namespace Gallery.Core.Features.Vaults;

internal sealed class GalleryVault(GalleryLifetime galleryLifetime, VaultSession vaultSession, VaultAccess vaultAccess,
    VaultManager vaultManager, VaultLocationCoordinator vaultLocations) : IGalleryVault
{
    private static void GuardIngestionProgressCallback(string operation) =>
        IngestionProgressCallbackGuard.ThrowIfActive(operation);

    public bool CatalogIsOnNetworkDrive => CatalogDirectoryRules.IsNetworkDirectory(VaultRoot);

    /// <summary>Relocates only the encrypted catalog and header; all encrypted media stays at its existing paths.</summary>

    public Task RelocateCatalogAsync(string directory)
    {
        GuardIngestionProgressCallback("relocate the catalog");
        return vaultLocations.RelocateCatalogAsync(directory);
    }

    public bool IsUnlocked => vaultSession.IsUnlocked;

    public bool VaultExists => galleryLifetime.Current.VaultStore.Exists;

    public string VaultRoot => galleryLifetime.Current.VaultStore.Root;

    public Task<string> CreateVaultAsync(string passphrase, string confirmation, string? name = null,
     MediaStorageOptions? storageOptions = null)
    {
        GuardIngestionProgressCallback("create a vault");
        return vaultManager.CreateVaultAsync(passphrase, confirmation, name, storageOptions);
    }

    public Task UnlockWithPassphraseAsync(string passphrase) => vaultAccess.UnlockWithPassphraseAsync(passphrase);

    public Task UnlockWithRecoveryPhraseAsync(string recoveryPhrase) => vaultAccess.UnlockWithRecoveryPhraseAsync(recoveryPhrase);

    public Task ChangePassphraseAsync(string passphrase, string confirmation)
    {
        GuardIngestionProgressCallback("change the passphrase");
        return vaultManager.ChangePassphraseAsync(passphrase, confirmation);
    }

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

    public void LockVault()
    {
        GuardIngestionProgressCallback("lock the vault");
        vaultAccess.LockVault();
    }

    public string VaultName => galleryLifetime.Current.VaultStore.Name;

    public IReadOnlyList<GalleryLocation> KnownGalleries => vaultLocations.KnownGalleries;

    public Task OpenExistingVaultAsync(string directory)
    {
        GuardIngestionProgressCallback("open a vault");
        return vaultLocations.OpenExistingVaultAsync(directory);
    }

    public Task SelectNewVaultAsync(string directory)
    {
        GuardIngestionProgressCallback("select a new vault location");
        return vaultLocations.SelectNewVaultAsync(directory);
    }

    public Task CloseVaultAsync()
    {
        GuardIngestionProgressCallback("close the vault");
        return vaultLocations.CloseVaultAsync();
    }

    public Task RenameVaultAsync(string name)
    {
        GuardIngestionProgressCallback("rename the vault");
        return vaultManager.RenameVaultAsync(name);
    }

    public Task MoveVaultAsync(string directory)
    {
        GuardIngestionProgressCallback("move the vault");
        return vaultLocations.MoveVaultAsync(directory);
    }

    public Task PurgeVaultAsync(string confirmation)
    {
        GuardIngestionProgressCallback("purge the vault");
        return vaultLocations.PurgeVaultAsync(confirmation);
    }
}
