using Gallery.Core.Models;

namespace Gallery.Core.Services;

internal sealed class GalleryMediaMaintenance(RemovalManager removalManager, StoredMediaMover storedMediaMover) : IGalleryMediaMaintenance
{
    public Task MoveAsync(string id, string folder, string name) => storedMediaMover.MoveAsync(id, folder, name);

    public Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids) =>
        removalManager.PrepareRemovalAsync(ids);

    public Task RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval = null) =>
        removalManager.RemoveAsync(removalItems, existingAction, missingAction, restoreFolder, reportRemoval);
}
