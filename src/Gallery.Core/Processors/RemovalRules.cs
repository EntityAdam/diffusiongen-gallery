using Gallery.Core.Models;

namespace Gallery.Core.Processors;

public static class RemovalRules
{
    public static bool CanRemove(ImageRecord image) => image.MarkedForDeletion;

    public static bool ShouldDeleteOriginal(RemovalItem item, ExistingOriginalAction action) =>
        item.OriginalExists && action == ExistingOriginalAction.Delete;

    public static bool ShouldRestoreOriginal(RemovalItem item, MissingOriginalAction action) =>
        !item.OriginalExists && action == MissingOriginalAction.RestorePng;
}
