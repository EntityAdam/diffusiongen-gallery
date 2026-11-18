using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Moves and removes stored media.</summary>
public interface IGalleryMediaMaintenance
{
    Task MoveAsync(string id, string folder, string name);
    Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids);
    Task RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval = null);
}
