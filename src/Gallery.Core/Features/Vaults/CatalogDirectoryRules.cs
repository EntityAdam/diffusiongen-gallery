using Gallery.Core.Shared.Files;

namespace Gallery.Core.Features.Vaults;

internal static class CatalogDirectoryRules
{
    public static bool IsNetworkDirectory(string directory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory))!;
        return root.StartsWith(@"\\", StringComparison.Ordinal)
            || OperatingSystem.IsWindows() && new DriveInfo(root).DriveType == DriveType.Network;
    }

    internal static string NormalizeVaultDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
        if (StorageFileSystem.PathEquals(root, Path.GetPathRoot(root)!))
            throw new InvalidOperationException("A filesystem or share root cannot be a gallery. Select a dedicated folder.");
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Gallery directories cannot use symbolic links or directory junctions.");
        return root;
    }

    internal static void RequireLocalCatalogDirectory(string directory)
    {
        if (CatalogDirectoryRules.IsNetworkDirectory(directory))
            throw new InvalidOperationException(
                $"Catalog directory '{directory}' is on a network drive. Keep gallery.db on the host's local drive. Choose a local catalog directory; encrypted media may use a NAS/SMB directory.");
    }
}
