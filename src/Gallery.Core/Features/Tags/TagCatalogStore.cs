using System.Collections.Immutable;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Tags;

internal static class TagCatalogStore
{
    public static string[] TagsOf(ImageRecord image) => image.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public static ImmutableArray<string> NormalizeTagNames(IEnumerable<string> tags) => tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
        .Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();

    public static async Task<TagCatalog> ReadAsync(IGalleryStore store, byte[] key, IReadOnlyList<ImageRecord>? records = null)
    {
        var catalog = await store.ReadSettingsAsync<TagCatalog>(TagCatalog.SettingId, TagCatalog.SettingPurpose, key);
        if (catalog is not null && !catalog.Names.IsDefault) return catalog;
        catalog = new(NormalizeTagNames((records ?? await store.ReadRecordsAsync(key)).SelectMany(TagsOf)));
        await store.CommitTagCatalogAsync([], catalog, null, key);
        return catalog;
    }

    public static async Task<IReadOnlyList<string>> LoadNamesAsync(IGalleryStore store, byte[] key)
    {
        await store.Gate.WaitAsync();
        try { return (await ReadAsync(store, key)).Names; }
        finally { store.Gate.Release(); }
    }
}
