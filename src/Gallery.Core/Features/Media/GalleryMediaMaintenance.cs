using Gallery.Core.Features.Media.Movement;
using Gallery.Core.Features.Media.Removal;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Media;

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
