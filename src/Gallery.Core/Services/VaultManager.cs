using System.Security.Cryptography;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Sessions;

namespace Gallery.Core.Services;

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
        var galleryName = NormalizeGalleryName(name ?? GalleryService.GenerateVaultName());
        if (storageOptions is not null)
            storageOptions = GalleryService.NormalizeMediaStorageOptions(storageOptions);

        await galleryLifetime.Gate.WaitAsync();
        try
        {
            var vaultRoot = galleryLifetime.Current.VaultStore.Root;
            GalleryService.RequireLocalCatalogDirectory(vaultRoot);
            EnsureCheckout();
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
            await galleryLifetime.Current.VaultStore.RenameAsync(galleryName);
        }
        finally { galleryLifetime.Gate.Release(); }
    }

    private async Task<string> CreateAndPublishVaultAsync(
        string vaultRoot,
        string passphrase,
        string galleryName,
        MediaStorageOptions? storageOptions)
    {
        var staging = CreateStagingDirectory(vaultRoot);
        byte[]? createdKey = null;
        try
        {
            var stagedVaultStore = new VaultStore(staging);
            var createdVault = await stagedVaultStore.CreateAsync(passphrase, galleryName);
            createdKey = createdVault.Key;
            await InitializeStagedCatalogAsync(staging, storageOptions, createdKey);
            EnsureActive();
            SaveVaultHistory(vaultRoot);
            using (var activation = vaultSession.PrepareUnlock(createdKey))
            {
                PublishCreatedVault(staging, vaultRoot);
                activation.Commit();
            }
            return createdVault.Recovery;
        }
        finally
        {
            if (createdKey is not null) CryptographicOperations.ZeroMemory(createdKey);
            DeleteCreationStaging(staging);
        }
    }

    private void EnsureVaultDoesNotExist(string vaultRoot)
    {
        if (galleryLifetime.Current.VaultStore.Exists)
            throw new InvalidOperationException("A gallery already exists. Unlock it instead.");
        foreach (var fileName in new[]
            {
                GalleryService.CatalogFileName,
                GalleryService.CatalogFileName + "-wal",
                GalleryService.CatalogFileName + "-shm"
            })
        {
            var path = Path.Combine(vaultRoot, fileName);
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("A new gallery cannot replace an existing catalog or its journal files.");
        }
    }

    private static string CreateStagingDirectory(string vaultRoot) =>
        Path.Combine(vaultRoot, ".vault-create-" + Guid.NewGuid().ToString("N"));

    private static async Task InitializeStagedCatalogAsync(
        string staging, MediaStorageOptions? storageOptions, byte[] createdKey)
    {
        var stagedStore = new GalleryStore(Path.Combine(staging, GalleryService.CatalogFileName));
        await stagedStore.InitializeAsync();
        if (storageOptions is not null)
            await stagedStore.SaveSettingsAsync(storageOptions, "media-storage", "media-storage:v1", createdKey);
    }

    private void SaveVaultHistory(string vaultRoot) => galleryLifetime.History?.Save(vaultRoot);

    private static void PublishCreatedVault(string staging, string vaultRoot)
    {
        var catalogPath = Path.Combine(vaultRoot, GalleryService.CatalogFileName);
        File.Move(Path.Combine(staging, GalleryService.CatalogFileName), catalogPath, overwrite: false);
        try
        {
            File.Move(Path.Combine(staging, GalleryService.VaultHeaderFileName),
                Path.Combine(vaultRoot, GalleryService.VaultHeaderFileName), overwrite: false);
        }
        catch
        {
            File.Move(catalogPath, Path.Combine(staging, GalleryService.CatalogFileName), overwrite: false);
            throw;
        }
    }

    private static void DeleteCreationStaging(string staging)
    {
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 14)
            throw new InvalidOperationException("Use a passphrase of at least 14 characters; a long unique phrase is recommended.");
    }

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
