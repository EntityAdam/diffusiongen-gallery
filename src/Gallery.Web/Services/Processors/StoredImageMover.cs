using Gallery.Web.Models;

namespace Gallery.Web.Services.Processors;

internal static class StoredImageMover
{
    private const string EncryptedPngExtension = ".dpng";
    private const string EncryptedMp4Extension = ".dmp4";

    public static WebStoredImageMoveDestination NormalizeDestination(string folder, string name) =>
        new(Path.GetFullPath(folder), NormalizeName(name));

    private static string NormalizeName(string name)
        => Path.GetFileNameWithoutExtension(name.Trim());

    public static string TargetPath(WebImageRecord record, WebStoredImageMoveDestination destination) =>
        Path.Combine(destination.Folder, destination.Name + (record.IsVideo ? EncryptedMp4Extension : EncryptedPngExtension));

    public static WebImageRecord CopyToDestination(WebImageRecord record, string target, string name)
    {
        // The old ciphertext remains until the catalog update succeeds.
        File.Copy(record.StoredPath, target, overwrite: false);
        return record with { StoredPath = target, Name = name };
    }

    public static void RollBackCopiedDestination(string target) => File.Delete(target);
}
