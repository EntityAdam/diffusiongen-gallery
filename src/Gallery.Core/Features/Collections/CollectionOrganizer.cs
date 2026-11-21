using System.Collections.Immutable;
using Gallery.Core.Features.Settings;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Collections;

internal sealed class CollectionOrganizer(GalleryLifetime galleryLifetime, VaultSession vaultSession) : IGalleryCollections
{
    public async Task<CollectionCatalog> LoadCollectionCatalogAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var galleryStore = galleryLifetime.Current.GalleryStore;
        await galleryStore.Gate.WaitAsync();
        try { return await ReadCollectionsAsync(galleryStore, key.Bytes); }
        finally { galleryStore.Gate.Release(); }
    }

    internal static async Task<CollectionCatalog> ReadCollectionsAsync(IGalleryStore galleryStore, byte[] key)
    {
        var saved = await galleryStore.ReadSettingsAsync<CollectionCatalog>(CollectionCatalog.SettingId, CollectionCatalog.SettingPurpose, key);
        if (saved is not null) return saved;
        var records = await galleryStore.ReadRecordsAsync(key);
        var legacyCollectionMigration = BuildLegacyCollectionMigration(records);
        await galleryStore.CommitCollectionCatalogAsync(legacyCollectionMigration.Records, legacyCollectionMigration.Catalog, null, key);
        return legacyCollectionMigration.Catalog;
    }

    private static LegacyCollectionMigration BuildLegacyCollectionMigration(IReadOnlyList<ImageRecord> records)
    {
        var names = records.SelectMany(image => image.Collections).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var collections = names.Select(name => new CollectionDefinition(Guid.NewGuid().ToString("N"), name, DateTimeOffset.UtcNow)).ToImmutableArray();
        var sets = collections.Select(collection => new MediaSet(Guid.NewGuid().ToString("N"), collection.Id, "Imported media", 0)).ToImmutableArray();
        var migratedRecords = records.Where(image => !image.Collections.IsEmpty).Select((image, index) =>
        {
            var distinct = image.Collections.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (distinct.Length != 1) return image with { LegacyCollections = image.Collections, Collections = [], SetId = null };
            var collection = RequireExactlyOne(collections,
                item => item.Name.Equals(distinct[0], StringComparison.OrdinalIgnoreCase),
                $"Legacy collection '{distinct[0]}' for media '{image.Id}'");
            var set = RequireExactlyOne(sets, item => item.CollectionId == collection.Id,
                $"Imported set for collection '{collection.Id}'");
            return image with { SetId = set.Id, SetOrder = index, Collections = [collection.Name] };
        }).ToArray();
        return new(new(collections, sets, 1), migratedRecords);
    }

    private static T RequireExactlyOne<T>(IEnumerable<T> source, Func<T, bool> predicate, string selection)
    {
        var matches = source.Where(predicate).Take(2).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"{selection} expected exactly one match, but found {(matches.Length == 0 ? "no matches" : "multiple matches")}.");
        return matches[0];
    }

    private sealed record LegacyCollectionMigration(CollectionCatalog Catalog, ImageRecord[] Records);

    private static string ContainerName(string name)
    {
        if (IsInvalidContainerName(name))
            throw new InvalidOperationException("Use a name of 1–80 characters without control characters.");
        return name.Trim();
    }
    private static bool IsInvalidContainerName(string name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl);
    private static void UniqueName(IEnumerable<string> names, string name)
    {
        if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"“{name}” already exists. Choose the existing item or use another name.");
    }
    private static MediaSet RequireSet(CollectionCatalog catalog, string id) => catalog.Sets.FirstOrDefault(set => set.Id == id)
        ?? throw new InvalidOperationException("This set no longer exists. Choose another destination.");
    internal static CollectionDefinition RequireCollection(CollectionCatalog catalog, string id) => catalog.Collections.FirstOrDefault(collection => collection.Id == id)
        ?? throw new InvalidOperationException("This collection no longer exists. Refresh and try again.");
    private static MediaPlacement Placement(ImageRecord image) => new(image.Id, image.SetId, image.SetOrder, image.Collections, image.LegacyCollections);

    private async Task<CollectionMutation> MutateCollectionsAsync(
        Func<CollectionCatalog, List<ImageRecord>, (CollectionCatalog CatalogToCommit, ImageRecord[] ChangedRecords)> change,
        string? renamedCollectionId = null, object? expectedStoreIdentity = null,
        ImmutableArray<SavedViewCollectionChange>? recordedViewChanges = null)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var galleryStore = galleryLifetime.Current.GalleryStore;
        if (expectedStoreIdentity is not null && !ReferenceEquals(galleryStore, expectedStoreIdentity))
            throw new InvalidOperationException("This collection change belongs to a different gallery.");
        await galleryStore.Gate.WaitAsync();
        try
        {
            var beforeCatalog = await ReadCollectionsAsync(galleryStore, key.Bytes);
            var records = await galleryStore.ReadRecordsAsync(key.Bytes);
            CollectionCatalog catalogToCommit;
            ImageRecord[] changedRecords;
            (catalogToCommit, changedRecords) = change(beforeCatalog, records);
            var nextCatalog = catalogToCommit with { Revision = checked(beforeCatalog.Revision + 1) };
            var savedViewRename = renamedCollectionId is null ? null
                : await PrepareRenamedViewsAsync(galleryStore, beforeCatalog, nextCatalog, renamedCollectionId, key.Bytes, recordedViewChanges);
            await galleryStore.CommitCollectionCatalogAsync(changedRecords, nextCatalog, savedViewRename?.Views, key.Bytes);
            var undo = BuildUndo(galleryStore, beforeCatalog, nextCatalog, records, changedRecords, renamedCollectionId,
                savedViewRename?.Changes ?? []);
            return new(nextCatalog, undo);
        }
        finally { galleryStore.Gate.Release(); }
    }

    private static async Task<SavedViewRename> PrepareRenamedViewsAsync(IGalleryStore galleryStore, CollectionCatalog beforeCatalog,
        CollectionCatalog nextCatalog, string collectionId, byte[] key, ImmutableArray<SavedViewCollectionChange>? recordedChanges)
    {
        var savedViews = await galleryStore.LoadViewsAsync(key);
        if (recordedChanges is { } changes)
            return new(RestoreRenamedViews(savedViews, changes), changes);

        var oldName = RequireCollection(beforeCatalog, collectionId).Name;
        var newName = RequireCollection(nextCatalog, collectionId).Name;
        var changedViews = savedViews.Where(view => view.GalleryFilter.Collection.Equals(oldName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var viewChanges = changedViews.Select(view => new SavedViewCollectionChange(view.Name, view.GalleryFilter.Collection, newName)).ToImmutableArray();
        var renamedViews = savedViews.Select(view => view.GalleryFilter.Collection.Equals(oldName, StringComparison.OrdinalIgnoreCase)
            ? view with { GalleryFilter = view.GalleryFilter with { Collection = newName } }
            : view).ToArray();
        return new(renamedViews, viewChanges);
    }

    private static IReadOnlyList<SavedView> RestoreRenamedViews(IReadOnlyList<SavedView> savedViews,
        ImmutableArray<SavedViewCollectionChange> changes)
    {
        foreach (var savedViewCollectionChange in changes)
        {
            var matches = savedViews.Where(view => view.Name.Equals(savedViewCollectionChange.Name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length != 1 || !matches[0].GalleryFilter.Collection.Equals(savedViewCollectionChange.AfterCollection, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Saved view '{savedViewCollectionChange.Name}' no longer has the expected collection filter '{savedViewCollectionChange.AfterCollection}'.");
        }

        var savedViewCollectionChangesByViewName = changes.ToDictionary(savedViewCollectionChange => savedViewCollectionChange.Name, StringComparer.OrdinalIgnoreCase);
        return savedViews.Select(view => savedViewCollectionChangesByViewName.TryGetValue(view.Name, out var savedViewCollectionChange)
            ? view with { GalleryFilter = view.GalleryFilter with { Collection = savedViewCollectionChange.BeforeCollection } }
            : view).ToArray();
    }

    private static CollectionUndo BuildUndo(IGalleryStore galleryStore, CollectionCatalog beforeCatalog, CollectionCatalog nextCatalog,
        IReadOnlyList<ImageRecord> records, ImageRecord[] changedRecords, string? renamedCollectionId,
        ImmutableArray<SavedViewCollectionChange> renamedViewChanges)
    {
        var changedIds = changedRecords.Select(image => image.Id).ToHashSet();
        return new CollectionUndo(beforeCatalog, nextCatalog.Revision,
            records.Where(image => changedIds.Contains(image.Id)).Select(Placement).ToImmutableArray(),
            changedRecords.Select(Placement).ToImmutableArray(), renamedCollectionId)
        {
            ResourceIdentity = galleryStore,
            RenamedViewChanges = renamedViewChanges
        };
    }

    private sealed record SavedViewRename(IReadOnlyList<SavedView> Views, ImmutableArray<SavedViewCollectionChange> Changes);

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
        return RequireExactlyOne(result.Catalog.Sets, set => set.Id == id, $"Generated set '{id}' for collection '{collectionId}'");
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
        var validCollectionName = collectionId is null ? ContainerName(collectionName) : collectionName;
        var validSetName = setId is null ? ContainerName(setName) : setName;
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

    private static (CollectionCatalog CatalogToCommit, ImageRecord[] ChangedRecords) PlaceMedia(CollectionCatalog catalog,
        List<ImageRecord> records, string[] ids, MediaSet? set, CollectionDefinition? collection)
    {
        var setId = set?.Id;
        var imageRecordsById = records.ToDictionary(image => image.Id);
        if (ids.Any(id => !imageRecordsById.ContainsKey(id))) throw new InvalidOperationException("Some selected media no longer exist. Refresh and try again.");
        long order = records.Where(image => image.SetId == setId).Select(image => image.SetOrder).DefaultIfEmpty(-1).Max() + 1;
        var changed = ids.Select(id => imageRecordsById[id]).Where(image => image.SetId != setId || !image.LegacyCollections.IsEmpty).Select(image => image with
        { SetId = setId, SetOrder = order++, Collections = collection is null ? [] : [collection.Name], LegacyCollections = [] }).ToArray();
        return (CatalogToCommit: collection is null ? catalog : Touch(catalog, collection.Id), ChangedRecords: changed);
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
        var reorderedMediaSetsById = siblings.Select((other, index) => other with { Order = index }).ToDictionary(other => other.Id);
        return (Touch(catalog with { Sets = catalog.Sets.Select(other => reorderedMediaSetsById.GetValueOrDefault(other.Id, other)).ToImmutableArray() }, set.CollectionId), []);
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
        var resourceIdentity = undo.ResourceIdentity
            ?? throw new InvalidOperationException("This collection change was not issued by an active gallery.");
        if (undo.RenamedCollectionId is not null && undo.RenamedViewChanges is null)
            throw new InvalidOperationException("This collection rename does not contain its saved-view changes.");
        await MutateCollectionsAsync((catalog, records) =>
        {
            if (catalog.Revision != undo.ExpectedRevision) throw new InvalidOperationException("Organization changed since this action. Refresh before making another move.");
            var placementsToRestoreByMediaId = undo.BeforeMedia.ToDictionary(image => image.Id);
            var expectedPlacementsByMediaId = undo.AfterMedia.ToDictionary(image => image.Id);
            var current = records.Where(image => placementsToRestoreByMediaId.ContainsKey(image.Id)).ToArray();
            if (current.Length != placementsToRestoreByMediaId.Count || current.Any(image =>
                    image.SetId != expectedPlacementsByMediaId[image.Id].SetId || image.SetOrder != expectedPlacementsByMediaId[image.Id].Order))
                throw new InvalidOperationException("Affected media changed or were removed; this action can no longer be undone.");
            return (CatalogToCommit: undo.Before, ChangedRecords: current.Select(image => image with
            {
                SetId = placementsToRestoreByMediaId[image.Id].SetId,
                SetOrder = placementsToRestoreByMediaId[image.Id].Order,
                Collections = placementsToRestoreByMediaId[image.Id].Collections,
                LegacyCollections = placementsToRestoreByMediaId[image.Id].LegacyCollections
            }).ToArray());
        }, undo.RenamedCollectionId, resourceIdentity, undo.RenamedViewChanges);
    }
}
