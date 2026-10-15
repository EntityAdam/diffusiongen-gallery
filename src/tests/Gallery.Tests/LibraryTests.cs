using Microsoft.Extensions.DependencyInjection;
using Gallery.Web.Managers.Gateways;
using Gallery.Web.Services.DataManagers;
using System.Net;
using System.Text;
using Gallery.Web.Exceptions;
using Gallery.Core.Queries;
using Gallery.Web.Models;
using Gallery.Web.Services;

namespace Gallery.Tests;

// Library scenarios use the gallery manager boundary, with real vision gateway behavior above
// the scripted HTTP transport and independent encrypted-store observations.
[Trait("Category", "acceptance")]
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
        using var fixture = new TestVault();
        var image = new WebImageRecordEdit(new WebImageRecord { Rating = 4, Reviewed = true, Favorite = true, MarkedForDeletion = true });

        // Act
        image = fixture.Service.SetCuration(image, rating, reviewed);

        // Assert
        Assert.Equal(expectedRating, image.Rating);
        Assert.Equal(expectedReviewed, image.Reviewed);
        Assert.True(image.Favorite);
        Assert.True(image.MarkedForDeletion);
    }

    [Fact]
    public void BulkCuration_WhenRatingExceedsRange_ReportsLimitsWithoutChangingReviewStatus()
    {
        // Arrange
        using var fixture = new TestVault();
        var image = new WebImageRecordEdit(new WebImageRecord { Rating = 4, Reviewed = true });

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Service.SetCuration(image, 6, false));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("6", error.Message, StringComparison.Ordinal);
        Assert.Contains("between 0 and 5", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, image.Rating);
        Assert.True(image.Reviewed);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void Curation_WhenRatingIsAssigned_MarksReviewedOnlyForNonzeroRatings(int? rating, bool expectedReviewed)
    {
        using var fixture = new TestVault();
        var image = new WebImageRecordEdit(new WebImageRecord { Reviewed = false });

        image = fixture.Service.SetCuration(image, rating, false);

        Assert.Equal(expectedReviewed, image.Reviewed);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void DeletionMark_WhenChanged_OnlyMarkingReviewed(bool markedForDeletion, bool expectedReviewed)
    {
        using var fixture = new TestVault();
        var image = new WebImageRecordEdit(new WebImageRecord { Reviewed = false });

        image = fixture.Service.SetDeletionMark(image, markedForDeletion);

        Assert.Equal(markedForDeletion, image.MarkedForDeletion);
        Assert.Equal(expectedReviewed, image.Reviewed);
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
            current = fixture.Service.SetOrganization(current, 4, true, "Private collection");
            current = fixture.Service.SetOrganization(current, 4, true, "private collection");
            return current with { Tags = fixture.Service.MergeTags("green, landscape", "GREEN, cinema") };
        });
        var storedRecords = await fixture.Service.ListAsync();
        var encryptedRecord = await fixture.RawStore.ReadRecordAsync(image.Id);
        var ratingError = Assert.Throws<InvalidOperationException>(() =>
            fixture.Service.SetOrganization(new WebImageRecordEdit(image), 6, true, null));

        // Assert
        var stored = Assert.Single(storedRecords);
        Assert.Equal(4, stored.Rating);
        Assert.True(stored.Reviewed);
        Assert.Equal("Private collection", Assert.Single(stored.Collections));
        Assert.Equal("green, landscape, cinema", stored.Tags);
        Assert.Equal(image.StoredPath, stored.StoredPath);
        Assert.DoesNotContain("Private collection", Encoding.UTF8.GetString(encryptedRecord));
        Assert.IsType<InvalidOperationException>(ratingError);
        Assert.Contains("6", ratingError.Message, StringComparison.Ordinal);
        Assert.Contains("between 0 and 5", ratingError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavedViews_WhenChangedAndRestarted_RemainEncryptedIndependentAndAvailable()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var library = fixture.Service;
        var filter = new WebGalleryFilter { Collection = "private-worlds", MinRating = 4, Review = "pending", Sort = "rating" };
        // Act
        await library.SaveViewAsync("Private view", filter);
        filter = filter with { MinRating = 1 };
        var initialViews = await library.LoadViewsAsync();
        var payload = await fixture.RawStore.ReadSettingsAsync("saved-views");
        await library.SaveViewAsync("private VIEW", filter);
        var replacedViews = await library.LoadViewsAsync();
        fixture.Session.Lock();
        var lockedError = await Assert.ThrowsAsync<VaultLockedException>(() => library.LoadViewsAsync());
        fixture.Session.Unlock(await fixture.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase));
        var restartedViews = await library.LoadViewsAsync();
        await library.DeleteViewAsync("private VIEW");
        var remainingViews = await library.LoadViewsAsync();

        // Assert
        Assert.Equal(4, Assert.Single(initialViews).Filter.MinRating);
        Assert.DoesNotContain("Private view", Encoding.UTF8.GetString(payload!));
        Assert.Single(replacedViews);
        Assert.IsType<VaultLockedException>(lockedError);
        Assert.Contains("unlock", lockedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gallery", lockedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first", lockedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Assert.Single(restartedViews).Filter.MinRating);
        Assert.Empty(remainingViews);
    }

    [Fact]
    public async Task SavedViews_WhenSavedConcurrently_PreserveBothEntriesAndFilters()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var library = fixture.Service;
        // Act
        await Task.WhenAll(library.SaveViewAsync("First", new() { MinRating = 3 }),
            library.SaveViewAsync("Second", new() { Review = "pending" }));

        // Assert
        var views = (await library.LoadViewsAsync()).OrderBy(view => view.Name, StringComparer.Ordinal);
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
    public void VaultComposition_WhenNewGalleryDirectoryIsConfigured_ThenUsesThatIndependentDirectory()
    {
        // Arrange
        using var fixture = new TestVault();
        var root = Path.Combine(fixture.Root, "DiffusionGenGallery02");
        var services = new ServiceCollection();
        GalleryService.Register(services, fixture.Configuration, root);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Act
        var service = scope.ServiceProvider.GetRequiredService<GalleryService>();

        // Assert - the configured new vault directory is retained by production composition
        Assert.Equal(root, service.VaultRoot);
    }

    [Fact]
    public void GalleryFilter_WhenNewCriteriaAreUsed_ComposesWithExistingFilters()
    {
        // Arrange
        using var fixture = new TestVault();
        var first = new WebImageRecord { Collections = ["Worlds"], Rating = 4, Reviewed = false, StoredPath = "C:\\one.dpng" };
        var second = first with { Id = "second", Rating = 5, Reviewed = true };
        var third = first with { Id = "third", Collections = [], Rating = 0 };
        var filter = new WebGalleryFilter { Collection = "worlds", MinRating = 4, Review = "pending" };
        // Act
        var matching = fixture.Service.Filter([first, second, third], filter).ToArray();
        var unorganized = fixture.Service.Filter([first, second, third], new() { UnorganizedOnly = true }).ToArray();
        var ratingSorted = fixture.Service.Filter([first, second, third], new() { Sort = "rating" }).ToArray();

        // Assert
        Assert.Equal(first, Assert.Single(matching));
        Assert.Equal(third, Assert.Single(unorganized));
        Assert.Equal([second, first, third], ratingSorted);
    }

    [Fact]
    public void GalleryFilter_WhenStoredPathHasNoParent_ReturnsNoFolderMatch()
    {
        // Arrange
        using var fixture = new TestVault();
        var image = new WebImageRecord { StoredPath = "image.dpng" };

        // Act
        var matches = fixture.Service.Filter([image], new WebGalleryFilter { Folder = "source" });

        // Assert
        Assert.Empty(matches);
    }

    [Fact]
    public async Task SearchInterpretation_WhenModelReturnsValidAndInvalidFilters_ValidatesWithoutChangingRecords()
    {
        // Arrange
        using var fixture = new TestVault();
        var service = new GalleryService(fixture.Store, fixture.Vault, fixture.Session, visionService: new VisionService(new HttpClient(new JsonHandler("""
            {"search":"","tag":"landscape","collection":"Worlds","minRating":4,"favoritesOnly":true,"orientation":"landscape","review":"pending"}
            """))));
        // Act
        var filter = await service.InterpretSearchAsync(new() { Model = "local" }, "Favorite landscapes awaiting review", ["Worlds"]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InterpretSearchAsync(new() { Model = "local" }, "query", []));

        // Assert
        Assert.Equal("landscape", filter.Tag);
        Assert.Equal(4, filter.MinRating);
        Assert.True(filter.FavoritesOnly);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("Worlds", error.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("none", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectionSuggestion_WhenModelOutputIsInvalid_ReportsTheInvalidCollection()
    {
        // Arrange
        using var fixture = new TestVault();
        var service = new GalleryService(fixture.Store, fixture.Vault, fixture.Session, visionService: new VisionService(new HttpClient(new JsonHandler("""{"collection":"Worlds"}"""))));
        var invalidService = new GalleryService(fixture.Store, fixture.Vault, fixture.Session, visionService: new VisionService(new HttpClient(new JsonHandler("""{"collection":""}"""))));

        // Act
        var suggested = await service.SuggestCollectionAsync(new() { Model = "local" }, "Landscape", "green", []);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            invalidService.SuggestCollectionAsync(new() { Model = "local" }, "Landscape", "green", []));

        // Assert
        Assert.Equal("Worlds", suggested);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("collection ''", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nonempty", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("80", error.Message, StringComparison.Ordinal);
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
