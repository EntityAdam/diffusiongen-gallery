using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class RemovalRules
{
    public static bool CanRemove(ImageRecord imageRecord) => imageRecord.IsMarkedForDeletion;

    public static bool ShouldDeleteOriginal(RemovalItem removalItem, ExistingOriginalAction action) =>
        removalItem.OriginalExists && action == ExistingOriginalAction.Delete;

    public static bool ShouldRestoreOriginal(RemovalItem removalItem, MissingOriginalAction action) =>
        !removalItem.OriginalExists && action == MissingOriginalAction.RestorePng;
}
