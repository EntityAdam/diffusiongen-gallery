using System.Collections.Immutable;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public async Task<MediaSet?> SuggestDestinationSetAsync(string collectionId, IEnumerable<string> mediaIds)
    {
        var catalog = await LoadCollectionCatalogAsync();
        var collection = RequireCollection(catalog, collectionId);
        var sets = catalog.Sets.Where(set => set.CollectionId == collectionId).ToArray();
        var ids = mediaIds.ToHashSet();
        var images = (await ListAsync()).Where(image => ids.Contains(image.Id)).ToArray();
        if (images.Length == 0 || sets.Length == 0) throw new InvalidOperationException("Choose media and a collection with existing sets first.");
        if (images.All(image => string.IsNullOrWhiteSpace(image.Description) && string.IsNullOrWhiteSpace(image.Tags)))
            throw new InvalidOperationException("Analyze or tag the selected media first so the model has content to use.");
        var settings = await SettingsAsync();
        var suggestedId = await RequiredVisionManager().SuggestSetAsync(settings, collection.Name, sets, images);
        return sets.FirstOrDefault(set => set.Id == suggestedId);
    }

    public async Task<CollectionCatalog> LoadCollectionCatalogAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.Gate.WaitAsync();
        try { return await ReadCollectionsAsync(key.Bytes); }
        finally { galleryStore.Gate.Release(); }
    }

    private async Task<CollectionCatalog> ReadCollectionsAsync(byte[] key)
    {
        var saved = await galleryStore.ReadSettingsAsync<CollectionCatalog>(CollectionCatalog.SettingId, CollectionCatalog.SettingPurpose, key);
        if (saved is not null) return saved;
        var records = await galleryStore.ReadRecordsAsync(key);
        var names = records.SelectMany(image => image.Collections).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var collections = names.Select(name => new CollectionDefinition(Guid.NewGuid().ToString("N"), name, DateTimeOffset.UtcNow)).ToImmutableArray();
        var sets = collections.Select(collection => new MediaSet(Guid.NewGuid().ToString("N"), collection.Id, "Imported media", 0)).ToImmutableArray();
        var migrated = records.Where(image => !image.Collections.IsEmpty).Select((image, index) =>
        {
            var distinct = image.Collections.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (distinct.Length != 1) return image with { LegacyCollections = image.Collections, Collections = [], SetId = null };
            var collection = collections.First(collection => collection.Name.Equals(distinct[0], StringComparison.OrdinalIgnoreCase));
            return image with { SetId = sets.First(set => set.CollectionId == collection.Id).Id, SetOrder = index, Collections = [collection.Name] };
        }).ToArray();
        var catalog = new CollectionCatalog(collections, sets, 1);
        await galleryStore.CommitCollectionCatalogAsync(migrated, catalog, null, key);
        return catalog;
    }

    private static string ContainerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl))
            throw new InvalidOperationException("Use a name of 1–80 characters without control characters.");
        return name.Trim();
    }
    private static void UniqueName(IEnumerable<string> names, string name)
    {
        if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"“{name}” already exists. Choose the existing item or use another name.");
    }
    private static MediaSet RequireSet(CollectionCatalog catalog, string id) => catalog.Sets.FirstOrDefault(set => set.Id == id)
        ?? throw new InvalidOperationException("This set no longer exists. Choose another destination.");
    private static CollectionDefinition RequireCollection(CollectionCatalog catalog, string id) => catalog.Collections.FirstOrDefault(collection => collection.Id == id)
        ?? throw new InvalidOperationException("This collection no longer exists. Refresh and try again.");
    private static MediaPlacement Placement(ImageRecord image) => new(image.Id, image.SetId, image.SetOrder, image.Collections, image.LegacyCollections);

    private async Task<CollectionMutation> MutateCollectionsAsync(Func<CollectionCatalog, List<ImageRecord>, (CollectionCatalog Catalog, ImageRecord[] Records)> change,
        string? renamedCollectionId = null)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.Gate.WaitAsync();
        try
        {
            var before = await ReadCollectionsAsync(key.Bytes);
            var records = await galleryStore.ReadRecordsAsync(key.Bytes);
            var (next, changed) = change(before, records);
            next = next with { Revision = checked(before.Revision + 1) };
            IReadOnlyList<SavedView>? views = null;
            if (renamedCollectionId is not null)
            {
                var oldName = RequireCollection(before, renamedCollectionId).Name;
                var newName = RequireCollection(next, renamedCollectionId).Name;
                views = (await galleryStore.LoadViewsAsync(key.Bytes)).Select(view => view.GalleryFilter.Collection.Equals(oldName, StringComparison.OrdinalIgnoreCase)
                    ? view with { GalleryFilter = view.GalleryFilter with { Collection = newName } } : view).ToArray();
            }
            await galleryStore.CommitCollectionCatalogAsync(changed, next, views, key.Bytes);
            var changedIds = changed.Select(image => image.Id).ToHashSet();
            return new(next, new(before, next.Revision, records.Where(image => changedIds.Contains(image.Id)).Select(Placement).ToImmutableArray(), changed.Select(Placement).ToImmutableArray(), renamedCollectionId));
        }
        finally { galleryStore.Gate.Release(); }
    }

    public async Task<CollectionDefinition> CreateCollectionAsync(string name)
    {
        name = ContainerName(name);
        var item = new CollectionDefinition(Guid.NewGuid().ToString("N"), name, DateTimeOffset.UtcNow);
        await MutateCollectionsAsync((catalog, _) =>
        {
            UniqueName(catalog.Collections.Select(collection => collection.Name), name);
            return (catalog with { Collections = catalog.Collections.Add(item) }, []);
        });
        return item;
    }
    public async Task<MediaSet> CreateSetAsync(string collectionId, string name)
    {
        name = ContainerName(name);
        var id = Guid.NewGuid().ToString("N");
        var result = await MutateCollectionsAsync((catalog, _) =>
        {
            RequireCollection(catalog, collectionId);
            UniqueName(catalog.Sets.Where(set => set.CollectionId == collectionId).Select(set => set.Name), name);
            var order = catalog.Sets.Where(set => set.CollectionId == collectionId).Select(set => set.Order).DefaultIfEmpty(-1).Max() + 1;
            return (Touch(catalog with { Sets = catalog.Sets.Add(new(id, collectionId, name, order)) }, collectionId), []);
        });
        return result.Catalog.Sets.First(set => set.Id == id);
    }
    private static CollectionCatalog Touch(CollectionCatalog catalog, string collectionId) => catalog with
    { Collections = catalog.Collections.Select(collection => collection.Id == collectionId ? collection with { UpdatedAt = DateTimeOffset.UtcNow } : collection).ToImmutableArray() };

    public Task<CollectionMutation> RenameCollectionAsync(string id, string name) => MutateCollectionsAsync((catalog, records) =>
    {
        var item = RequireCollection(catalog, id); var valid = ContainerName(name);
        UniqueName(catalog.Collections.Where(collection => collection.Id != id).Select(collection => collection.Name), valid);
        var setIds = catalog.Sets.Where(set => set.CollectionId == id).Select(set => set.Id).ToHashSet();
        return (Touch(catalog with { Collections = catalog.Collections.Replace(item, item with { Name = valid }) }, id),
            records.Where(image => image.SetId is not null && setIds.Contains(image.SetId)).Select(image => image with { Collections = [valid] }).ToArray());
    }, id);

    public Task<CollectionMutation> RenameSetAsync(string id, string name) => MutateCollectionsAsync((catalog, _) =>
    {
        var set = RequireSet(catalog, id); var valid = ContainerName(name);
        UniqueName(catalog.Sets.Where(other => other.CollectionId == set.CollectionId && other.Id != id).Select(other => other.Name), valid);
        return (Touch(catalog with { Sets = catalog.Sets.Replace(set, set with { Name = valid }) }, set.CollectionId), []);
    });

    public Task<CollectionMutation> MoveMediaToSetAsync(IEnumerable<string> mediaIds, string? setId)
    {
        var ids = mediaIds.Distinct().ToArray();
        return MutateCollectionsAsync((catalog, records) =>
        {
            var set = setId is null ? null : RequireSet(catalog, setId);
            var collection = set is null ? null : RequireCollection(catalog, set.CollectionId);
            return PlaceMedia(catalog, records, ids, set, collection);
        });
    }

    // Destination creation and placement share one commit: Cancel and failed moves leave no containers behind.
    public Task<CollectionMutation> MoveMediaToDestinationAsync(IEnumerable<string> mediaIds, string collectionName,
        string setName, string? collectionId = null, string? setId = null)
    {
        var ids = mediaIds.Distinct().ToArray();
        var validCollectionName = ContainerName(collectionName);
        var validSetName = ContainerName(setName);
        return MutateCollectionsAsync((catalog, records) =>
        {
            if (ids.Length == 0) throw new InvalidOperationException("Choose media to move first.");
            var collection = collectionId is not null ? RequireCollection(catalog, collectionId)
                : catalog.Collections.FirstOrDefault(item => item.Name.Equals(validCollectionName, StringComparison.OrdinalIgnoreCase));
            if (collection is null)
            {
                collection = new(Guid.NewGuid().ToString("N"), validCollectionName, DateTimeOffset.UtcNow);
                catalog = catalog with { Collections = catalog.Collections.Add(collection) };
            }
            var set = setId is not null ? RequireSet(catalog, setId)
                : catalog.Sets.FirstOrDefault(item => item.CollectionId == collection.Id && item.Name.Equals(validSetName, StringComparison.OrdinalIgnoreCase));
            if (set is not null && set.CollectionId != collection.Id)
                throw new InvalidOperationException("This set no longer belongs to the selected collection. Refresh and try again.");
            if (set is null)
            {
                var order = catalog.Sets.Where(item => item.CollectionId == collection.Id).Select(item => item.Order).DefaultIfEmpty(-1).Max() + 1;
                set = new(Guid.NewGuid().ToString("N"), collection.Id, validSetName, order);
                catalog = catalog with { Sets = catalog.Sets.Add(set) };
            }
            return PlaceMedia(catalog, records, ids, set, collection);
        });
    }

    private static (CollectionCatalog Catalog, ImageRecord[] Records) PlaceMedia(CollectionCatalog catalog,
        List<ImageRecord> records, string[] ids, MediaSet? set, CollectionDefinition? collection)
    {
        var setId = set?.Id;
        var byId = records.ToDictionary(image => image.Id);
        if (ids.Any(id => !byId.ContainsKey(id))) throw new InvalidOperationException("Some selected media no longer exist. Refresh and try again.");
        long order = records.Where(image => image.SetId == setId).Select(image => image.SetOrder).DefaultIfEmpty(-1).Max() + 1;
        var changed = ids.Select(id => byId[id]).Where(image => image.SetId != setId || !image.LegacyCollections.IsEmpty).Select(image => image with
        { SetId = setId, SetOrder = order++, Collections = collection is null ? [] : [collection.Name], LegacyCollections = [] }).ToArray();
        return (collection is null ? catalog : Touch(catalog, collection.Id), changed);
    }

    public Task<CollectionMutation> DeleteContainerAsync(string id, bool isSet) => MutateCollectionsAsync((catalog, records) =>
    {
        if (isSet) RequireSet(catalog, id); else RequireCollection(catalog, id);
        var removed = catalog.Sets.Where(set => isSet ? set.Id == id : set.CollectionId == id).Select(set => set.Id).ToHashSet();
        return (catalog with { Sets = catalog.Sets.Where(set => !removed.Contains(set.Id)).ToImmutableArray(),
            Collections = isSet ? catalog.Collections : catalog.Collections.Where(collection => collection.Id != id).ToImmutableArray() },
            records.Where(image => image.SetId is not null && removed.Contains(image.SetId)).Select(image => image with { SetId = null, SetOrder = 0, Collections = [] }).ToArray());
    });

    public Task<CollectionMutation> MoveSetAsync(string setId, string collectionId) => MutateCollectionsAsync((catalog, records) =>
    {
        var set = RequireSet(catalog, setId); var collection = RequireCollection(catalog, collectionId);
        if (set.CollectionId == collectionId) return (catalog, []);
        UniqueName(catalog.Sets.Where(other => other.CollectionId == collectionId).Select(other => other.Name), set.Name);
        var order = catalog.Sets.Where(other => other.CollectionId == collectionId).Select(other => other.Order).DefaultIfEmpty(-1).Max() + 1;
        return (Touch(catalog with { Sets = catalog.Sets.Replace(set, set with { CollectionId = collectionId, Order = order }) }, collectionId),
            records.Where(image => image.SetId == setId).Select(image => image with { Collections = [collection.Name] }).ToArray());
    });

    public Task<CollectionMutation> ReorderSetAsync(string id, int offset) => MutateCollectionsAsync((catalog, _) =>
    {
        var set = RequireSet(catalog, id);
        var siblings = catalog.Sets.Where(other => other.CollectionId == set.CollectionId).OrderBy(other => other.Order).ToList();
        var from = siblings.FindIndex(other => other.Id == id); var to = (int)Math.Clamp((long)from + offset, 0, siblings.Count - 1);
        siblings.RemoveAt(from); siblings.Insert(to, set);
        var ordered = siblings.Select((other, index) => other with { Order = index }).ToDictionary(other => other.Id);
        return (Touch(catalog with { Sets = catalog.Sets.Select(other => ordered.GetValueOrDefault(other.Id, other)).ToImmutableArray() }, set.CollectionId), []);
    });

    public Task<CollectionMutation> ReorderMediaAsync(string setId, string mediaId, int offset) => MutateCollectionsAsync((catalog, records) =>
    {
        RequireSet(catalog, setId);
        var ordered = records.Where(image => image.SetId == setId).OrderBy(image => image.SetOrder).ThenBy(image => image.Id).ToList();
        var from = ordered.FindIndex(image => image.Id == mediaId);
        if (from < 0) throw new InvalidOperationException("This media is no longer in the set.");
        var to = Math.Clamp(from + Math.Sign(offset), 0, ordered.Count - 1);var item = ordered[from];ordered.RemoveAt(from);ordered.Insert(to, item);
        return (catalog, ordered.Select((image, index) => image with { SetOrder = index }).ToArray());
    });

    public async Task UndoCollectionChangeAsync(CollectionUndo undo)
    {
        await MutateCollectionsAsync((catalog, records) =>
        {
            if (catalog.Revision != undo.ExpectedRevision) throw new InvalidOperationException("Organization changed since this action. Refresh before making another move.");
            var before = undo.BeforeMedia.ToDictionary(image => image.Id);var after = undo.AfterMedia.ToDictionary(image => image.Id);
            var current = records.Where(image => before.ContainsKey(image.Id)).ToArray();
            if (current.Length != before.Count || current.Any(image => image.SetId != after[image.Id].SetId || image.SetOrder != after[image.Id].Order))
                throw new InvalidOperationException("Affected media changed or were removed; this action can no longer be undone.");
            return (undo.Before, current.Select(image => image with { SetId = before[image.Id].SetId, SetOrder = before[image.Id].Order,
                Collections = before[image.Id].Collections, LegacyCollections = before[image.Id].LegacyCollections }).ToArray());
        }, undo.RenamedCollectionId);
    }
}
