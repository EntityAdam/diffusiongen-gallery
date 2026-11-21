using System.Collections.Immutable;
using Gallery.Core.Features.Tags.TagNameValidators;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Tags;

internal static class TagCatalogStore
{
    public static string[] TagsOf(ImageRecord image) => image.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public static ImmutableArray<string> NormalizeTagNames(IEnumerable<string> tags) => tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
        .Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    public static ImmutableArray<string> ReconcileNames(TagCatalog tagCatalog, IEnumerable<ImageRecord> records) =>
        NormalizeTagNames(tagCatalog.Names.Concat(records.SelectMany(TagsOf)));

    public static TagCatalog MergeEditedTags(TagCatalog tagCatalog, ImageRecord imageRecordForTagRetention, ImageRecord imageRecordToCommit)
    {
        var previousTagNames = TagsOf(imageRecordForTagRetention);
        var updatedTagNames = TagsOf(imageRecordToCommit);
        var alreadyStoredNames = tagCatalog.Names.Concat(previousTagNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tagName in updatedTagNames.Where(tagName => !alreadyStoredNames.Contains(tagName)))
            TagNameValidator.ValidateTagName(tagName);
        return new(NormalizeTagNames(tagCatalog.Names.Concat(previousTagNames).Concat(updatedTagNames)));
    }

    private static bool HasInitializedNames(TagCatalog tagCatalog) => !tagCatalog.Names.IsDefault;

    public static async Task<(TagCatalog Catalog, bool IsInitialized)> ReadSnapshotAsync(
        IGalleryStore store, byte[] key, IReadOnlyList<ImageRecord>? records = null)
    {
        var catalog = await store.ReadSettingsAsync<TagCatalog>(TagCatalog.SettingId, TagCatalog.SettingPurpose, key);
        if (catalog is { } saved && HasInitializedNames(saved)) return (saved, true);
        catalog = new(NormalizeTagNames((records ?? await store.ReadRecordsAsync(key)).SelectMany(TagsOf)));
        return (catalog, false);
    }

    public static async Task<TagCatalog> ReadAsync(IGalleryStore store, byte[] key, IReadOnlyList<ImageRecord>? records = null)
    {
        var (catalog, isInitialized) = await ReadSnapshotAsync(store, key, records);
        if (!isInitialized) await store.CommitTagCatalogAsync([], catalog, null, key);
        return catalog;
    }

    public static async Task<IReadOnlyList<string>> LoadNamesAsync(IGalleryStore store, byte[] key)
    {
        await store.Gate.WaitAsync();
        try { return (await ReadAsync(store, key)).Names; }
        finally { store.Gate.Release(); }
    }
}
