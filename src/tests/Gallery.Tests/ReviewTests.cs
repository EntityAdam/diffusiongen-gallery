using Gallery.Core.Processors;
using Gallery.Core;
using Gallery.Web.Models;
using Gallery.Web.Services;

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
        Assert.True(stored[rated.Id].Reviewed);
        Assert.True(stored[marked.Id].Reviewed);
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
        Assert.True(File.Exists(restored.StoredPath));
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
        Assert.True(stored.Reviewed);
        Assert.Equal(action == ReviewAction.MarkDeletion, stored.MarkedForDeletion);
        Assert.Equal(action == ReviewAction.Favorite, stored.Favorite);
        Assert.Equal(action == ReviewAction.Rate ? 4 : 0, stored.Rating);
        Assert.True(File.Exists(stored.StoredPath));
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
        Assert.True(record.Reviewed);
        Assert.False(record.MarkedForDeletion);
        Assert.True(record.Favorite);
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
        Assert.IsType<InvalidOperationException>(ratingError);
        Assert.Contains("Rating 0", ratingError!.Message, StringComparison.Ordinal);
        Assert.Contains("between 1 and 5", ratingError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(reviewedAfterInvalidRating);
        Assert.False(record.Reviewed);
        Assert.IsType<InvalidOperationException>(actionError);
        Assert.Contains("99", actionError!.Message, StringComparison.Ordinal);
        Assert.Contains("Keep", actionError.Message, StringComparison.Ordinal);
        Assert.Contains("Favorite", actionError.Message, StringComparison.Ordinal);
        Assert.Contains("MarkDeletion", actionError.Message, StringComparison.Ordinal);
        Assert.Contains("Rate", actionError.Message, StringComparison.Ordinal);
    }
}
