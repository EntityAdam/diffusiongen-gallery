using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<MediaSet?> SuggestDestinationSetAsync(string collectionId, IEnumerable<string> mediaIds) =>
        visionWorkflow.SuggestDestinationSetAsync(collectionId, mediaIds);

    public Task<CollectionCatalog> LoadCollectionCatalogAsync() => collectionOrganizer.LoadCollectionCatalogAsync();

    public Task<CollectionDefinition> CreateCollectionAsync(string name) => collectionOrganizer.CreateCollectionAsync(name);

    public Task<MediaSet> CreateSetAsync(string collectionId, string name) => collectionOrganizer.CreateSetAsync(collectionId, name);

    public Task<CollectionMutation> RenameCollectionAsync(string id, string name) => collectionOrganizer.RenameCollectionAsync(id, name);

    public Task<CollectionMutation> RenameSetAsync(string id, string name) => collectionOrganizer.RenameSetAsync(id, name);

    public Task<CollectionMutation> MoveMediaToSetAsync(IEnumerable<string> mediaIds, string? setId) =>
        collectionOrganizer.MoveMediaToSetAsync(mediaIds, setId);

    public Task<CollectionMutation> MoveMediaToDestinationAsync(IEnumerable<string> mediaIds, string collectionName,
        string setName, string? collectionId = null, string? setId = null) =>
        collectionOrganizer.MoveMediaToDestinationAsync(mediaIds, collectionName, setName, collectionId, setId);

    public Task<CollectionMutation> DeleteContainerAsync(string id, bool isSet) => collectionOrganizer.DeleteContainerAsync(id, isSet);

    public Task<CollectionMutation> MoveSetAsync(string setId, string collectionId) => collectionOrganizer.MoveSetAsync(setId, collectionId);

    public Task<CollectionMutation> ReorderSetAsync(string id, int offset) => collectionOrganizer.ReorderSetAsync(id, offset);

    public Task<CollectionMutation> ReorderMediaAsync(string setId, string mediaId, int offset) =>
        collectionOrganizer.ReorderMediaAsync(setId, mediaId, offset);

    public Task UndoCollectionChangeAsync(CollectionUndo undo) => collectionOrganizer.UndoCollectionChangeAsync(undo);
}
