using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class StoredImageMover
{
    private const string EncryptedPngExtension = ".dpng";
    private const string EncryptedMp4Extension = ".dmp4";

    public static StoredImageMoveDestination NormalizeDestination(string folder, string name) =>
        new(Path.GetFullPath(folder), NormalizeName(name));

    private static string NormalizeName(string name)
        => Path.GetFileNameWithoutExtension(name.Trim());

    public static string TargetPath(ImageRecord imageRecord, StoredImageMoveDestination storedImageMoveDestination) =>
        Path.Combine(storedImageMoveDestination.Folder, storedImageMoveDestination.Name + (imageRecord.IsVideo ? EncryptedMp4Extension : EncryptedPngExtension));

    public static ImageRecord CopyToDestination(ImageRecord imageRecord, string target, string name)
    {
        // The old ciphertext remains until the catalog update succeeds.
        File.Copy(imageRecord.StoredPath, target, overwrite: false);
        return imageRecord with { StoredPath = target, Name = name };
    }

    public static void RollBackCopiedDestination(string target) => File.Delete(target);
}
