using System.Text;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Composition;
using Microsoft.Extensions.DependencyInjection;

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
        var image = new ImageRecordEdit(new ImageRecord { Rating = 4, IsReviewed = true, IsFavorite = true, IsMarkedForDeletion = true });

        // Act
        image = fixture.Scope.Records.SetCuration(image, rating, reviewed);

        // Assert
        Assert.Equal(expectedRating, image.Rating);
        Assert.Equal(expectedReviewed, image.IsReviewed);
        Assert.True(image.IsFavorite);
        Assert.True(image.IsMarkedForDeletion);
    }

    [Fact]
    public void BulkCuration_WhenRatingExceedsRange_ReportsLimitsWithoutChangingReviewStatus()
    {
        // Arrange
        using var fixture = new TestVault();
        var image = new ImageRecordEdit(new ImageRecord { Rating = 4, IsReviewed = true });

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Scope.Records.SetCuration(image, 6, false));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("6", error.Message, StringComparison.Ordinal);
        Assert.Contains("between 0 and 5", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, image.Rating);
        Assert.True(image.IsReviewed);
    }

    // Nonzero ratings mark the image reviewed; zero and absent ratings do not.
    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void Curation_WhenRatingIsAssigned_MarksReviewedOnlyForNonzeroRatings(int? rating, bool expectedReviewed)
    {
        // Arrange
        using var fixture = new TestVault();
        var image = new ImageRecordEdit(new ImageRecord { IsReviewed = false });

        // Act
        image = fixture.Scope.Records.SetCuration(image, rating, false);

        // Assert
        Assert.Equal(expectedReviewed, image.IsReviewed);
    }

    // Marking for deletion marks reviewed; clearing the deletion mark does not.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void DeletionMark_WhenChanged_OnlyMarkingReviewed(bool markedForDeletion, bool expectedReviewed)
    {
        // Arrange
        using var fixture = new TestVault();
        var image = new ImageRecordEdit(new ImageRecord { IsReviewed = false });

        // Act
        image = fixture.Scope.Records.SetDeletionMark(image, markedForDeletion);

        // Assert
        Assert.Equal(markedForDeletion, image.IsMarkedForDeletion);
        Assert.Equal(expectedReviewed, image.IsReviewed);
    }

    [Fact]
    public async Task CurationAndTags_WhenAppliedToImage_PersistEncryptedWithoutMovingFiles()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        // Act
        await fixture.Scope.Records.UpdateAsync(image.Id, current =>
        {
            current = fixture.Scope.Records.SetCuration(current, 4, true);
            return current with { Tags = fixture.Scope.Records.MergeTags("green, landscape", "GREEN, cinema") };
        });
        var storedRecords = await fixture.Scope.Catalog.ListAsync();
        var encryptedRecord = await fixture.RawStore.ReadRecordAsync(image.Id);
        var ratingError = Assert.Throws<InvalidOperationException>(() =>
            fixture.Scope.Records.SetCuration(new ImageRecordEdit(image), 6, true));

        // Assert
        var stored = Assert.Single(storedRecords);
        Assert.Equal(4, stored.Rating);
        Assert.True(stored.IsReviewed);
        Assert.Equal("green, landscape, cinema", stored.Tags);
        Assert.Equal(image.StoredPath, stored.StoredPath);
        Assert.DoesNotContain("green, landscape, cinema", Encoding.UTF8.GetString(encryptedRecord));
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
        var settings = fixture.Scope.Settings;
        var filter = new GalleryFilter { Collection = "private-worlds", MinRating = 4, Review = "pending", Sort = "rating" };
        // Act
        await settings.SaveViewAsync("Private view", filter);
        filter = filter with { MinRating = 1 };
        var initialViews = await settings.LoadViewsAsync();
        var payload = await fixture.RawStore.ReadSettingsAsync("saved-views");
        await settings.SaveViewAsync("private VIEW", filter);
        var replacedViews = await settings.LoadViewsAsync();
        fixture.Session.Lock();
        var lockedError = await Assert.ThrowsAsync<VaultLockedException>(() => settings.LoadViewsAsync());
        fixture.Session.Unlock(await fixture.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase));
        var restartedViews = await settings.LoadViewsAsync();
        await settings.DeleteViewAsync("private VIEW");
        var remainingViews = await settings.LoadViewsAsync();

        // Assert
        Assert.Equal(4, Assert.Single(initialViews).GalleryFilter.MinRating);
        Assert.DoesNotContain("Private view", Encoding.UTF8.GetString(payload!));
        Assert.Single(replacedViews);
        var expectedLocked = ExpectedExceptionContract.Create(typeof(VaultLockedException), ["unlock", "gallery", "first"],
            System.Net.HttpStatusCode.BadRequest, Severity.Error, "Unlock the gallery first.", "Vault.AcquireKey",
            new Dictionary<string, object?> { ["Vault.State"] = "locked" });
        AsserterExceptionContract.AssertMatches(expectedLocked, lockedError);
        Assert.Equal(1, Assert.Single(restartedViews).GalleryFilter.MinRating);
        Assert.Empty(remainingViews);
    }

    [Fact]
    public async Task SavedViews_WhenSavedConcurrently_PreserveBothEntriesAndFilters()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var settings = fixture.Scope.Settings;
        // Act
        await Task.WhenAll(settings.SaveViewAsync("First", new() { MinRating = 3 }),
            settings.SaveViewAsync("Second", new() { Review = "pending" }));

        // Assert
        var views = (await settings.LoadViewsAsync()).OrderBy(view => view.Name, StringComparer.Ordinal);
        Assert.Collection(views,
            first =>
            {
                Assert.Equal("First", first.Name);
                Assert.Equal(3, first.GalleryFilter.MinRating);
            },
            second =>
            {
                Assert.Equal("Second", second.Name);
                Assert.Equal("pending", second.GalleryFilter.Review);
            });
    }

    [Fact]
    public void VaultComposition_WhenNewGalleryDirectoryIsConfigured_ThenUsesThatIndependentDirectory()
    {
        // Arrange
        using var fixture = new TestVault();
        var root = Path.Combine(fixture.Root, "DiffusionGenGallery");
        var services = new ServiceCollection();
        GalleryServiceRegistration.Register(services, fixture.Configuration, root);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Act
        var vault = scope.ServiceProvider.GetRequiredService<IGalleryVault>();

        // Assert - the configured new vault directory is retained by production composition
        Assert.Equal(root, vault.VaultRoot);
    }

    [Fact]
    public void GalleryFilter_WhenNewCriteriaAreUsed_ComposesWithExistingFilters()
    {
        // Arrange
        using var fixture = new TestVault();
        var first = new ImageRecord { Collections = ["Worlds"], Rating = 4, IsReviewed = false, StoredPath = "C:\\one.dpng" };
        var second = first with { Id = "second", Rating = 5, IsReviewed = true };
        var third = first with { Id = "third", Collections = [], Rating = 0 };
        var filter = new GalleryFilter { Collection = "worlds", MinRating = 4, Review = "pending" };
        // Act
        var matching = fixture.Scope.Catalog.Filter([first, second, third], filter);
        var unorganized = fixture.Scope.Catalog.Filter([first, second, third], new() { IncludesUnorganizedOnly = true });
        var ratingSorted = fixture.Scope.Catalog.Filter([first, second, third], new() { Sort = "rating" });

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
        var image = new ImageRecord { StoredPath = "image.dpng" };

        // Act
        var matches = fixture.Scope.Catalog.Filter([image], new GalleryFilter { Folder = "source" });

        // Assert
        Assert.Empty(matches);
    }


}
