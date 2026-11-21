using System.Security.Cryptography;
using Gallery.Core.Features.Settings;
using Gallery.Core.Features.Vaults.Publication;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Vaults;

internal sealed class VaultManager(GalleryLifetime galleryLifetime, VaultSession vaultSession)
{
    public async Task<string> CreateVaultAsync(
        string passphrase,
        string confirmation,
        string? name,
        MediaStorageOptions? storageOptions)
    {
        ValidateCredentialConfirmation(passphrase, confirmation);
        ValidatePassphrase(passphrase);
        var galleryName = NormalizeGalleryName(name ?? VaultStore.DefaultGalleryName);
        if (storageOptions is not null)
            storageOptions = GallerySettings.NormalizeMediaStorageOptions(storageOptions);

        await galleryLifetime.Gate.WaitAsync();
        try
        {
            var vaultRoot = galleryLifetime.Current.VaultStore.Root;
            CatalogDirectoryRules.RequireLocalCatalogDirectory(vaultRoot);
            EnsureCheckout();
            VaultCreationPublication.RecoverInterruptedCreation(vaultRoot);
            EnsureVaultDoesNotExist(vaultRoot);
            return await CreateAndPublishVaultAsync(vaultRoot, passphrase, galleryName, storageOptions);
        }
        finally { galleryLifetime.Gate.Release(); }
    }

    public async Task ChangePassphraseAsync(string passphrase, string confirmation)
    {
        ValidateCredentialConfirmation(passphrase, confirmation);
        ValidatePassphrase(passphrase);
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryLifetime.Current.VaultStore.ChangePassphraseAsync(keyLease.Bytes, passphrase);
    }

    public async Task RenameVaultAsync(string name)
    {
        var galleryName = NormalizeGalleryName(name);
        await galleryLifetime.Gate.WaitAsync();
        try
        {
            using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
            EnsureCheckout();
            await galleryLifetime.Current.VaultStore.RenameVaultAsync(galleryName);
        }
        finally { galleryLifetime.Gate.Release(); }
    }

    private async Task<string> CreateAndPublishVaultAsync(
        string vaultRoot,
        string passphrase,
        string galleryName,
        MediaStorageOptions? storageOptions)
    {
        var staging = VaultCreationPublication.CreateStagingDirectory(vaultRoot);
        byte[]? createdKey = null;
        try
        {
            var stagedVaultStore = new VaultStore(staging);
            var createdVault = await stagedVaultStore.CreateVaultAsync(passphrase, galleryName, galleryLifetime.StoreFactory.NewCatalogBackend);
            createdKey = createdVault.Key;
            await InitializeStagedCatalogAsync(staging, storageOptions, createdKey);
            EnsureActive();
            SaveVaultHistory(vaultRoot);
            vaultSession.PublishUnlock(createdKey, () =>
            {
                VaultCreationPublication.Prepare(staging, vaultRoot);
                var catalogPath = Path.Combine(vaultRoot, GalleryStoreFactory.CatalogFileName);
                try
                {
                    MoveCreatedVaultFiles(staging, vaultRoot, catalogPath);
                }
                catch
                {
                    VaultCreationPublication.RecoverInterruptedCreation(vaultRoot);
                    throw;
                }
                VaultCreationPublication.RemoveReceipt(vaultRoot);
            });
            return createdVault.Recovery;
        }
        finally
        {
            if (createdKey is not null) CryptographicOperations.ZeroMemory(createdKey);
            if (!VaultCreationPublication.HasReceipt(vaultRoot))
                VaultCreationPublication.DeleteStaging(staging);
        }
    }

    private void EnsureVaultDoesNotExist(string vaultRoot)
    {
        if (galleryLifetime.Current.VaultStore.Exists)
            throw new InvalidOperationException("A gallery already exists. Unlock it instead.");
        foreach (var fileName in new[]
            {
                GalleryStoreFactory.CatalogFileName,
                GalleryStoreFactory.CatalogFileName + "-wal",
                GalleryStoreFactory.CatalogFileName + "-shm"
            })
        {
            var path = Path.Combine(vaultRoot, fileName);
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("A new gallery cannot replace an existing catalog or its journal files.");
        }
    }

    private async Task InitializeStagedCatalogAsync(
        string staging, MediaStorageOptions? storageOptions, byte[] createdKey)
    {
        var stagedStore = galleryLifetime.StoreFactory.Create(staging);
        await stagedStore.InitializeAsync(createdKey);
        if (storageOptions is not null)
            await stagedStore.SaveSettingsAsync(storageOptions, MediaStorageOptions.SettingId, MediaStorageOptions.SettingPurpose, createdKey);
        await stagedStore.PrepareForPublicationAsync(createdKey);
    }

    private void SaveVaultHistory(string vaultRoot) => galleryLifetime.History?.Save(vaultRoot);

    private static void MoveCreatedVaultFiles(string staging, string vaultRoot, string catalogPath)
    {
        File.Move(Path.Combine(staging, GalleryStoreFactory.CatalogFileName), catalogPath, overwrite: false);
        File.Move(Path.Combine(staging, VaultStore.HeaderFileName),
            Path.Combine(vaultRoot, VaultStore.HeaderFileName), overwrite: false);
    }

    private void EnsureActive() =>
        ObjectDisposedException.ThrowIf(galleryLifetime.IsDisposed, galleryLifetime);

    private void EnsureCheckout() => galleryLifetime.EnsureCheckout();

    private static void ValidateCredentialConfirmation(string passphrase, string confirmation)
    {
        if (passphrase != confirmation)
            throw new InvalidOperationException("Passphrases do not match.");
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (!IsAcceptablePassphrase(passphrase))
            throw new InvalidOperationException("Use a passphrase of at least 14 characters; a long unique phrase is recommended.");
    }

    private static bool IsAcceptablePassphrase(string passphrase) =>
        !string.IsNullOrWhiteSpace(passphrase) && passphrase.Length >= 14;

    private static string NormalizeGalleryName(string galleryName)
    {
        var normalizedGalleryName = galleryName.Trim();
        if (!IsValidGalleryName(normalizedGalleryName))
            throw new InvalidOperationException("Gallery names must have 1 to 80 characters and no control characters.");
        return normalizedGalleryName;
    }

    private static bool IsValidGalleryName(string galleryName) =>
        galleryName.Length is >= 1 and <= 80 && !galleryName.Any(char.IsControl);
}
