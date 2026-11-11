using System.Collections.Immutable;
using Gallery.Core.Models;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Processors;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    private static string[] TagsOf(ImageRecord image) => image.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static ImmutableArray<string> NormalizeTagNames(IEnumerable<string> tags) => tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
        .Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();

    private static string ValidateTagName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Contains(',') || name.Any(char.IsControl))
            throw new InvalidOperationException("A tag must contain 1–100 characters and cannot contain commas or control characters.");
        return name.Trim();
    }

    private async Task<TagCatalog> ReadTagCatalogWithKeyAsync(byte[] key, IReadOnlyList<ImageRecord>? records = null, IGalleryStore? store = null)
    {
        store ??= galleryStore;
        var catalog = await store.ReadSettingsAsync<TagCatalog>(TagCatalog.SettingId, TagCatalog.SettingPurpose, key);
        if (catalog is not null && !catalog.Names.IsDefault) return catalog;
        catalog = new(NormalizeTagNames((records ?? await store.ReadRecordsAsync(key)).SelectMany(TagsOf)));
        await store.CommitTagCatalogAsync([], catalog, null, key);
        return catalog;
    }

    public async Task<IReadOnlyList<TagDefinition>> LoadTagCatalogAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.Gate.WaitAsync();
        try
        {
            var records = await galleryStore.ReadRecordsAsync(key.Bytes);
            var catalog = await ReadTagCatalogWithKeyAsync(key.Bytes, records);
            var names = NormalizeTagNames(catalog.Names.Concat(records.SelectMany(TagsOf)));
            if (!names.SequenceEqual(catalog.Names))
                await galleryStore.CommitTagCatalogAsync([], new(names), null, key.Bytes);
            var counts = records.SelectMany(image => TagsOf(image).Distinct(StringComparer.OrdinalIgnoreCase))
                .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            return names.Select(name => new TagDefinition(name, counts.GetValueOrDefault(name))).ToArray();
        }
        finally { galleryStore.Gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> LoadTagNamesAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.Gate.WaitAsync();
        try { return (await ReadTagCatalogWithKeyAsync(key.Bytes)).Names; }
        finally { galleryStore.Gate.Release(); }
    }

    public Task AddTagAsync(string name) => ChangeTagCatalogAsync(null, ValidateTagName(name));
    public Task RenameTagAsync(string name, string replacement) => ChangeTagCatalogAsync(RequireTagLookup(name), ValidateTagName(replacement));
    public Task DeleteTagAsync(string name) => ChangeTagCatalogAsync(RequireTagLookup(name), null);
    private static string RequireTagLookup(string name) => !string.IsNullOrWhiteSpace(name)
        ? name.Trim() : throw new InvalidOperationException("Choose an existing tag.");

    private async Task ChangeTagCatalogAsync(string? original, string? replacement)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.Gate.WaitAsync();
        try
        {
            var records = await galleryStore.ReadRecordsAsync(key.Bytes);
            var catalog = await ReadTagCatalogWithKeyAsync(key.Bytes, records);
            var names = NormalizeTagNames(catalog.Names.Concat(records.SelectMany(TagsOf)));
            if (original is not null && !names.Contains(original, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Tag '{original}' does not exist.");
            var target = replacement is null ? null : names.FirstOrDefault(name => name.Equals(replacement, StringComparison.OrdinalIgnoreCase)
                && !name.Equals(original, StringComparison.OrdinalIgnoreCase)) ?? replacement;
            var nextNames = NormalizeTagNames(names.Where(name => !name.Equals(original, StringComparison.OrdinalIgnoreCase))
                .Concat(target is null ? [] : new[] { target }));
            var changed = original is null ? [] : records.Where(image => TagsOf(image).Contains(original, StringComparer.OrdinalIgnoreCase))
                .Select(image => image with
                {
                    Tags = string.Join(", ", TagsOf(image).Select(tag => tag.Equals(original, StringComparison.OrdinalIgnoreCase) ? target : tag)
                        .Where(tag => tag is not null).Distinct(StringComparer.OrdinalIgnoreCase))
                }).ToArray();
            // Renames follow exact-tag saved filters. Deletion retains those filters so it cannot silently broaden their results.
            IReadOnlyList<SavedView>? views = original is not null && target is not null
                ? (await galleryStore.LoadViewsAsync(key.Bytes)).Select(view => view.GalleryFilter.Tag.Equals(original, StringComparison.OrdinalIgnoreCase)
                    ? view with { GalleryFilter = view.GalleryFilter with { Tag = target } } : view).ToArray()
                : null;
            await galleryStore.CommitTagCatalogAsync(changed, new(nextNames), views, key.Bytes);
        }
        finally { galleryStore.Gate.Release(); }
    }

    private async Task SaveEditedTagsAsync(ImageRecord previous, ImageRecord updated, byte[] key)
    {
        var catalog = await ReadTagCatalogWithKeyAsync(key);
        var names = NormalizeTagNames(catalog.Names.Concat(TagsOf(previous)).Concat(TagsOf(updated)));
        // Keep tag definitions even when their last assignment is removed.
        await galleryStore.CommitTagCatalogAsync([updated], new(names), null, key);
    }
}
