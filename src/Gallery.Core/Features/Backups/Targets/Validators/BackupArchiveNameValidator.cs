namespace Gallery.Core.Features.Backups.Targets.Validators;

internal static class BackupArchiveNameValidator
{
    public static bool IsValid(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not ("." or "..")
        && name.IndexOfAny(['/', '\\', ':']) < 0 && Path.GetFileName(name) == name;

    public static void Validate(string name)
    {
        if (!IsValid(name))
            throw new InvalidOperationException("Backup names must be filenames, not paths.");
    }
}
