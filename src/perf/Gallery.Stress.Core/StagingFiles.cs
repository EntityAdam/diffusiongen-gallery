namespace Gallery.Stress.Core;

public static class StagingFiles
{
    // Finder may create this file while an operator inspects a running batch.
    // Do not ignore arbitrary hidden files: an unexpected source must still block completion.
    public static bool IsFinderMetadata(string path) =>
        Path.GetFileName(path).Equals(".DS_Store", StringComparison.Ordinal) && File.Exists(path);

    public static bool HasRemainingSources(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(path => !IsFinderMetadata(path));

    public static bool CanInitialize(string directory) =>
        !Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory)
            .All(path => IsFinderMetadata(path)
                || File.Exists(path) && Path.GetFileName(path) == "stress-staging.marker");
}
