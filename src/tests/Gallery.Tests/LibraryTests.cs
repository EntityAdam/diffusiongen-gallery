using System.Net;
using System.Text;
using Gallery.Core;
using Gallery.Core.Queries;
using Gallery.Models;
using Gallery.Services;

namespace Gallery.Tests;

// Direct rule/query tests isolate preservation of unselected fields and ordering of synthetic records,
// without import timestamps/files obscuring the oracle. Vision tests use a fake transport to supply
// malformed responses deterministically; persistence scenarios use the real encrypted store.
public sealed class LibraryTests
{
    [Theory]
    // Null means leave the field unchanged; explicit values replace only the selected fields.
    [InlineData(null, null, 4, true)]
    [InlineData(2, null, 2, true)]
    [InlineData(null, false, 4, false)]
    [InlineData(0, true, 0, true)]
    public void BulkCuration_WhenOptionalFieldsAreProvided_ChangesOnlyThoseFields(int? rating, bool? reviewed, int expectedRating, bool expectedReviewed)
    {
        // Arrange
        var image = new ImageRecordEdit(new ImageRecord { Rating = 4, Reviewed = true, Favorite = true, MarkedForDeletion = true });

        // Act
        LibraryRules.SetCuration(image, rating, reviewed);

        // Assert
        Assert.Equal(expectedRating, image.Rating);
        Assert.Equal(expectedReviewed, image.Reviewed);
        TestAssertions.True(image.Favorite, "the favorite flag remains true");
        TestAssertions.True(image.MarkedForDeletion, "the deletion flag remains true");
    }

    [Fact]
    public void BulkCuration_WhenRatingExceedsRange_ReportsLimitsWithoutChangingReviewStatus()
    {
        // Arrange
        using var fixture = new TestVault();
        var image = new ImageRecordEdit(new ImageRecord { Rating = 4, Reviewed = true });

        // Act
        var error = Record.Exception(() => fixture.Service.SetCuration(image, 6, false));

        // Assert
        TestAssertions.Exception<InvalidOperationException>(error,
            "the rating validation message identifies the invalid value and allowed range", "6", "between", "0", "5");
        Assert.Equal(4, image.Rating);
        TestAssertions.True(image.Reviewed, "the previous reviewed state is preserved after validation fails");
    }

    [Fact]
    public async Task Organization_WhenAppliedToImage_PersistsEncryptedWithoutMovingFiles()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        // Act
        await fixture.Service.UpdateAsync(image.Id, current =>
        {
            LibraryRules.SetOrganization(current, 4, true, "Private collection");
            LibraryRules.SetOrganization(current, 4, true, "private collection");
            current.Tags = LibraryRules.MergeTags("green, landscape", "GREEN, cinema");
        });
        var storedRecords = await fixture.Service.ListAsync();
        var encryptedRecord = await fixture.Store.ReadRecordAsync(image.Id);
        var ratingError = Record.Exception(() =>
            fixture.Service.SetOrganization(new ImageRecordEdit(image), 6, true, null));

        // Assert
        var stored = TestAssertions.Single(storedRecords, "the updated image remains in the library");
        Assert.Equal(4, stored.Rating);
        TestAssertions.True(stored.Reviewed, "the image remains reviewed");
        Assert.Equal("Private collection", Assert.Single(stored.Collections));
        Assert.Equal("green, landscape, cinema", stored.Tags);
        Assert.Equal(image.StoredPath, stored.StoredPath);
        Assert.DoesNotContain("Private collection", Encoding.UTF8.GetString(encryptedRecord));
        TestAssertions.Exception<InvalidOperationException>(ratingError,
            "the organization rating is rejected with the invalid value and allowed bounds", "6", "between", "0", "5");
    }

    [Fact]
    public async Task SavedViews_WhenChangedAndRestarted_RemainEncryptedIndependentAndAvailable()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var library = new LibraryService(fixture.Store, fixture.Session);
        var filter = new GalleryFilter { Collection = "private-worlds", MinRating = 4, Review = "pending", Sort = "rating" };
        // Act
        await library.SaveViewAsync("Private view", filter);
        filter = filter with { MinRating = 1 };
        var initialViews = await library.LoadViewsAsync();
        var payload = await fixture.Store.ReadSettingsAsync("saved-views");
        await library.SaveViewAsync("private VIEW", filter);
        var replacedViews = await library.LoadViewsAsync();
        fixture.Session.Lock();
        var lockedError = await Record.ExceptionAsync(() => library.LoadViewsAsync());
        fixture.Session.Unlock(await fixture.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase));
        var restartedViews = await library.LoadViewsAsync();
        await library.DeleteViewAsync("private VIEW");
        var remainingViews = await library.LoadViewsAsync();

        // Assert
        Assert.Equal(4, TestAssertions.Single(initialViews, "the saved view is returned").Filter.MinRating);
        TestAssertions.DoesNotContain("Private view", Encoding.UTF8.GetString(payload!), "saved views are encrypted");
        TestAssertions.Single(replacedViews, "case-insensitive names replace rather than duplicate saved views");
        Assert.IsType<InvalidOperationException>(lockedError);
        Assert.Equal(1, TestAssertions.Single(restartedViews, "the replaced view survives unlocking").Filter.MinRating);
        TestAssertions.Empty(remainingViews, "deleting the saved view leaves no entries");
    }

    [Fact]
    public async Task SavedViews_WhenSavedConcurrently_PreserveBothEntriesAndFilters()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var library = new LibraryService(fixture.Store, fixture.Session);
        // Act
        await Task.WhenAll(library.SaveViewAsync("First", new() { MinRating = 3 }),
            library.SaveViewAsync("Second", new() { Review = "pending" }));

        // Assert
        var views = (await library.LoadViewsAsync()).OrderBy(view => view.Name, StringComparer.Ordinal).ToArray();
        Assert.Collection(views,
            first =>
            {
                Assert.Equal("First", first.Name);
                Assert.Equal(3, first.Filter.MinRating);
            },
            second =>
            {
                Assert.Equal("Second", second.Name);
                Assert.Equal("pending", second.Filter.Review);
            });
    }

    [Fact]
    public void Gallery02_WhenInitialized_UsesAnIndependentDefaultVault()
    {
        // Arrange
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionGenGallery02");

        // Act
        var vault = new VaultStore(root);

        // Assert
        Assert.Equal("DiffusionGenGallery02", Path.GetFileName(vault.Root));
    }

    [Fact]
    public void GalleryFilter_WhenNewCriteriaAreUsed_ComposesWithExistingFilters()
    {
        // Arrange
        var first = new ImageRecord { Collections = ["Worlds"], Rating = 4, Reviewed = false, StoredPath = "C:\\one.dpng" };
        var second = first with { Id = "second", Rating = 5, Reviewed = true };
        var third = first with { Id = "third", Collections = [], Rating = 0 };
        var filter = new GalleryFilter { Collection = "worlds", MinRating = 4, Review = "pending" };
        // Act
        var matching = GalleryFilterProcessor.Apply(filter, [first, second, third]).ToArray();
        var unorganized = GalleryFilterProcessor.Apply(new() { UnorganizedOnly = true }, [first, second, third]).ToArray();
        var ratingSorted = GalleryFilterProcessor.Apply(new() { Sort = "rating" }, [first, second, third]).ToArray();

        // Assert
        Assert.Equal(first, TestAssertions.Single(matching, "collection, rating, and pending-review filters select one record"));
        Assert.Equal(third, TestAssertions.Single(unorganized, "only the third record is unorganized"));
        TestAssertions.True(ratingSorted.Length > 0, $"rating sorting returns a nonempty selection; observed count {ratingSorted.Length}");
        Assert.Equal(second, ratingSorted[0]);
    }

    [Fact]
    public void GalleryFilter_WhenStoredPathHasNoParent_ReturnsNoFolderMatch()
    {
        // Arrange
        var image = new ImageRecord { StoredPath = "image.dpng" };

        // Act
        var matches = GalleryFilterProcessor.Apply(new GalleryFilter { Folder = "source" }, [image]);

        // Assert
        TestAssertions.Empty(matches, "a path without a parent directory does not match a folder filter");
    }

    [Fact]
    public async Task SearchInterpretation_WhenModelReturnsValidAndInvalidFilters_ValidatesWithoutChangingRecords()
    {
        // Arrange
        var service = new VisionService(new HttpClient(new JsonHandler("""
            {"search":"","tag":"landscape","collection":"Worlds","minRating":4,"favoritesOnly":true,"orientation":"landscape","review":"pending"}
            """)));
        // Act
        var filter = await service.InterpretSearchAsync(new() { Model = "local" }, "Favorite landscapes awaiting review", ["Worlds"]);
        var error = await Record.ExceptionAsync(() =>
            service.InterpretSearchAsync(new() { Model = "local" }, "query", []));

        // Assert
        Assert.Equal("landscape", filter.Tag);
        Assert.Equal(4, filter.MinRating);
        TestAssertions.True(filter.FavoritesOnly, "the interpreted filter enables favorites-only results");
        TestAssertions.Exception<InvalidOperationException>(error,
            "the error identifies the rejected collection and its valid choices", "Worlds", "does not exist", "none");
    }

    [Fact]
    public async Task CollectionSuggestion_WhenModelOutputIsInvalid_ReportsTheInvalidCollection()
    {
        // Arrange
        var service = new VisionService(new HttpClient(new JsonHandler("""{"collection":"Worlds"}""")));
        var invalidService = new VisionService(new HttpClient(new JsonHandler("""{"collection":""}""")));

        // Act
        var suggested = await service.SuggestCollectionAsync(new() { Model = "local" }, "Landscape", "green", []);
        var error = await Record.ExceptionAsync(() =>
            invalidService.SuggestCollectionAsync(new() { Model = "local" }, "Landscape", "green", []));

        // Assert
        Assert.Equal("Worlds", suggested);
        TestAssertions.Exception<InvalidOperationException>(error,
            "the error identifies the offending empty collection and its required constraint", "collection ''", "nonempty", "80");
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
