using Gallery.Core.Features.Backups.Targets.Validators;

namespace Gallery.Core.Features.Backups.Targets;

internal sealed class BackupTargetLocal(string directory) : IBackupTarget
{
    public void ValidateArchiveName(string name) => BackupArchiveNameValidator.Validate(name);

    private string PathFor(string name)
    {
        ValidateArchiveName(name);
        return Path.Combine(directory, name);
    }

    public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>(ReadOrderedArchiveNames());

    private string[] ReadOrderedArchiveNames() =>
        Directory.EnumerateFiles(directory, "*.zip").Select(Path.GetFileName).OfType<string>()
            .Where(BackupArchiveNameValidator.IsValid)
            .OrderDescending(StringComparer.Ordinal).ToArray();

    public Task<bool> ExistsAsync(string name) => Task.FromResult(File.Exists(PathFor(name)));

    public async Task DownloadAsync(string name, string destinationPath)
    {
        await using var source = new FileStream(
            PathFor(name), FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination);
    }

    public async Task UploadAsync(string name, string sourcePath)
    {
        EnsureBackupDirectoryExists();
        var temporaryPath = CreateTemporaryPath(name);
        await CopyAndPublishAsync(sourcePath, temporaryPath, PathFor(name));
    }

    private void EnsureBackupDirectoryExists() => Directory.CreateDirectory(directory);

    private string CreateTemporaryPath(string name) => PathFor(name + ".partial-" + Guid.NewGuid().ToString("N"));

    private static async Task CopyAndPublishAsync(string sourcePath, string temporaryPath, string destinationPath)
    {
        try
        {
            await using (var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var temporary = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(temporary);
                await temporary.FlushAsync();
            }
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
