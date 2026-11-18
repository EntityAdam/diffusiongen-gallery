namespace Gallery.Core.Services;

/// <summary>Lists backups and restores a selected backup to a vault.</summary>
public interface IGalleryRestore
{
    Task<IReadOnlyList<string>> ListRestoreBackupsAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix, string connectionString);
    Task<IReadOnlyList<string>> ListRestoreBackupsFromSasAsync(string sasUrl, string azurePrefix = "");
    Task RestoreVaultAsync(
        string sourceType, string localDirectory, string azureContainer, string azurePrefix,
        string connectionString, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null);
    Task RestoreGalleryFromSasAsync(
        string sasUrl, string latestBackupName, string destinationDirectory,
        string credential, bool useRecovery = false, string? name = null, string azurePrefix = "");
}
