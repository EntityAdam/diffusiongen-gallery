using Gallery.Core.Models;

namespace Gallery.Core.Services.Validators;

internal static class StoredImageMoveValidator
{
    public static void ValidateDestination(StoredImageMoveDestination storedImageMoveDestination)
    {
        ValidateName(storedImageMoveDestination.Name);
        if (!Directory.Exists(storedImageMoveDestination.Folder))
            throw new DirectoryNotFoundException("Choose an existing destination folder.");
    }

    public static void ValidateName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' ') || name.Contains('/') || name.Contains('\\')
            || name.StartsWith('.') && !name[1..].Contains('.'))
            throw new InvalidOperationException($"Invalid filename '{name}'. Enter a valid filename without a path or extension.");
    }

    public static void ValidateDifferentPath(string source, string target)
    {
        if (string.Equals(source, target,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Choose a different name or destination.");
    }
}
