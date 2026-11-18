using System.Security.Cryptography;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Sessions;
using Gallery.Core.Services.Validators;

namespace Gallery.Core.Services;

internal sealed class TagCatalogManager(GalleryLifetime galleryLifetime, VaultSession vaultSession) : IGalleryTags
{
    private static string ValidateTagName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Contains(',') || name.Any(char.IsControl))
            throw new InvalidOperationException("A tag must contain 1–100 characters and cannot contain commas or control characters.");
        return name.Trim();
    }

    public async Task<IReadOnlyList<TagDefinition>> LoadTagCatalogAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        await store.Gate.WaitAsync();
        try
        {
            var records = await store.ReadRecordsAsync(key.Bytes);
            var catalog = await TagCatalogStore.ReadAsync(store, key.Bytes, records);
            var names = TagCatalogStore.NormalizeTagNames(catalog.Names.Concat(records.SelectMany(TagCatalogStore.TagsOf)));
            if (!names.SequenceEqual(catalog.Names))
                await store.CommitTagCatalogAsync([], new(names), null, key.Bytes);
            var counts = records.SelectMany(image => TagCatalogStore.TagsOf(image).Distinct(StringComparer.OrdinalIgnoreCase))
                .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            return names.Select(name => new TagDefinition(name, counts.GetValueOrDefault(name))).ToArray();
        }
        finally { store.Gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> LoadTagNamesAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        return await TagCatalogStore.LoadNamesAsync(store, key.Bytes);
    }

    public Task AddTagAsync(string name) => ChangeTagCatalogAsync(null, ValidateTagName(name));
    public Task RenameTagAsync(string name, string replacement) => ChangeTagCatalogAsync(RequireTagLookup(name), ValidateTagName(replacement));
    public Task DeleteTagAsync(string name) => ChangeTagCatalogAsync(RequireTagLookup(name), null);
    private static string RequireTagLookup(string name) => !string.IsNullOrWhiteSpace(name)
        ? name.Trim() : throw new InvalidOperationException("Choose an existing tag.");

    private async Task ChangeTagCatalogAsync(string? original, string? replacement)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        await store.Gate.WaitAsync();
        try
        {
            var records = await store.ReadRecordsAsync(key.Bytes);
            var catalog = await TagCatalogStore.ReadAsync(store, key.Bytes, records);
            var names = TagCatalogStore.NormalizeTagNames(catalog.Names.Concat(records.SelectMany(TagCatalogStore.TagsOf)));
            if (original is not null && !names.Contains(original, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Tag '{original}' does not exist.");
            var target = replacement is null ? null : names.FirstOrDefault(name => name.Equals(replacement, StringComparison.OrdinalIgnoreCase)
                && !name.Equals(original, StringComparison.OrdinalIgnoreCase)) ?? replacement;
            var nextNames = TagCatalogStore.NormalizeTagNames(names.Where(name => !name.Equals(original, StringComparison.OrdinalIgnoreCase))
                .Concat(target is null ? [] : new[] { target }));
            var changed = original is null ? [] : records.Where(image => TagCatalogStore.TagsOf(image).Contains(original, StringComparer.OrdinalIgnoreCase))
                .Select(image => image with
                {
                    Tags = string.Join(", ", TagCatalogStore.TagsOf(image).Select(tag => tag.Equals(original, StringComparison.OrdinalIgnoreCase) ? target : tag)
                        .Where(tag => tag is not null).Distinct(StringComparer.OrdinalIgnoreCase))
                }).ToArray();
            // Renames follow exact-tag saved filters. Deletion retains those filters so it cannot silently broaden their results.
            IReadOnlyList<SavedView>? views = original is not null && target is not null
                ? (await store.LoadViewsAsync(key.Bytes)).Select(view => view with
                {
                    GalleryFilter = view.GalleryFilter.WithRenamedTag(original, target)
                }).ToArray()
                : null;
            await store.CommitTagCatalogAsync(changed, new(nextNames), views, key.Bytes);
        }
        finally { store.Gate.Release(); }
    }

    public const long MaxTagCsvBytes = IGalleryTags.MaxCsvBytes;

    public async Task<(int Added, int Skipped)> ImportTagsCsvAsync(Stream input)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryLifetime.Current.GalleryStore;
        using var content = await ImportContentValidator.ReadAsync(input, MaxTagCsvBytes, "Tag CSV files must be 10 MiB or smaller.");
        string[] imported;
        try
        {
            content.Position = 0;
            imported = TagCsvProcessor.Read(content).Select((name, index) =>
            {
                try { return ValidateTagName(name); }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidOperationException($"CSV tag {index + 1}: {exception.Message}", exception);
                }
            }).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(content.GetBuffer()); }
        generation.RevocationToken.ThrowIfCancellationRequested();
        using var key = generation.Borrow();
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            var records = await store.ReadRecordsAsync(key.Bytes);
            var catalog = await TagCatalogStore.ReadAsync(store, key.Bytes, records);
            var names = TagCatalogStore.NormalizeTagNames(catalog.Names.Concat(records.SelectMany(TagCatalogStore.TagsOf)));
            var next = TagCatalogStore.NormalizeTagNames(names.Concat(imported));
            var added = next.Length - names.Length;
            generation.RevocationToken.ThrowIfCancellationRequested();
            if (!next.SequenceEqual(catalog.Names)) await store.CommitTagCatalogAsync([], new(next), null, key.Bytes);
            return (added, imported.Length - added);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<ExportFile> ExportTagsCsvAsync()
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryLifetime.Current.GalleryStore;
        using var key = generation.Borrow();
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            var records = await store.ReadRecordsAsync(key.Bytes);
            var catalog = await TagCatalogStore.ReadAsync(store, key.Bytes, records);
            var names = TagCatalogStore.NormalizeTagNames(catalog.Names.Concat(records.SelectMany(TagCatalogStore.TagsOf)));
            generation.RevocationToken.ThrowIfCancellationRequested();
            var csv = TagCsvProcessor.Write(names);
            try { return new("tags.csv", "text/csv;charset=utf-8", csv); }
            finally { CryptographicOperations.ZeroMemory(csv); }
        }
        finally { store.Gate.Release(); }
    }

    // The record editor already holds the catalog gate for the read, validation, and commit.
    public static async Task SaveEditedTagsAsync(IGalleryStore store, ImageRecord previous, ImageRecord updated, byte[] key)
    {
        var catalog = await TagCatalogStore.ReadAsync(store, key);
        var names = TagCatalogStore.NormalizeTagNames(catalog.Names.Concat(TagCatalogStore.TagsOf(previous)).Concat(TagCatalogStore.TagsOf(updated)));
        // Keep tag definitions even when their last assignment is removed.
        await store.CommitTagCatalogAsync([updated], new(names), null, key);
    }
}
