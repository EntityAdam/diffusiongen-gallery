using Gallery.Core.Models;
using static Gallery.Tests.BrowsingWorkspaceFixture;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class GuidedReviewTests
{
    [Fact]
    public void GuidedReview_WhenStartedWithTheDefaultGoal_ThenOpensADetachedReviewOfTenEligibleImagesAcrossPagesInSortOrder()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(30);
        imageRecords[0] = imageRecords[0] with { IsReviewed = true };
        imageRecords[1] = imageRecords[1] with { IsMarkedForDeletion = true };
        using var home = CreateSession(testVault, imageRecords);
        ComponentTestAccess.Set(home, "Browser.Filter", new GalleryFilter { Sort = "name" });
        ComponentTestAccess.Set(home, "Browser.Density", "detail");
        ComponentTestAccess.Set(home, "Browser.Page", 1);
        const int ExpectedReviewGoal = 10;
        var expectedImageIds = imageRecords.Skip(2).Take(ExpectedReviewGoal).Select(imageRecord => imageRecord.Id).ToArray();
        var expectedScenario = ExpectedScenario.CreateFrom("Default review selects ten eligible images in library sort order, independent of pagination, in a detached session.", new
        {
            Goal = ExpectedReviewGoal, ImageIds = expectedImageIds, InitialId = expectedImageIds[0], ReviewMode = true, IsDetachedSnapshot = true
        });

        // Act
        home.Presentation.StartReview(home.Browser.ReviewPool, home.Browser.ReviewGoal, home.Browser.ReviewRandomOrder);

        // Assert - gather the review session
        var actualImageRecords = home.Presentation.ViewerImages;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Goal = home.Browser.ReviewGoal, ImageIds = actualImageRecords.Select(imageRecord => imageRecord.Id).ToArray(),
            InitialId = home.Presentation.ViewerInitialId, ReviewMode = home.Presentation.ReviewMode,
            IsDetachedSnapshot = actualImageRecords.Count > 0 && !ReferenceEquals(imageRecords[2], actualImageRecords[0])
        });
    }

    // Review goals are clamped to at least one and at most the eligible filtered pool.
    [Theory]
    [InlineData(100, 6)]
    [InlineData(3, 3)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void GuidedReview_WhenTheGoalIsOutsideThePoolBounds_ThenClampsTheGoalAndSession(int requestedGoal, int expectedGoal)
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(20);
        for (var index = 0; index < imageRecords.Count; index++)
            imageRecords[index] = imageRecords[index] with
            {
                IsFavorite = index < 8, IsReviewed = index == 0, IsMarkedForDeletion = index == 1
            };
        using var home = CreateSession(testVault, imageRecords);
        ComponentTestAccess.Set(home, "Browser.Filter", new GalleryFilter { IncludesFavoritesOnly = true });
        home.Browser.ReviewGoal = requestedGoal;
        var expectedScenario = ExpectedScenario.CreateFrom("Review goals and session sizes stay within the eligible filtered pool.",
            new { Goal = expectedGoal, SessionCount = expectedGoal, AllEligible = true });

        // Act
        home.Presentation.StartReview(home.Browser.ReviewPool, home.Browser.ReviewGoal, home.Browser.ReviewRandomOrder);

        // Assert - gather the review session
        var actualImageRecords = home.Presentation.ViewerImages;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Goal = home.Browser.ReviewGoal, SessionCount = actualImageRecords.Count,
            AllEligible = actualImageRecords.All(imageRecord => imageRecord.IsFavorite && !imageRecord.IsReviewed && !imageRecord.IsMarkedForDeletion)
        });
    }

    [Fact]
    public void GuidedReview_WhenRandomOrderIsSelected_ThenChoosesUniqueEligibleImagesWithoutChangingTheLibrary()
    {
        // Arrange
        const int ExpectedReviewGoal = 7;
        using var testVault = new TestVault();
        var imageRecords = CreateImages(80);
        for (var index = 0; index < imageRecords.Count; index++)
            imageRecords[index] = imageRecords[index] with
            {
                IsFavorite = index % 2 == 0, IsReviewed = index < 10, IsMarkedForDeletion = index > 70
            };
        using var home = CreateSession(testVault, imageRecords);
        ComponentTestAccess.Set(home, "Browser.Filter", new GalleryFilter { IncludesFavoritesOnly = true });
        home.Browser.ReviewRandomOrder = true;
        home.Browser.ReviewGoal = ExpectedReviewGoal;
        var eligibleImageIds = imageRecords.Where(imageRecord => imageRecord.IsFavorite && !imageRecord.IsReviewed && !imageRecord.IsMarkedForDeletion)
            .Select(imageRecord => imageRecord.Id).ToHashSet(StringComparer.Ordinal);
        var expectedScenario = ExpectedScenario.CreateFrom("Random review selects unique eligible images without reordering the library.", new
        {
            SessionCount = ExpectedReviewGoal, UniqueCount = ExpectedReviewGoal, AllEligible = true,
            LibraryOrder = imageRecords.Select(imageRecord => imageRecord.Id).ToArray()
        });

        // Act
        home.Presentation.StartReview(home.Browser.ReviewPool, home.Browser.ReviewGoal, home.Browser.ReviewRandomOrder);

        // Assert - gather the review session
        var actualImageRecords = home.Presentation.ViewerImages;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            SessionCount = actualImageRecords.Count, UniqueCount = actualImageRecords.Select(imageRecord => imageRecord.Id).Distinct().Count(),
            AllEligible = actualImageRecords.All(imageRecord => eligibleImageIds.Contains(imageRecord.Id)),
            LibraryOrder = imageRecords.Select(imageRecord => imageRecord.Id).ToArray()
        });
    }

    // The displayed goal and a new session both shrink to the remaining eligible pool, including zero.
    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    public void GuidedReview_WhenTheEligiblePoolShrinks_ThenCapsTheDisplayedGoalAndSession(bool includesFavorites, int expectedCount)
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(20);
        for (var index = 0; index < 3; index++) imageRecords[index] = imageRecords[index] with { IsFavorite = true };
        using var home = CreateSession(testVault, imageRecords);
        ComponentTestAccess.Set(home, "Browser.Filter", includesFavorites
            ? new GalleryFilter { IncludesFavoritesOnly = true }
            : new GalleryFilter { MinRating = 5 });
        var expectedScenario = ExpectedScenario.CreateFrom("A reduced eligible pool caps both the displayed goal and the next review session.",
            new { Goal = expectedCount, SessionCount = expectedCount });

        // Act
        home.Presentation.StartReview(home.Browser.ReviewPool, home.Browser.ReviewGoal, home.Browser.ReviewRandomOrder);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario,
            new { Goal = home.Browser.ReviewGoal, SessionCount = home.Presentation.ViewerImages.Count });
    }
}
