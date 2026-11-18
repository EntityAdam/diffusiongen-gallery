namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<IReadOnlyList<string>> ListRestoreBackupsAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString) =>
        vaultLocations.ListRestoreBackupsAsync(sourceType, localDirectory, azureContainer, azurePrefix, connectionString, ownedHttpClient);

    public Task<IReadOnlyList<string>> ListRestoreBackupsFromSasAsync(string sasUrl, string azurePrefix = "") =>
        vaultLocations.ListRestoreBackupsFromSasAsync(sasUrl, azurePrefix, ownedHttpClient);

    public Task RestoreVaultAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix,
        string connectionString, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null) =>
        vaultLocations.RestoreVaultAsync(sourceType, localDirectory, azureContainer, azurePrefix,
            connectionString, latestBackupName, destinationDirectory, credential, useRecovery, name, ownedHttpClient);

    public Task RestoreGalleryFromSasAsync(
        string sasUrl, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null, string azurePrefix = "") =>
        vaultLocations.RestoreGalleryFromSasAsync(sasUrl, latestBackupName, destinationDirectory,
            credential, useRecovery, name, azurePrefix, ownedHttpClient);
}
