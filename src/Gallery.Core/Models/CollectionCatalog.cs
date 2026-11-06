using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record CollectionDefinition(string Id, string Name, DateTimeOffset UpdatedAt);
public sealed record MediaSet(string Id, string CollectionId, string Name, int Order);
public sealed record CollectionCatalog(ImmutableArray<CollectionDefinition> Collections, ImmutableArray<MediaSet> Sets, long Revision = 0)
{
    public const string SettingId = "collection-hierarchy";
    public const string SettingPurpose = "collection-hierarchy:v1";
    public static CollectionCatalog Empty { get; } = new([], []);
    public string PathOf(ImageRecord image) => Sets.FirstOrDefault(set => set.Id == image.SetId) is { } set
        ? $"{Collections.First(collection => collection.Id == set.CollectionId).Name} / {set.Name}" : "Unorganized";
}

// Undo contains only organization data. Fresh notes, tags and ratings are preserved when it is applied.
public sealed record MediaPlacement(string Id, string? SetId, long Order, ImmutableArray<string> Collections, ImmutableArray<string> LegacyCollections);
public sealed record CollectionUndo(CollectionCatalog Before, long ExpectedRevision, ImmutableArray<MediaPlacement> BeforeMedia, ImmutableArray<MediaPlacement> AfterMedia, string? RenamedCollectionId = null);
public sealed record CollectionMutation(CollectionCatalog Catalog, CollectionUndo Undo);
