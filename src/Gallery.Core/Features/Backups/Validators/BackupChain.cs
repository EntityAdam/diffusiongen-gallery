using Gallery.Core.Features.Backups.Targets;

namespace Gallery.Core.Features.Backups;

/// <summary>Enforces traversal rules shared by incremental backup validation and restore.</summary>
internal static class BackupChain
{
    internal const int MaximumChainLength = 10_000;

    internal static int Visit(
        IBackupTarget target, string archiveName, HashSet<string> visited, string invalidChainMessage)
    {
        target.ValidateArchiveName(archiveName);
        if (!visited.Add(archiveName) || visited.Count > MaximumChainLength)
            throw new InvalidOperationException(invalidChainMessage);
        return visited.Count;
    }

    internal static void ValidateManifest(BackupArchive.Manifest manifest, string invalidManifestMessage)
    {
        if (!IsSupportedManifest(manifest))
            throw new InvalidOperationException(invalidManifestMessage);
    }

    private static bool IsSupportedManifest(BackupArchive.Manifest manifest) =>
        manifest.FormatVersion == 1
        && !string.IsNullOrWhiteSpace(manifest.BackupId)
        && manifest.Media is not null;

    internal static bool CanAppendIncremental(int existingChainLength) =>
        existingChainLength < MaximumChainLength;

    internal static string? ReadParentName(BackupArchive.Manifest manifest, string invalidFullMessage, string invalidIncrementalMessage)
    {
        if (manifest.BackupType == "full")
        {
            if (manifest.ParentBackupName is not null) throw new InvalidOperationException(invalidFullMessage);
            return null;
        }

        if (manifest.BackupType != "incremental" || string.IsNullOrWhiteSpace(manifest.ParentBackupName))
            throw new InvalidOperationException(invalidIncrementalMessage);
        return manifest.ParentBackupName;
    }
}
