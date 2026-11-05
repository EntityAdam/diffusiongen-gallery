namespace Gallery.Core.Services.Processors;

internal static class CatalogDirectoryRules
{
    public static bool IsNetworkDirectory(string directory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory))!;
        return root.StartsWith(@"\\", StringComparison.Ordinal)
            || OperatingSystem.IsWindows() && new DriveInfo(root).DriveType == DriveType.Network;
    }
}
