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
    public async Task Viewer_UsesSavedToggleResultsAndCapturesUndoFromTheLatestPersistedRecord()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var stale = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "stale-review.png", "folder");
        await fixture.Service.UpdateAsync(stale.Id, edit => edit with { Favorite = true, Rating = 3 });
        await fixture.Service.UpdateAsync(stale.Id, edit => edit with { Reviewed = false, Description = "latest" });
        var viewer = new Gallery.Web.Components.FullscreenViewer();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = viewer.GetType();
        type.GetProperty(nameof(viewer.Images))!.SetValue(viewer, new List<WebImageRecord> { stale });
        type.GetProperty("Gallery", flags)!.SetValue(viewer, fixture.Service);
        type.GetProperty("Logger", flags)!.SetValue(viewer,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gallery.Web.Components.FullscreenViewer>.Instance);
        var cache = (Dictionary<string, (string Url, long Bytes)>)type.GetField("fitCache", flags)!.GetValue(viewer)!;
        cache[stale.Id] = ("cached-url", 0);
        Task Invoke(string method, params object[] args) => (Task)type.GetMethod(method, flags)!.Invoke(viewer, args)!;
        WebImageRecord Current() => (WebImageRecord)type.GetProperty("Current", flags)!.GetValue(viewer)!;

        await Invoke("FavoriteAsync");

        Assert.False(Current().Favorite);
        Assert.Equal("latest", Current().Description);
        await fixture.Service.UpdateAsync(stale.Id, edit => edit with { Rating = 2, Favorite = true });
        var beforeReview = fixture.Service.CaptureReviewState(await fixture.Service.UpdateAsync(stale.Id,
            edit => edit with { Reviewed = false, Description = "latest review note" }));
        Assert.Equal(3, Current().Rating);
        Assert.Equal(2, beforeReview.Rating);
        Assert.False(beforeReview.Reviewed);
        await Invoke("DecideAsync", WebReviewAction.Rate, 4);
        Assert.Equal(4, Current().Rating);
        Assert.True(Current().Reviewed);
        await Invoke("UndoAsync");

        Assert.Equal("", type.GetField("error", flags)!.GetValue(viewer));
        Assert.Equal(beforeReview, fixture.Service.CaptureReviewState(Current()));
        Assert.Equal(beforeReview, fixture.Service.CaptureReviewState(Assert.Single(await fixture.Service.ListAsync())));
        Assert.Equal("latest review note", Current().Description);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(3, false, false)]
    [InlineData(5, true, false)]
    [InlineData(3, true, true)]
    public async Task Undo_RestoresTheExactSnapshotWithoutInferringAReviewDecision(
        int rating, bool markedForDeletion, bool favorite)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "undo-rating.png", "folder");
        await fixture.Service.UpdateAsync(image.Id, edit => edit with
        {
            Rating = rating, MarkedForDeletion = markedForDeletion, Favorite = favorite
        });
        var unreviewed = await fixture.Service.UpdateAsync(image.Id, edit => edit with { Reviewed = false });
        var snapshot = fixture.Service.CaptureReviewState(unreviewed);
        await fixture.Service.UpdateAsync(image.Id, edit => fixture.Service.ApplyReviewDecision(edit, WebReviewAction.Rate, 4));
        await fixture.Service.UpdateAsync(image.Id, edit => edit with { Description = "new note", Tags = "new tag" });

        var restored = await fixture.Service.RestoreReviewStateAsync(image.Id, snapshot);
        var stored = Assert.Single(await fixture.Service.ListAsync());

        Assert.Equal(snapshot, fixture.Service.CaptureReviewState(restored));
        Assert.Equal(snapshot, fixture.Service.CaptureReviewState(stored));
        Assert.False(restored.Reviewed);
        Assert.Equal("new note", restored.Description);
        Assert.Equal("new tag", restored.Tags);
        Assert.Equal(image.StoredPath, restored.StoredPath);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public async Task Undo_ValidatesTheSnapshotBeforeAccessingTheVault(int rating)
    {
        using var fixture = new TestVault();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.RestoreReviewStateAsync("image", new(false, false, false, rating)));

        Assert.False(Directory.Exists(fixture.Root));
    }

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
        await fixture.Service.RestoreReviewStateAsync(record.Id, before);
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
