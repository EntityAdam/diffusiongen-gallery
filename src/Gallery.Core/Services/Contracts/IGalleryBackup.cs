using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Configures backup destinations and writes encrypted full or incremental backups.</summary>
public interface IGalleryBackup
{
    Task<BackupOptions> LoadBackupOptionsAsync();
    Task<BackupOptions> SaveBackupOptionsAsync(
        string destinationType, string localDirectory, string azureContainer,
        string azurePrefix, string azureConnectionString);
    Task<BackupRunResult> CreateFullBackupAsync();
    Task<BackupRunResult> CreateIncrementalBackupAsync();
}
