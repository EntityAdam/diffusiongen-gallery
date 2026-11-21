using System.Security.Cryptography;
using Gallery.Core.Features.Tags.CatalogProjectors;
using Gallery.Core.Features.Ingestion;
using Gallery.Core.Features.Tags.TagNameValidators;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Tags;

internal sealed class TagCatalogManager(GalleryLifetime galleryLifetime, VaultSession vaultSession)
    : IGalleryTags, IGalleryRecordTagCommitter
{
    private static string ValidateTagName(string name)
        => TagNameValidator.ValidateTagName(name);

    public async Task<IReadOnlyList<TagDefinition>> LoadTagCatalogAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        await store.Gate.WaitAsync();
        try
        {
            var records = await store.ReadRecordsAsync(key.Bytes);
            var catalog = await TagCatalogStore.ReadAsync(store, key.Bytes, records);
            var projection = TagCatalogProjector.Project(catalog, records);
            if (!projection.Catalog.Names.SequenceEqual(catalog.Names))
                await store.CommitTagCatalogAsync([], projection.Catalog, null, key.Bytes);
            return projection.Definitions;
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
            var names = TagCatalogStore.ReconcileNames(catalog, records);
            if (original is not null && !names.Contains(original, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Tag '{original}' does not exist.");
            var target = replacement is null ? null : names.FirstOrDefault(name => name.Equals(replacement, StringComparison.OrdinalIgnoreCase)
                && !name.Equals(original, StringComparison.OrdinalIgnoreCase)) ?? replacement;
            var nextNames = TagCatalogStore.NormalizeTagNames(names.Where(name => !name.Equals(original, StringComparison.OrdinalIgnoreCase))
                .Concat(target is null ? [] : new[] { target }));
            var changedRecords = original is null ? [] : PrepareChangedTagAssignments(records, original, target);
            // Renames follow exact-tag saved filters. Deletion retains those filters so it cannot silently broaden their results.
            IReadOnlyList<SavedView>? views = original is not null && target is not null
                ? PrepareRenamedSavedViews(await store.LoadViewsAsync(key.Bytes), original, target)
                : null;
            await store.CommitTagCatalogAsync(changedRecords, new(nextNames), views, key.Bytes);
        }
        finally { store.Gate.Release(); }
    }

    private static ImageRecord[] PrepareChangedTagAssignments(IReadOnlyList<ImageRecord> records, string original, string? target) =>
        records.Where(image => TagCatalogStore.TagsOf(image).Contains(original, StringComparer.OrdinalIgnoreCase))
            .Select(image => image with
            {
                Tags = string.Join(", ", TagCatalogStore.TagsOf(image).Select(tag =>
                        tag.Equals(original, StringComparison.OrdinalIgnoreCase) ? target : tag)
                    .Where(tag => tag is not null).Distinct(StringComparer.OrdinalIgnoreCase))
            }).ToArray();

    private static IReadOnlyList<SavedView> PrepareRenamedSavedViews(
        IReadOnlyList<SavedView> savedViews, string original, string replacement) =>
        savedViews.Select(view => view with
        {
            GalleryFilter = view.GalleryFilter.WithRenamedTag(original, replacement)
        }).ToArray();

    public const long MaxTagCsvBytes = IGalleryTags.MaxCsvBytes;

    public async Task<(int Added, int Skipped)> ImportTagsCsvAsync(Stream input)
    {
        var unlockedVaultSession = vaultSession.AcquireUnlockedSession();
        var store = galleryLifetime.Current.GalleryStore;
        using var content = await ImportContentValidator.ReadAsync(input, MaxTagCsvBytes, "Tag CSV files must be 10 MiB or smaller.");
        var importedRows = TagCsvProcessor.ReadAndClear(content);
        unlockedVaultSession.RevocationToken.ThrowIfCancellationRequested();
        using var key = unlockedVaultSession.Borrow();
        await store.Gate.WaitAsync(unlockedVaultSession.RevocationToken);
        try
        {
            var records = await store.ReadRecordsAsync(key.Bytes);
            var (catalog, isInitialized) = await TagCatalogStore.ReadSnapshotAsync(store, key.Bytes, records);
            var names = TagCatalogStore.ReconcileNames(catalog, records);
            var imported = TagCsvProcessor.ValidateRows(importedRows, names);
            var next = TagCatalogStore.NormalizeTagNames(names.Concat(imported));
            var added = next.Length - names.Length;
            unlockedVaultSession.RevocationToken.ThrowIfCancellationRequested();
            if (!isInitialized || !next.SequenceEqual(catalog.Names))
                await store.CommitTagCatalogAsync([], new(next), null, key.Bytes);
            return (added, imported.Length - added);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<ExportFile> ExportTagsCsvAsync()
    {
        var unlockedVaultSession = vaultSession.AcquireUnlockedSession();
        var store = galleryLifetime.Current.GalleryStore;
        using var key = unlockedVaultSession.Borrow();
        await store.Gate.WaitAsync(unlockedVaultSession.RevocationToken);
        try
        {
            var records = await store.ReadRecordsAsync(key.Bytes);
            var catalog = await TagCatalogStore.ReadAsync(store, key.Bytes, records);
            var names = TagCatalogStore.ReconcileNames(catalog, records);
            unlockedVaultSession.RevocationToken.ThrowIfCancellationRequested();
            var csv = TagCsvProcessor.Write(names);
            try { return new("tags.csv", "text/csv;charset=utf-8", csv); }
            finally { CryptographicOperations.ZeroMemory(csv); }
        }
        finally { store.Gate.Release(); }
    }

    // The record editor already holds the catalog gate for the read, validation, and commit.
    public async Task CommitEditedTagsAsync(
        IGalleryStore store, ImageRecord imageRecordForTagRetention, ImageRecord imageRecordToCommit, byte[] key)
    {
        var (catalog, _) = await TagCatalogStore.ReadSnapshotAsync(store, key);
        var nextCatalog = TagCatalogStore.MergeEditedTags(catalog, imageRecordForTagRetention, imageRecordToCommit);
        await store.CommitTagCatalogAsync([imageRecordToCommit], nextCatalog, null, key);
    }
}
