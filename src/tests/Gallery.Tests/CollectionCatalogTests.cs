using Gallery.Core.Services;
using Gallery.Core.Services.Ports.Models;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Ports;
using System.Security.Cryptography;

namespace Gallery.Tests;

public sealed class CollectionCatalogTests
{
    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task FailedDestinationMoveLeavesNoContainersAndExistingDestinationsAreReused(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);
        await fixture.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.MoveMediaToDestinationAsync(["missing"], "New", "Set"));
        var empty = await fixture.Service.LoadCollectionCatalogAsync();
        Assert.Empty(empty.Collections);
        Assert.Empty(empty.Sets);
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(17, 19)), "second.png", "folder");
        var moved = await fixture.Service.MoveMediaToDestinationAsync([first.Id], "New", "Set");
        var reused = await fixture.Service.MoveMediaToDestinationAsync([second.Id], " new ", " SET ");
        Assert.Single(reused.Catalog.Collections);
        Assert.Single(reused.Catalog.Sets);
        Assert.Equal(moved.Catalog.Sets[0].Id, reused.Catalog.Sets[0].Id);
        Assert.All(await fixture.Service.ListAsync(), image => Assert.Equal(moved.Catalog.Sets[0].Id, image.SetId));
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task EmptyHierarchySurvivesReopeningAndNamesAreUniqueWithinParents(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var first = await fixture.Service.CreateCollectionAsync("Series");
        var other = await fixture.Service.CreateCollectionAsync("Portraits");
        var set = await fixture.Service.CreateSetAsync(first.Id, "Season 1");
        await fixture.Service.CreateSetAsync(other.Id, "Season 1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CreateCollectionAsync(" series "));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CreateSetAsync(first.Id, "SEASON 1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CreateSetAsync("missing", "Invalid"));
        var vault = fixture.Service.VaultRoot;await fixture.Service.CloseVaultAsync();await fixture.Service.OpenExistingVaultAsync(vault);await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var catalog = await fixture.Service.LoadCollectionCatalogAsync();
        Assert.Equal(2, catalog.Collections.Length);Assert.Contains(catalog.Sets, item => item.Id == set.Id);Assert.Empty(await fixture.Service.ListAsync());
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task MovesReplaceOwnershipAndUndoPreservesLaterTagAndRatingEdits(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var a = await fixture.Service.CreateCollectionAsync("A");var b = await fixture.Service.CreateCollectionAsync("B");
        var sa = await fixture.Service.CreateSetAsync(a.Id, "First");var sb = await fixture.Service.CreateSetAsync(b.Id, "Second");
        await fixture.Service.MoveMediaToSetAsync([image.Id], sa.Id);
        var moved = await fixture.Service.MoveMediaToSetAsync([image.Id, image.Id], sb.Id);
        var updated = Assert.Single(await fixture.Service.ListAsync());Assert.Equal(sb.Id, updated.SetId);Assert.Equal(new[] { "B" }, updated.Collections.ToArray());
        await fixture.Service.UpdateAsync(image.Id, edit => edit with { Tags = "later", Rating = 5 });
        await fixture.Service.UndoCollectionChangeAsync(moved.Undo);
        updated = Assert.Single(await fixture.Service.ListAsync());Assert.Equal(sa.Id, updated.SetId);Assert.Equal("later", updated.Tags);Assert.Equal(5, updated.Rating);
        Assert.Equal(new[] { "A" }, updated.Collections.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UndoCollectionChangeAsync(moved.Undo));
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task DeleteCollectionReturnsMediaToInboxAndUndoRestoresHierarchy(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.UpdateAsync(image.Id, edit => edit with { Tags = "safe", Rating = 4, IsFavorite = true });
        var collection = await fixture.Service.CreateCollectionAsync("Series");var set = await fixture.Service.CreateSetAsync(collection.Id, "First");
        await fixture.Service.MoveMediaToSetAsync([image.Id], set.Id);
        var deleted = await fixture.Service.DeleteContainerAsync(collection.Id, false);
        Assert.Empty(deleted.Catalog.Collections);Assert.Empty(deleted.Catalog.Sets);
        var unorganized = Assert.Single(await fixture.Service.ListAsync());Assert.Null(unorganized.SetId);Assert.Empty(unorganized.Collections);Assert.Equal("safe", unorganized.Tags);Assert.Equal(4, unorganized.Rating);Assert.True(unorganized.IsFavorite);
        Assert.True(File.Exists(unorganized.StoredPath));
        await fixture.Service.UndoCollectionChangeAsync(deleted.Undo);
        Assert.Equal(set.Id, Assert.Single(await fixture.Service.ListAsync()).SetId);Assert.Single((await fixture.Service.LoadCollectionCatalogAsync()).Collections);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task RenameAndSetMoveCascadeWithoutChangingTagsAndMissingBatchMemberRollsBack(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var a = await fixture.Service.CreateCollectionAsync("Old");var b = await fixture.Service.CreateCollectionAsync("Other");var set = await fixture.Service.CreateSetAsync(a.Id, "First");
        await fixture.Service.MoveMediaToSetAsync([image.Id], set.Id);await fixture.Service.SaveViewAsync("Saved", new GalleryFilter { Collection = "Old" });
        var renamed = await fixture.Service.RenameCollectionAsync(a.Id, "Renamed");
        Assert.Equal("Renamed", Assert.Single(await fixture.Service.ListAsync()).Collections[0]);Assert.Equal("Renamed", Assert.Single(await fixture.Service.LoadViewsAsync()).GalleryFilter.Collection);
        await fixture.Service.UndoCollectionChangeAsync(renamed.Undo);
        Assert.Equal("Old", Assert.Single(await fixture.Service.LoadViewsAsync()).GalleryFilter.Collection);
        await fixture.Service.RenameCollectionAsync(a.Id, "Renamed");
        await fixture.Service.MoveSetAsync(set.Id, b.Id);Assert.Equal("Other", Assert.Single(await fixture.Service.ListAsync()).Collections[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.MoveMediaToSetAsync([image.Id, "missing"], null));
        Assert.Equal(set.Id, Assert.Single(await fixture.Service.ListAsync()).SetId);
        await fixture.Service.DeleteContainerAsync(set.Id, true);Assert.Null(Assert.Single(await fixture.Service.ListAsync()).SetId);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task LegacySingleMembershipMigratesAndAmbiguousMembershipIsPreservedForReview(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var one = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        var many = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(4,4)), "many.png", "folder");
        await fixture.Service.UpdateAsync(one.Id, edit => edit with { Collections = ["Series"] });
        await fixture.Service.UpdateAsync(many.Id, edit => edit with { Collections = ["Series", "Portraits"] });
        var catalog = await fixture.Service.LoadCollectionCatalogAsync();var records = await fixture.Service.ListAsync();
        var single = records.Single(image => image.Id == one.Id);Assert.NotNull(single.SetId);Assert.EndsWith("/ Imported media", catalog.PathOf(single));
        var ambiguous = records.Single(image => image.Id == many.Id);Assert.Null(ambiguous.SetId);Assert.Empty(ambiguous.Collections);Assert.Equal(new[] { "Series", "Portraits" }, ambiguous.LegacyCollections.ToArray());
        await fixture.Service.MoveMediaToSetAsync([many.Id], single.SetId);Assert.Empty((await fixture.Service.ListAsync()).Single(image => image.Id == many.Id).LegacyCollections);
        Assert.Equal(catalog.Collections.Length, (await fixture.Service.LoadCollectionCatalogAsync()).Collections.Length);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task SequenceAndSetOrderingAreDeterministic(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var a = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");var b = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(4,4)), "two.png", "folder");
        var collection = await fixture.Service.CreateCollectionAsync("Series");var first = await fixture.Service.CreateSetAsync(collection.Id, "First");var second = await fixture.Service.CreateSetAsync(collection.Id, "Second");
        await fixture.Service.ReorderSetAsync(second.Id, -1);Assert.Equal(second.Id, (await fixture.Service.LoadCollectionCatalogAsync()).Sets.OrderBy(set => set.Order).First().Id);
        await fixture.Service.MoveMediaToSetAsync([b.Id, a.Id], first.Id);Assert.Equal(new[] { b.Id, a.Id }, (await fixture.Service.ListAsync()).OrderBy(image => image.SetOrder).Select(image => image.Id));
        await fixture.Service.ReorderMediaAsync(first.Id, a.Id, -1);Assert.Equal(a.Id, (await fixture.Service.ListAsync()).OrderBy(image => image.SetOrder).First().Id);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task AdapterRollsBackEntireHierarchyTransactionWhenARecordIsMissing(CatalogBackend backend)
    {
        var root = Path.Combine(Path.GetTempPath(), "collection-atomic-" + Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            IGalleryStore store = new GalleryStoreFactory().Create(root, backend);await store.InitializeAsync(key);await using var operation = await store.OpenOperationAsync(key);
            var image = new ImageRecord { Id = "exists", Name = "sample", Sha256 = new string('A',64) };using var thumbnail = new ImageThumbnail([1,2,3]);await store.InsertAsync(image, thumbnail, key);
            await store.CommitCollectionCatalogAsync([], CollectionCatalog.Empty, null, key);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitCollectionCatalogAsync([image with { SetId = "set" }, image with { Id = "missing", SetId = "set" }], new([new("collection", "New", DateTimeOffset.UtcNow)], [new("set", "collection", "Set", 0)]), null, key));
            Assert.Null((await store.ReadRecordAsync(image.Id, key)).SetId);Assert.Empty((await store.ReadSettingsAsync<CollectionCatalog>(CollectionCatalog.SettingId, CollectionCatalog.SettingPurpose, key))!.Collections);
        }
        finally { CryptographicOperations.ZeroMemory(key);Directory.Delete(root,true); }
    }
    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task LegacyEditCannotBypassSingleDestinationAfterHierarchyIsInitialized(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        var collection = await fixture.Service.CreateCollectionAsync("Collection");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UpdateAsync(image.Id, edit => fixture.Service.AddCollection(edit, "Other")));
        var set = await fixture.Service.CreateSetAsync(collection.Id, "Set");await fixture.Service.MoveMediaToSetAsync([image.Id], set.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UpdateAsync(image.Id, edit => fixture.Service.AddCollection(edit, "Other")));
        Assert.Equal(set.Id, Assert.Single(await fixture.Service.ListAsync()).SetId);
    }

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string root = Path.Combine(TemporaryRoot(), "tag-library-" + Guid.NewGuid().ToString("N"));
        private static string TemporaryRoot()
        {
            string path = Path.GetTempPath();
            return OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;
        }
        public GalleryService Service { get; }
        public ServiceFixture(CatalogBackend backend, HttpClient? client = null) => Service = GalleryService.Create(Path.Combine(root, "vault"),
            new GalleryRuntimeOptions { CatalogBackend = backend, StateDirectory = Path.Combine(root, "state") }, client ?? new HttpClient());
        public Task<string> InitializeAsync() => Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        public void Dispose()
        {
            Service.Dispose();
            Service.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
