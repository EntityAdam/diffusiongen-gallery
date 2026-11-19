using System.Security.Cryptography;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

public sealed class CollectionCatalogTests
{
    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task CollectionCommandsUseTheSelectedVaultAfterSwitching(CatalogBackend backend)
    {
        using var source = new ServiceFixture(backend);
        using var destination = new ServiceFixture(backend);
        await source.InitializeAsync();
        var sourceRoot = source.Scope.Vault.VaultRoot;
        var sourceCollection = await source.Scope.Collections.CreateCollectionAsync("Source");
        await destination.InitializeAsync();
        var destinationCollection = await destination.Scope.Collections.CreateCollectionAsync("Destination");
        var destinationRoot = destination.Scope.Vault.VaultRoot;
        await destination.Scope.Vault.CloseVaultAsync();

        await source.Scope.Vault.OpenExistingVaultAsync(destinationRoot);
        await source.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var catalog = await source.Scope.Collections.LoadCollectionCatalogAsync();
        Assert.Equal(destinationCollection.Id, Assert.Single(catalog.Collections).Id);
        await source.Scope.Collections.RenameCollectionAsync(destinationCollection.Id, "Renamed");

        await source.Scope.Vault.OpenExistingVaultAsync(sourceRoot);
        await source.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        catalog = await source.Scope.Collections.LoadCollectionCatalogAsync();
        Assert.Equal(sourceCollection, Assert.Single(catalog.Collections));

        await destination.Scope.Vault.OpenExistingVaultAsync(destinationRoot);
        await destination.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        catalog = await destination.Scope.Collections.LoadCollectionCatalogAsync();
        Assert.Equal("Renamed", Assert.Single(catalog.Collections).Name);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task FailedDestinationMoveLeavesNoContainersAndExistingDestinationsAreReused(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);
        await fixture.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Collections.MoveMediaToDestinationAsync(["missing"], "New", "Set"));
        var empty = await fixture.Scope.Collections.LoadCollectionCatalogAsync();
        Assert.Empty(empty.Collections);
        Assert.Empty(empty.Sets);
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(17, 19)), "second.png", "folder");
        var moved = await fixture.Scope.Collections.MoveMediaToDestinationAsync([first.Id], "New", "Set");
        var reused = await fixture.Scope.Collections.MoveMediaToDestinationAsync([second.Id], " new ", " SET ");
        Assert.Single(reused.Catalog.Collections);
        Assert.Single(reused.Catalog.Sets);
        Assert.Equal(moved.Catalog.Sets[0].Id, reused.Catalog.Sets[0].Id);
        Assert.All(await fixture.Scope.Catalog.ListAsync(), image => Assert.Equal(moved.Catalog.Sets[0].Id, image.SetId));
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task EmptyHierarchySurvivesReopeningAndNamesAreUniqueWithinParents(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var first = await fixture.Scope.Collections.CreateCollectionAsync("Series");
        var other = await fixture.Scope.Collections.CreateCollectionAsync("Portraits");
        var set = await fixture.Scope.Collections.CreateSetAsync(first.Id, "Season 1");
        await fixture.Scope.Collections.CreateSetAsync(other.Id, "Season 1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Collections.CreateCollectionAsync(" series "));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Collections.CreateSetAsync(first.Id, "SEASON 1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Collections.CreateSetAsync("missing", "Invalid"));
        var vault = fixture.Scope.Vault.VaultRoot;await fixture.Scope.Vault.CloseVaultAsync();await fixture.Scope.Vault.OpenExistingVaultAsync(vault);await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var catalog = await fixture.Scope.Collections.LoadCollectionCatalogAsync();
        Assert.Equal(2, catalog.Collections.Length);Assert.Contains(catalog.Sets, item => item.Id == set.Id);Assert.Empty(await fixture.Scope.Catalog.ListAsync());
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task MovesReplaceOwnershipAndUndoPreservesLaterTagAndRatingEdits(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var a = await fixture.Scope.Collections.CreateCollectionAsync("A");var b = await fixture.Scope.Collections.CreateCollectionAsync("B");
        var sa = await fixture.Scope.Collections.CreateSetAsync(a.Id, "First");var sb = await fixture.Scope.Collections.CreateSetAsync(b.Id, "Second");
        await fixture.Scope.Collections.MoveMediaToSetAsync([image.Id], sa.Id);
        var moved = await fixture.Scope.Collections.MoveMediaToSetAsync([image.Id, image.Id], sb.Id);
        var updated = Assert.Single(await fixture.Scope.Catalog.ListAsync());Assert.Equal(sb.Id, updated.SetId);Assert.Equal(new[] { "B" }, updated.Collections.ToArray());
        await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Tags = "later", Rating = 5 });
        await fixture.Scope.Collections.UndoCollectionChangeAsync(moved.Undo);
        updated = Assert.Single(await fixture.Scope.Catalog.ListAsync());Assert.Equal(sa.Id, updated.SetId);Assert.Equal("later", updated.Tags);Assert.Equal(5, updated.Rating);
        Assert.Equal(new[] { "A" }, updated.Collections.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Collections.UndoCollectionChangeAsync(moved.Undo));
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task DeleteCollectionReturnsMediaToInboxAndUndoRestoresHierarchy(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Tags = "safe", Rating = 4, IsFavorite = true });
        var collection = await fixture.Scope.Collections.CreateCollectionAsync("Series");var set = await fixture.Scope.Collections.CreateSetAsync(collection.Id, "First");
        await fixture.Scope.Collections.MoveMediaToSetAsync([image.Id], set.Id);
        var deleted = await fixture.Scope.Collections.DeleteContainerAsync(collection.Id, false);
        Assert.Empty(deleted.Catalog.Collections);Assert.Empty(deleted.Catalog.Sets);
        var unorganized = Assert.Single(await fixture.Scope.Catalog.ListAsync());Assert.Null(unorganized.SetId);Assert.Empty(unorganized.Collections);Assert.Equal("safe", unorganized.Tags);Assert.Equal(4, unorganized.Rating);Assert.True(unorganized.IsFavorite);
        Assert.True(File.Exists(unorganized.StoredPath));
        await fixture.Scope.Collections.UndoCollectionChangeAsync(deleted.Undo);
        Assert.Equal(set.Id, Assert.Single(await fixture.Scope.Catalog.ListAsync()).SetId);Assert.Single((await fixture.Scope.Collections.LoadCollectionCatalogAsync()).Collections);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task RenameAndSetMoveCascadeWithoutChangingTagsAndMissingBatchMemberRollsBack(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var a = await fixture.Scope.Collections.CreateCollectionAsync("Old");var b = await fixture.Scope.Collections.CreateCollectionAsync("Other");var set = await fixture.Scope.Collections.CreateSetAsync(a.Id, "First");
        await fixture.Scope.Collections.MoveMediaToSetAsync([image.Id], set.Id);await fixture.Scope.Settings.SaveViewAsync("Saved", new GalleryFilter { Collection = "Old" });
        var renamed = await fixture.Scope.Collections.RenameCollectionAsync(a.Id, "Renamed");
        Assert.Equal("Renamed", Assert.Single(await fixture.Scope.Catalog.ListAsync()).Collections[0]);Assert.Equal("Renamed", Assert.Single(await fixture.Scope.Settings.LoadViewsAsync()).GalleryFilter.Collection);
        await fixture.Scope.Collections.UndoCollectionChangeAsync(renamed.Undo);
        Assert.Equal("Old", Assert.Single(await fixture.Scope.Settings.LoadViewsAsync()).GalleryFilter.Collection);
        await fixture.Scope.Collections.RenameCollectionAsync(a.Id, "Renamed");
        await fixture.Scope.Collections.MoveSetAsync(set.Id, b.Id);Assert.Equal("Other", Assert.Single(await fixture.Scope.Catalog.ListAsync()).Collections[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Collections.MoveMediaToSetAsync([image.Id, "missing"], null));
        Assert.Equal(set.Id, Assert.Single(await fixture.Scope.Catalog.ListAsync()).SetId);
        await fixture.Scope.Collections.DeleteContainerAsync(set.Id, true);Assert.Null(Assert.Single(await fixture.Scope.Catalog.ListAsync()).SetId);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task LegacySingleMembershipMigratesAndAmbiguousMembershipIsPreservedForReview(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var one = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        var many = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4,4)), "many.png", "folder");
        await fixture.Scope.Records.UpdateAsync(one.Id, edit => edit with { Collections = ["Series"] });
        await fixture.Scope.Records.UpdateAsync(many.Id, edit => edit with { Collections = ["Series", "Portraits"] });
        var catalog = await fixture.Scope.Collections.LoadCollectionCatalogAsync();var records = await fixture.Scope.Catalog.ListAsync();
        var single = records.Single(image => image.Id == one.Id);Assert.NotNull(single.SetId);Assert.EndsWith("/ Imported media", catalog.PathOf(single));
        var ambiguous = records.Single(image => image.Id == many.Id);Assert.Null(ambiguous.SetId);Assert.Empty(ambiguous.Collections);Assert.Equal(new[] { "Series", "Portraits" }, ambiguous.LegacyCollections.ToArray());
        await fixture.Scope.Collections.MoveMediaToSetAsync([many.Id], single.SetId);Assert.Empty((await fixture.Scope.Catalog.ListAsync()).Single(image => image.Id == many.Id).LegacyCollections);
        Assert.Equal(catalog.Collections.Length, (await fixture.Scope.Collections.LoadCollectionCatalogAsync()).Collections.Length);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task SequenceAndSetOrderingAreDeterministic(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);await fixture.InitializeAsync();
        var a = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");var b = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4,4)), "two.png", "folder");
        var collection = await fixture.Scope.Collections.CreateCollectionAsync("Series");var first = await fixture.Scope.Collections.CreateSetAsync(collection.Id, "First");var second = await fixture.Scope.Collections.CreateSetAsync(collection.Id, "Second");
        await fixture.Scope.Collections.ReorderSetAsync(second.Id, -1);Assert.Equal(second.Id, (await fixture.Scope.Collections.LoadCollectionCatalogAsync()).Sets.OrderBy(set => set.Order).First().Id);
        await fixture.Scope.Collections.MoveMediaToSetAsync([b.Id, a.Id], first.Id);Assert.Equal(new[] { b.Id, a.Id }, (await fixture.Scope.Catalog.ListAsync()).OrderBy(image => image.SetOrder).Select(image => image.Id));
        await fixture.Scope.Collections.ReorderMediaAsync(first.Id, a.Id, -1);Assert.Equal(a.Id, (await fixture.Scope.Catalog.ListAsync()).OrderBy(image => image.SetOrder).First().Id);
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
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        var collection = await fixture.Scope.Collections.CreateCollectionAsync("Collection");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Collections = ["Other"] }));
        var set = await fixture.Scope.Collections.CreateSetAsync(collection.Id, "Set");await fixture.Scope.Collections.MoveMediaToSetAsync([image.Id], set.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Collections = ["Other"] }));
        Assert.Equal(set.Id, Assert.Single(await fixture.Scope.Catalog.ListAsync()).SetId);
    }

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string root = Path.Combine(TemporaryRoot(), "tag-library-" + Guid.NewGuid().ToString("N"));
        private static string TemporaryRoot()
        {
            string path = Path.GetTempPath();
            return OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;
        }
        public GalleryScope Scope { get; }
        public ServiceFixture(CatalogBackend backend, HttpClient? client = null) => Scope = GalleryScope.Create(Path.Combine(root, "vault"),
            new GalleryRuntimeOptions { CatalogBackend = backend, StateDirectory = Path.Combine(root, "state") }, client ?? new HttpClient());
        public Task<string> InitializeAsync() => Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        public void Dispose()
        {
            Scope.Dispose();
            Scope.DriveOperations.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
