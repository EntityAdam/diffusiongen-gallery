namespace Gallery.Core.Services.Gateways;

internal sealed class BackupTargetLocal(string directory) : IBackupTarget
{
    public void ValidateArchiveName(string name) => Validators.BackupArchiveNameValidator.Validate(name);

    private string PathFor(string name)
    {
        ValidateArchiveName(name);
        return Path.Combine(directory, name);
    }

    public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>(ReadOrderedArchiveNames());

    private string[] ReadOrderedArchiveNames() =>
        Directory.EnumerateFiles(directory, "*.zip").Select(Path.GetFileName).OfType<string>()
            .OrderDescending(StringComparer.Ordinal).ToArray();

    public Task<bool> ExistsAsync(string name) => Task.FromResult(File.Exists(PathFor(name)));

    public Task DownloadAsync(string name, string destinationPath)
    {
        File.Copy(PathFor(name), destinationPath, overwrite: false);
        return Task.CompletedTask;
    }

    public Task UploadAsync(string name, string sourcePath)
    {
        Directory.CreateDirectory(directory);
        var temporaryPath = CreateTemporaryPath(name);
        CopyAndPublish(sourcePath, temporaryPath, PathFor(name));
        return Task.CompletedTask;
    }

    private string CreateTemporaryPath(string name) => PathFor(name + ".partial-" + Guid.NewGuid().ToString("N"));

    private static void CopyAndPublish(string sourcePath, string temporaryPath, string destinationPath)
    {
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
