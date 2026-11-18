using System.Security.Cryptography;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Sessions.Locations;
using Gallery.Core.Services.Validators;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<IReadOnlyList<string>> ListRestoreBackupsAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString) =>
        CreateRestoreTarget(sourceType, localDirectory, azureContainer, azurePrefix, connectionString).ListAsync();

    public Task<IReadOnlyList<string>> ListRestoreBackupsFromSasAsync(string sasUrl, string azurePrefix = "") =>
        CreateSasRestoreTarget(sasUrl, azurePrefix).ListAsync();

    private IBackupTarget CreateSasRestoreTarget(string sasUrl, string azurePrefix) =>
        new BackupTargetAzure(sasUrl, NormalizeAzurePrefix(azurePrefix), ownedHttpClient);

    private IBackupTarget CreateRestoreTarget(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString)
    {
        if (sourceType == "local")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
            return new BackupTargetLocal(Path.GetFullPath(localDirectory.Trim()));
        }
        if (sourceType == "azure")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(azureContainer);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            return new BackupTargetAzure(connectionString.Trim(), azureContainer.Trim(), NormalizeAzurePrefix(azurePrefix), ownedHttpClient);
        }
        throw new InvalidOperationException("Choose a local/SMB folder or Azure Blob Storage.");
    }

    public async Task RestoreVaultAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix,
        string connectionString, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null)
    {
        var target = CreateRestoreTarget(sourceType, localDirectory, azureContainer, azurePrefix, connectionString);
        await RestoreVaultFromTargetAsync(target, latestBackupName, destinationDirectory, credential, useRecovery,
            name, sourceType == "local" ? localDirectory : null);
    }

    public Task RestoreGalleryFromSasAsync(
        string sasUrl, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null, string azurePrefix = "") =>
        RestoreVaultFromTargetAsync(CreateSasRestoreTarget(sasUrl, azurePrefix), latestBackupName, destinationDirectory,
            credential, useRecovery, name, sourceDirectory: null);

    private async Task RestoreVaultFromTargetAsync(
        IBackupTarget target, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery, string? name, string? sourceDirectory)
    {
        target.ValidateArchiveName(latestBackupName);
        if (useRecovery) credential = RecoveryPhraseValidator.Normalize(credential);
        var destination = NormalizeVaultDirectory(destinationDirectory);
        RequireLocalCatalogDirectory(destination);
        await lifecycleGate.WaitAsync();
        VaultCheckout? destinationCheckout = null;
        byte[]? key = null;
        string? staging = null;
        try
        {
            EnsureActive();
            RejectOverlappingDirectories(destination, VaultRoot);
            if (sourceDirectory is not null)
                RejectOverlappingDirectories(destination, Path.GetFullPath(sourceDirectory.Trim()));
            RequireEmptyDestination(destination);
            destinationCheckout = galleryLifetime.AcquireLocationCheckout(destination);
            RequireEmptyDestination(destination);
            staging = Path.Combine(destination, ".vault-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            IGalleryStore store;
            (store, key) = await BackupArchive.StageRestoreAsync(
                galleryLifetime.StoreFactory, target, latestBackupName, staging, destination, credential, useRecovery);
            await ClearLocationSettingsAsync(store, key);
            if (name is not null) await new VaultStore(staging).RenameAsync(name);
            await StopDriveOperationPlayerAsync();
            await vaultSession.LockAndDrainAsync();
            EnsureActive();
            await store.PrepareForPublicationAsync(key);
            PublishStagedVault(staging, destination);
            galleryLifetime.PublishLocation(galleryLifetime.StoreFactory.Create(destination),
                new VaultStore(destination), destinationCheckout)?.Dispose();
            destinationCheckout = null;
            history?.Save(destination);
            // Restore returns to regular sign-in; credentials and plaintext never survive the operation.
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            destinationCheckout?.Dispose();
            try { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            finally { lifecycleGate.Release(); }
        }
    }
}
