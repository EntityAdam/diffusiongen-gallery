using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record CollectionDefinition(string Id, string Name, DateTimeOffset UpdatedAt);
public sealed record MediaSet(string Id, string CollectionId, string Name, int Order);
public sealed record CollectionCatalog(ImmutableArray<CollectionDefinition> Collections, ImmutableArray<MediaSet> Sets, long Revision = 0)
{
    public const string SettingId = "collection-hierarchy";
    public const string SettingPurpose = "collection-hierarchy:v1";
    public static CollectionCatalog Empty { get; } = new([], []);
    public string PathOf(ImageRecord image)
    {
        var set = Sets.FirstOrDefault(item => item.Id == image.SetId);
        if (set is null) return "Unorganized";

        var collections = Collections.Where(collection => collection.Id == set.CollectionId).Take(2).ToArray();
        if (collections.Length != 1)
            throw new InvalidOperationException(
                $"Set '{set.Id}' expected exactly one parent collection with ID '{set.CollectionId}', but found {(collections.Length == 0 ? "no matches" : "multiple matches")}.");
        return $"{collections[0].Name} / {set.Name}";
    }
}

// Undo contains only organization data. Fresh notes, tags and ratings are preserved when it is applied.
public sealed record MediaPlacement(string Id, string? SetId, long Order, ImmutableArray<string> Collections, ImmutableArray<string> LegacyCollections);
public sealed record CollectionUndo(CollectionCatalog Before, long ExpectedRevision, ImmutableArray<MediaPlacement> BeforeMedia, ImmutableArray<MediaPlacement> AfterMedia, string? RenamedCollectionId = null)
{
    internal object? ResourceIdentity { get; init; }
    internal ImmutableArray<SavedViewCollectionChange>? RenamedViewChanges { get; init; }
}

internal sealed record SavedViewCollectionChange(string Name, string BeforeCollection, string AfterCollection);
public sealed record CollectionMutation(CollectionCatalog Catalog, CollectionUndo Undo);
