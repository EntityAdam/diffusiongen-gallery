using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Selects and manages vaults and controls access to the active vault.</summary>
public interface IGalleryVault
{
    bool CatalogIsOnNetworkDrive { get; }
    Task RelocateCatalogAsync(string directory);
    bool IsUnlocked { get; }
    bool VaultExists { get; }
    string VaultRoot { get; }
    Task<string> CreateVaultAsync(string passphrase, string confirmation, string? name = null,
        MediaStorageOptions? storageOptions = null);
    Task UnlockWithPassphraseAsync(string passphrase);
    Task UnlockWithRecoveryPhraseAsync(string recoveryPhrase);
    Task ChangePassphraseAsync(string passphrase, string confirmation);
    event Action? OnAutoLocked;
    void ConfigureAutoLock(int minutes);
    void ConfigureInactivityTimeout(TimeSpan? timeout);
    void RecordActivity();
    void Panic();
    void LockVault();
    string VaultName { get; }
    IReadOnlyList<GalleryLocation> KnownGalleries { get; }
    Task OpenExistingVaultAsync(string directory);
    Task SelectNewVaultAsync(string directory);
    Task CloseVaultAsync();
    Task RenameVaultAsync(string name);
    Task MoveVaultAsync(string directory);
    Task PurgeVaultAsync(string confirmation);
}
