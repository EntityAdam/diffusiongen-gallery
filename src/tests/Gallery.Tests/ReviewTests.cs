using Gallery.Models;
using Gallery.Services;

namespace Gallery.Tests;

public sealed class ReviewTests
{
    [Fact]
    public async Task UndoRestoresAllCurationFlagsWithoutChangingNotesOrFiles()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "undo.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => { image.Rating = 3; image.Favorite = true; image.Tags = "original"; });
        var before = ReviewState.Capture(Assert.Single(await fixture.Service.ListAsync()));
        await fixture.Service.UpdateAsync(record.Id, image => ReviewDecision.Apply(image, ReviewAction.MarkDeletion));
        await fixture.Service.UpdateAsync(record.Id, image => image.Description = "New note");
        await fixture.Service.UpdateAsync(record.Id, before.Restore);
        var restored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(before, ReviewState.Capture(restored));
        Assert.Equal("original", restored.Tags);
        Assert.Equal("New note", restored.Description);
        Assert.True(File.Exists(restored.StoredPath));
    }

    [Theory]
    [InlineData(ReviewAction.Keep)]
    [InlineData(ReviewAction.Favorite)]
    [InlineData(ReviewAction.MarkDeletion)]
    [InlineData(ReviewAction.Rate)]
    public async Task ReviewDecisionsPersistWithoutRemovingFiles(ReviewAction action)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "review.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => ReviewDecision.Apply(image, action, 4));
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.True(stored.Reviewed);
        Assert.Equal(action == ReviewAction.MarkDeletion, stored.MarkedForDeletion);
        Assert.Equal(action == ReviewAction.Favorite, stored.Favorite);
        Assert.Equal(action == ReviewAction.Rate ? 4 : 0, stored.Rating);
        Assert.True(File.Exists(stored.StoredPath));
    }

    [Fact]
    public void RevisingDeletionWithKeepUnmarksWithoutErasingRatingOrFavorite()
    {
        var record = new ImageRecord { Rating = 5, Favorite = true, MarkedForDeletion = true };
        ReviewDecision.Apply(record, ReviewAction.Keep);
        Assert.True(record.Reviewed);
        Assert.False(record.MarkedForDeletion);
        Assert.True(record.Favorite);
        Assert.Equal(5, record.Rating);
    }

    [Fact]
    public void InvalidRatingDoesNotChangeReviewState()
    {
        var record = new ImageRecord();
        Assert.Throws<InvalidOperationException>(() => ReviewDecision.Apply(record, ReviewAction.Rate, 0));
        Assert.False(record.Reviewed);
        Assert.Throws<InvalidOperationException>(() => ReviewDecision.Apply(record, (ReviewAction)99));
    }
}
