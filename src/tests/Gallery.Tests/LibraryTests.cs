using System.Net;
using System.Text;
using Gallery.Models;
using Gallery.Services;

namespace Gallery.Tests;

public sealed class LibraryTests
{
    [Theory]
    [InlineData(null, null, 4, true)]
    [InlineData(2, null, 2, true)]
    [InlineData(null, false, 4, false)]
    [InlineData(0, true, 0, true)]
    public void BulkCurationOnlyChangesExplicitFields(int? rating, bool? reviewed, int expectedRating, bool expectedReviewed)
    {
        var image = new ImageRecord { Rating = 4, Reviewed = true, Favorite = true, MarkedForDeletion = true };
        LibraryService.SetCuration(image, rating, reviewed);
        Assert.Equal(expectedRating, image.Rating);
        Assert.Equal(expectedReviewed, image.Reviewed);
        Assert.True(image.Favorite);
        Assert.True(image.MarkedForDeletion);
    }

    [Fact]
    public void InvalidBulkRatingDoesNotChangeReviewStatus()
    {
        var image = new ImageRecord { Rating = 4, Reviewed = true };
        Assert.Throws<InvalidOperationException>(() => LibraryService.SetCuration(image, 6, false));
        Assert.Equal(4, image.Rating);
        Assert.True(image.Reviewed);
    }

    [Fact]
    public async Task OrganizationPersistsEncryptedWithoutMovingImages()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        await fixture.Service.UpdateAsync(image.Id, current =>
        {
            LibraryService.SetOrganization(current, 4, true, "Private collection");
            LibraryService.SetOrganization(current, 4, true, "private collection");
            current.Tags = LibraryService.MergeTags("green, landscape", "GREEN, cinema");
        });
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(4, stored.Rating);
        Assert.True(stored.Reviewed);
        Assert.Single(stored.Collections);
        Assert.Equal("green, landscape, cinema", stored.Tags);
        Assert.Equal(image.StoredPath, stored.StoredPath);
        Assert.DoesNotContain("Private collection", Encoding.UTF8.GetString(await fixture.Store.ReadRecordAsync(image.Id)));
        Assert.Throws<InvalidOperationException>(() => LibraryService.SetOrganization(stored, 6, true, null));
    }

    [Fact]
    public async Task SavedViewsAreEncryptedIndependentAndSurviveRestart()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var library = new LibraryService(fixture.Store, fixture.Session);
        var filter = new GalleryFilter { Collection = "private-worlds", MinRating = 4, Review = "pending", Sort = "rating" };
        await library.SaveViewAsync("Private view", filter);
        filter.MinRating = 1;
        var saved = Assert.Single(await library.LoadViewsAsync());
        Assert.Equal(4, saved.Filter.MinRating);
        var payload = await fixture.Store.ReadSettingsAsync("saved-views");
        Assert.DoesNotContain("Private view", Encoding.UTF8.GetString(payload!));
        await library.SaveViewAsync("private VIEW", filter);
        Assert.Single(await library.LoadViewsAsync());
        fixture.Session.Lock();
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.LoadViewsAsync());
        fixture.Session.Unlock(await fixture.Vault.UnlockAsync(TestVault.Passphrase, false));
        Assert.Equal(1, Assert.Single(await library.LoadViewsAsync()).Filter.MinRating);
        await library.DeleteViewAsync("private VIEW");
        Assert.Empty(await library.LoadViewsAsync());
    }

    [Fact]
    public async Task ConcurrentSavedViewsDoNotLoseEntries()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var library = new LibraryService(fixture.Store, fixture.Session);
        await Task.WhenAll(library.SaveViewAsync("First", new() { MinRating = 3 }),
            library.SaveViewAsync("Second", new() { Review = "pending" }));
        Assert.Equal(2, (await library.LoadViewsAsync()).Count);
    }

    [Fact]
    public void Gallery02UsesAnIndependentDefaultVault()
    {
        var vault = new VaultStore(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.Equal("DiffusionGenGallery02", Path.GetFileName(vault.Root));
    }

    [Fact]
    public void NewFiltersAndRatingSortComposeWithExistingFilters()
    {
        var first = new ImageRecord { Collections = ["Worlds"], Rating = 4, Reviewed = false, StoredPath = "C:\\one.dpng" };
        var second = first with { Id = "second", Rating = 5, Reviewed = true };
        var third = first with { Id = "third", Collections = [], Rating = 0 };
        var filter = new GalleryFilter { Collection = "worlds", MinRating = 4, Review = "pending" };
        Assert.Equal(first, Assert.Single(filter.Apply([first, second, third])));
        filter = new() { UnorganizedOnly = true };
        Assert.Equal(third, Assert.Single(filter.Apply([first, second, third])));
        filter = new() { Sort = "rating" };
        Assert.Equal(second, filter.Apply([first, second, third]).First());
    }

    [Fact]
    public async Task SearchInterpretationIsValidatedAndDoesNotAlterRecords()
    {
        var service = new VisionService(new HttpClient(new JsonHandler("""
            {"search":"","tag":"landscape","collection":"Worlds","minRating":4,"favoritesOnly":true,"orientation":"landscape","review":"pending"}
            """)));
        var filter = await service.InterpretSearchAsync(new() { Model = "local" }, "Favorite landscapes awaiting review", ["Worlds"]);
        Assert.Equal("landscape", filter.Tag);
        Assert.Equal(4, filter.MinRating);
        Assert.True(filter.FavoritesOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InterpretSearchAsync(new() { Model = "local" }, "query", []));
    }

    [Fact]
    public async Task CollectionSuggestionsRejectInvalidModelOutput()
    {
        var service = new VisionService(new HttpClient(new JsonHandler("""{"collection":"Worlds"}""")));
        Assert.Equal("Worlds", await service.SuggestCollectionAsync(new() { Model = "local" }, "Landscape", "green", []));
        service = new(new HttpClient(new JsonHandler("""{"collection":""}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SuggestCollectionAsync(new() { Model = "local" }, "Landscape", "green", []));
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content = json } } }
                }))
            });
    }
}
