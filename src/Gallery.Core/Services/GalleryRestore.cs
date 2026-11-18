using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Ports;

namespace Gallery.Core.Services;

internal sealed class GalleryRestore(VaultLocationCoordinator vaultLocations, HttpClient? httpClient) : IGalleryRestore
{
    public Task<IReadOnlyList<string>> ListRestoreBackupsAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString) =>
        CreateRestoreTarget(sourceType, localDirectory, azureContainer, azurePrefix, connectionString).ListAsync();

    public Task<IReadOnlyList<string>> ListRestoreBackupsFromSasAsync(string sasUrl, string azurePrefix = "") =>
        CreateSasRestoreTarget(sasUrl, azurePrefix).ListAsync();

    private IBackupTarget CreateSasRestoreTarget(string sasUrl, string azurePrefix) =>
        new BackupTargetAzure(sasUrl, BackupTargetAzure.NormalizePrefix(azurePrefix), httpClient);

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
            return new BackupTargetAzure(connectionString.Trim(), azureContainer.Trim(), BackupTargetAzure.NormalizePrefix(azurePrefix), httpClient);
        }
        throw new InvalidOperationException("Choose a local/SMB folder or Azure Blob Storage.");
    }

    public async Task RestoreVaultAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix,
        string connectionString, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null)
    {
        var target = CreateRestoreTarget(sourceType, localDirectory, azureContainer, azurePrefix, connectionString);
        await vaultLocations.RestoreVaultFromTargetAsync(target, latestBackupName, destinationDirectory, credential, useRecovery,
            name, sourceType == "local" ? localDirectory : null);
    }

    public Task RestoreGalleryFromSasAsync(
        string sasUrl, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null, string azurePrefix = "") =>
        vaultLocations.RestoreVaultFromTargetAsync(CreateSasRestoreTarget(sasUrl, azurePrefix), latestBackupName, destinationDirectory,
            credential, useRecovery, name, sourceDirectory: null);
}
