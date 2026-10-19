namespace Gallery.Core.Services.Gateways;

internal interface IBackupTarget
{
    void ValidateArchiveName(string name);
    Task<IReadOnlyList<string>> ListAsync();
    Task<bool> ExistsAsync(string name);
    Task DownloadAsync(string name, string destinationPath);
    Task UploadAsync(string name, string sourcePath);
}
