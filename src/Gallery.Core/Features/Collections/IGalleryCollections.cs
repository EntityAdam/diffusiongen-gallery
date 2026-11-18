using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Manages collection and set hierarchy, media placement, and undo.</summary>
public interface IGalleryCollections
{
    Task<CollectionCatalog> LoadCollectionCatalogAsync();
    Task<CollectionDefinition> CreateCollectionAsync(string name);
    Task<MediaSet> CreateSetAsync(string collectionId, string name);
    Task<CollectionMutation> RenameCollectionAsync(string id, string name);
    Task<CollectionMutation> RenameSetAsync(string id, string name);
    Task<CollectionMutation> MoveMediaToSetAsync(IEnumerable<string> mediaIds, string? setId);
    Task<CollectionMutation> MoveMediaToDestinationAsync(IEnumerable<string> mediaIds, string collectionName,
        string setName, string? collectionId = null, string? setId = null);
    Task<CollectionMutation> DeleteContainerAsync(string id, bool isSet);
    Task<CollectionMutation> MoveSetAsync(string setId, string collectionId);
    Task<CollectionMutation> ReorderSetAsync(string id, int offset);
    Task<CollectionMutation> ReorderMediaAsync(string setId, string mediaId, int offset);
    Task UndoCollectionChangeAsync(CollectionUndo undo);
}
