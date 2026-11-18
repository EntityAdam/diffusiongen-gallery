using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public const string BackupActivitySourceName = BackupArchive.ActivitySourceName;

    public Task<BackupOptions> LoadBackupOptionsAsync() => backupWriter.LoadBackupOptionsAsync();

    public Task<BackupOptions> SaveBackupOptionsAsync(
        string destinationType, string localDirectory, string azureContainer,
        string azurePrefix, string azureConnectionString) =>
        backupWriter.SaveBackupOptionsAsync(destinationType, localDirectory, azureContainer, azurePrefix, azureConnectionString);

    public Task<BackupRunResult> CreateFullBackupAsync() => backupWriter.CreateFullBackupAsync();

    public Task<BackupRunResult> CreateIncrementalBackupAsync() => backupWriter.CreateIncrementalBackupAsync();
}
