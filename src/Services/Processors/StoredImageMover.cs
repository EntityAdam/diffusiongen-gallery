using Gallery.Models;

namespace Gallery.Services.Processors;

internal static class StoredImageMover
{
    private const string EncryptedPngExtension = ".dpng";
    private const string EncryptedMp4Extension = ".dmp4";

    public static StoredImageMoveDestination NormalizeDestination(string folder, string name) =>
        new(Path.GetFullPath(folder), NormalizeName(name));

    private static string NormalizeName(string name)
    {
        var normalizedName = name.Trim();
        if (normalizedName.EndsWith(EncryptedPngExtension, StringComparison.OrdinalIgnoreCase)
            || normalizedName.EndsWith(EncryptedMp4Extension, StringComparison.OrdinalIgnoreCase))
            normalizedName = normalizedName[..^5];
        return normalizedName;
    }

    public static string TargetPath(ImageRecord record, StoredImageMoveDestination destination) =>
        Path.Combine(destination.Folder, destination.Name + (record.IsVideo ? EncryptedMp4Extension : EncryptedPngExtension));

    public static ImageRecord CopyToDestination(ImageRecord record, string target, string name)
    {
        // The old ciphertext remains until the catalog update succeeds.
        File.Copy(record.StoredPath, target, overwrite: false);
        return record with { StoredPath = target, Name = name };
    }

    public static void RollBackCopiedDestination(string target) => File.Delete(target);

    public static void RemoveOldCopy(string source, string target)
    {
        try { File.Delete(source); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The image was moved to '{target}', but the old encrypted copy at '{source}' could not be removed. Remove that extra copy after checking the destination.",
                exception);
        }
    }
}
