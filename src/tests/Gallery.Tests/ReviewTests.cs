using Gallery.Core.Processors;
using Gallery.Core;
using Gallery.Models;
using Gallery.Services;

namespace Gallery.Tests;

// Direct transition tests pin review-state changes; service tests cover their persistence and file effects.
[Trait("Category", "acceptance")]
public sealed class ReviewTests
{
    [Fact]
    public async Task Curation_WhenRatingOrDeletionMarkIsApplied_PersistsAsReviewed()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var rated = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 20)), "rated.png", "folder");
        var marked = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 10)), "marked.png", "folder");

        await fixture.Service.UpdateAsync(rated.Id, image => image with { Rating = 3 });
        await fixture.Service.UpdateAsync(marked.Id, image => image with { MarkedForDeletion = true });

        var stored = (await fixture.Service.ListAsync()).ToDictionary(image => image.Id);
        TestAssertions.True(stored[rated.Id].Reviewed, "assigning a rating marks the persisted record reviewed");
        TestAssertions.True(stored[marked.Id].Reviewed, "marking for deletion marks the persisted record reviewed");
    }

    [Fact]
    public async Task Review_WhenUndoIsApplied_RestoresCurationWithoutChangingNotesOrFiles()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "undo.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image with { Rating = 3, Favorite = true, Tags = "original" });
        var before = fixture.Service.CaptureReviewState(Assert.Single(await fixture.Service.ListAsync()));
        // Act
        await fixture.Service.UpdateAsync(record.Id, image => fixture.Service.ApplyReviewDecision(image, ReviewAction.MarkDeletion));
        await fixture.Service.UpdateAsync(record.Id, image => image with { Description = "New note" });
        await fixture.Service.UpdateAsync(record.Id, image => fixture.Service.RestoreReviewState(image, before));
        // Assert
        var restored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(before, fixture.Service.CaptureReviewState(restored));
        Assert.Equal("original", restored.Tags);
        Assert.Equal("New note", restored.Description);
        TestAssertions.True(File.Exists(restored.StoredPath), $"the original image remains at {restored.StoredPath}");
    }

    [Theory]
    [InlineData(ReviewAction.Keep)]
    [InlineData(ReviewAction.Favorite)]
    [InlineData(ReviewAction.MarkDeletion)]
    [InlineData(ReviewAction.Rate)]
    // Every action should mark the item reviewed while preserving the original file.
    public async Task ReviewDecision_WhenActionIsApplied_PersistsWithoutRemovingFiles(ReviewAction action)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "review.png", "folder");
        // Act
        await fixture.Service.UpdateAsync(record.Id, image => fixture.Service.ApplyReviewDecision(image, action, 4));

        // Assert
        var stored = Assert.Single(await fixture.Service.ListAsync());
        TestAssertions.True(stored.Reviewed, "the chosen review action marks the record reviewed");
        Assert.Equal(action == ReviewAction.MarkDeletion, stored.MarkedForDeletion);
        Assert.Equal(action == ReviewAction.Favorite, stored.Favorite);
        Assert.Equal(action == ReviewAction.Rate ? 4 : 0, stored.Rating);
        TestAssertions.True(File.Exists(stored.StoredPath), $"the original image remains at {stored.StoredPath}");
    }

    [Fact]
    public void ReviewDecision_WhenDeletionIsRevisedToKeep_UnmarksWithoutErasingOtherCuration()
    {
        // Arrange
        using var fixture = new TestVault();
        var record = new ImageRecordEdit(new ImageRecord { Rating = 5, Favorite = true, MarkedForDeletion = true });

        // Act
        record = fixture.Service.ApplyReviewDecision(record, ReviewAction.Keep);

        // Assert
        TestAssertions.True(record.Reviewed, "the keep decision marks the record reviewed");
        TestAssertions.False(record.MarkedForDeletion, "keeping the record clears its deletion mark");
        TestAssertions.True(record.Favorite, "keeping the record preserves its favorite mark");
        Assert.Equal(5, record.Rating);
    }

    [Fact]
    public void ReviewDecision_WhenRatingOrActionIsInvalid_ExplainsValidationAndPreservesState()
    {
        // Arrange
        using var fixture = new TestVault();
        var record = new ImageRecordEdit(new ImageRecord());

        // Act
        var ratingError = Record.Exception(() => fixture.Service.ApplyReviewDecision(record, ReviewAction.Rate, 0));
        var reviewedAfterInvalidRating = record.Reviewed;
        var actionError = Record.Exception(() => fixture.Service.ApplyReviewDecision(record, (ReviewAction)99));

        // Assert
        TestAssertions.Exception<InvalidOperationException>(new ExpectedException("the validation message identifies the invalid rating and both allowed bounds", "Rating 0", "between", "1", "5"), ratingError);
        TestAssertions.False(reviewedAfterInvalidRating, "invalid rating leaves the review state unchanged");
        TestAssertions.False(record.Reviewed, "invalid rating leaves the review state unchanged");
        TestAssertions.Exception<InvalidOperationException>(new ExpectedException("the validation message identifies the invalid action and choices", "99", "Keep", "Favorite", "MarkDeletion", "Rate"), actionError);
    }
}
