using Gallery.Web.Models;

namespace Gallery.Web.Services.Validators;

internal static class StoredImageMoveValidator
{
    public static void ValidateDestination(StoredImageMoveDestination destination)
    {
        ValidateName(destination.Name);
        if (!Directory.Exists(destination.Folder))
            throw new DirectoryNotFoundException("Choose an existing destination folder.");
    }

    public static void ValidateName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' ') || name.Contains('/') || name.Contains('\\'))
            throw new InvalidOperationException($"Invalid filename '{name}'. Enter a valid filename without a path or extension.");
    }

    public static void ValidateDifferentPath(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a different name or destination.");
    }
}
