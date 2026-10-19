namespace Gallery.Core.Services.Gateways.Validators;

internal static class BackupArchiveNameValidator
{
    public static void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.IndexOfAny(['/', '\\', ':']) >= 0 || Path.GetFileName(name) != name)
            throw new InvalidOperationException("Backup names must be filenames, not paths.");
    }

}
