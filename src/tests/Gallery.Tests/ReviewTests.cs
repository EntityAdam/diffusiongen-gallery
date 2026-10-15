using Gallery.Core.Processors;
using Gallery.Web.Exceptions;
using Gallery.Web.Models;
using Gallery.Web.Services;

namespace Gallery.Tests;

// Direct transition tests pin review-state changes; service tests cover their persistence and file effects.
[Trait("Category", "acceptance")]
public sealed class ReviewTests
{
    [Fact]
    public async Task EditProjection_UsesTheSameDomainRulesAsPersistedUpdates()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "projection.png", "folder");
        WebImageRecordEdit Edit(WebImageRecordEdit edit) => fixture.Service.AddCollection(
            edit with { Rating = 4, Favorite = true }, " collection ");

        var projected = fixture.Service.ApplyEdit(image, Edit);
        await fixture.Service.UpdateAsync(image.Id, Edit);
        var stored = Assert.Single(await fixture.Service.ListAsync());

        Assert.Equal(4, projected.Rating);
        Assert.True(projected.Favorite);
        Assert.True(projected.Reviewed);
        Assert.Equal(["collection"], projected.Collections.ToArray());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(stored),
            System.Text.Json.JsonSerializer.Serialize(projected));
        Assert.False(image.Reviewed);
        Assert.Empty(image.Collections);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void CollectionChanges_RejectInvalidBoundaryValuesWithoutAccessingTheVault(string collection)
    {
        using var fixture = new TestVault();
        var edit = new WebImageRecordEdit(new WebImageRecord());

        Assert.Throws<InvalidOperationException>(() => fixture.Service.AddCollection(edit, collection));
        Assert.Throws<InvalidOperationException>(() => fixture.Service.RemoveCollection(edit, collection));
        Assert.Empty(edit.Collections);
        Assert.False(Directory.Exists(fixture.Root));
    }

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
        await fixture.Service.UpdateAsync(record.Id, image => fixture.Service.ApplyReviewDecision(image, WebReviewAction.MarkDeletion));
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
    [InlineData(WebReviewAction.Keep)]
    [InlineData(WebReviewAction.Favorite)]
    [InlineData(WebReviewAction.MarkDeletion)]
    [InlineData(WebReviewAction.Rate)]
    // Every action should mark the item reviewed while preserving the original file.
    public async Task ReviewDecision_WhenActionIsApplied_PersistsWithoutRemovingFiles(WebReviewAction action)
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
        Assert.Equal(action == WebReviewAction.MarkDeletion, stored.MarkedForDeletion);
        Assert.Equal(action == WebReviewAction.Favorite, stored.Favorite);
        Assert.Equal(action == WebReviewAction.Rate ? 4 : 0, stored.Rating);
        Assert.True(File.Exists(stored.StoredPath));
    }

    [Fact]
    public void ReviewDecision_WhenDeletionIsRevisedToKeep_UnmarksWithoutErasingOtherCuration()
    {
        // Arrange
        using var fixture = new TestVault();
        var record = new WebImageRecordEdit(new WebImageRecord { Rating = 5, Favorite = true, MarkedForDeletion = true });

        // Act
        record = fixture.Service.ApplyReviewDecision(record, WebReviewAction.Keep);

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
        var record = new WebImageRecordEdit(new WebImageRecord());

        // Act
        var ratingError = Record.Exception(() => fixture.Service.ApplyReviewDecision(record, WebReviewAction.Rate, 0));
        var reviewedAfterInvalidRating = record.Reviewed;
        var actionError = Record.Exception(() => fixture.Service.ApplyReviewDecision(record, (WebReviewAction)99));

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
