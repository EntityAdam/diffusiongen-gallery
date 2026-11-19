using Gallery.Core.Features.Backups.Targets;

namespace Gallery.Core.Features.Backups;

/// <summary>Enforces traversal rules shared by incremental backup validation and restore.</summary>
internal sealed class BackupChain(IBackupTarget target)
{
    private const int MaximumChainLength = 10_000;
    private readonly HashSet<string> visited = new(StringComparer.Ordinal);

    internal void Visit(string archiveName, string invalidChainMessage)
    {
        target.ValidateArchiveName(archiveName);
        if (!visited.Add(archiveName) || visited.Count > MaximumChainLength)
            throw new InvalidOperationException(invalidChainMessage);
    }

    internal static void ValidateManifest(BackupArchive.Manifest manifest, string invalidManifestMessage)
    {
        if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.BackupId) || manifest.Media is null)
            throw new InvalidOperationException(invalidManifestMessage);
    }

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
