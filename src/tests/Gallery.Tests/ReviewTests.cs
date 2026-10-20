using Gallery.Core.Services.Processors;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

// Direct transition tests pin review-state changes; service tests cover their persistence and file effects.
[Trait("Category", "acceptance")]
public sealed class ReviewTests
{
    [Fact]
    public async Task Organize_ColumnActionsPreserveOtherMetadata()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "organize.png", "folder");
        image = await fixture.Service.UpdateAsync(image.Id, edit => edit with
        {
            Description = "preserve", Tags = "existing", Rating = 3, IsFavorite = true
        });
        var workbench = new Gallery.Web.Components.LibraryWorkbench();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = workbench.GetType();
        type.GetProperty("Gallery", flags)!.SetValue(workbench, fixture.Service);
        type.GetProperty("Logger", flags)!.SetValue(workbench,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gallery.Web.Components.LibraryWorkbench>.Instance);
        type.GetProperty(nameof(workbench.SelectedIds))!.SetValue(workbench, new[] { image.Id });
        type.GetField("collection", flags)!.SetValue(workbench, "new collection");
        type.GetField("tags", flags)!.SetValue(workbench, "new tag");
        await (Task)type.GetMethod("ApplyCollectionAsync", flags)!.Invoke(workbench, null)!;
        var collected = Assert.Single(await fixture.Service.ListAsync());
        Assert.Contains("new collection", collected.Collections);
        Assert.Equal("existing", collected.Tags);
        await (Task)type.GetMethod("ApplyTagsAsync", flags)!.Invoke(workbench, null)!;
        var tagged = Assert.Single(await fixture.Service.ListAsync());
        Assert.Contains("existing", tagged.Tags);
        Assert.Contains("new tag", tagged.Tags);
        Assert.Equal(collected.Collections.ToArray(), tagged.Collections.ToArray());
        Assert.Equal(image.Description, tagged.Description);
        Assert.Equal(image.Rating, tagged.Rating);
        Assert.Equal(image.IsReviewed, tagged.IsReviewed);
        Assert.Equal(image.IsFavorite, tagged.IsFavorite);
    }

    [Theory]
    [InlineData(false, 2, 1)]
    [InlineData(true, 2, 0)]
    [InlineData(false, 1, 0)]
    public async Task Viewer_CelebratesOnceOnlyForAnEntirelyReviewedSession(bool hasSkipped, int reviewed, int expectedBursts)
    {
        var viewer = new Gallery.Web.Components.FullscreenViewer();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = viewer.GetType();
        type.GetProperty(nameof(viewer.Images))!.SetValue(viewer, new List<ImageRecord>
        {
            new() { Id = "first" }, new() { Id = "last" }
        });
        type.GetProperty(nameof(viewer.ReviewMode))!.SetValue(viewer, true);
        type.GetField("reviewComplete", flags)!.SetValue(viewer, true);
        type.GetField("hasSkipped", flags)!.SetValue(viewer, hasSkipped);
        var decisions = (HashSet<string>)type.GetField("decisions", flags)!.GetValue(viewer)!;
        decisions.UnionWith(new[] { "first", "last" }.Take(reviewed));
        var js = new CelebrationJsRuntime();
        type.GetProperty("JS", flags)!.SetValue(viewer, js);

        for (var render = 0; render < 2; render++)
            await (Task)type.GetMethod("OnAfterRenderAsync", flags)!.Invoke(viewer, [false])!;

        Assert.Equal(expectedBursts, js.Calls);
    }

    private sealed class CelebrationJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public int Calls { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Assert.Equal("galleryViewer.celebrate", identifier);
            Calls++;
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    [Fact]
    public async Task Viewer_ProgressCountsSkippedImagesAndOnlyCompletesWhenTheGoalIsReached()
    {
        var viewer = new Gallery.Web.Components.FullscreenViewer();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = viewer.GetType();
        type.GetProperty(nameof(viewer.Images))!.SetValue(viewer, new List<ImageRecord>
        {
            new() { Id = "first" }, new() { Id = "last" }
        });
        type.GetField("index", flags)!.SetValue(viewer, 1);
        var cache = (Dictionary<string, (string Url, long Bytes)>)type.GetField("fitCache", flags)!.GetValue(viewer)!;
        cache["first"] = ("first-url", 0);
        cache["last"] = ("last-url", 0);
        Task Skip() => (Task)type.GetMethod("SkipAsync", flags)!.Invoke(viewer, null)!;

        await Skip();

        Assert.False((bool)type.GetField("reviewComplete", flags)!.GetValue(viewer)!);
        Assert.Equal(0, type.GetField("index", flags)!.GetValue(viewer));
        Assert.Equal("0 Reviewed + 1 Skipped / 2", type.GetProperty("ReviewProgressText", flags)!.GetValue(viewer));
        Assert.True((bool)type.GetField("hasSkipped", flags)!.GetValue(viewer)!);

        await Skip();

        Assert.True((bool)type.GetField("reviewComplete", flags)!.GetValue(viewer)!);
        Assert.Equal("0 Reviewed + 2 Skipped / 2", type.GetProperty("ReviewProgressText", flags)!.GetValue(viewer));
    }

    [Fact]
    public async Task Viewer_UsesSavedToggleResultsAndCapturesUndoFromTheLatestPersistedRecord()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var stale = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "stale-review.png", "folder");
        await fixture.Service.UpdateAsync(stale.Id, edit => edit with { IsFavorite = true, Rating = 3 });
        await fixture.Service.UpdateAsync(stale.Id, edit => edit with { IsReviewed = false, Description = "latest" });
        var viewer = new Gallery.Web.Components.FullscreenViewer();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = viewer.GetType();
        type.GetProperty(nameof(viewer.Images))!.SetValue(viewer, new List<ImageRecord> { stale });
        type.GetProperty("Gallery", flags)!.SetValue(viewer, fixture.Service);
        type.GetProperty("Logger", flags)!.SetValue(viewer,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gallery.Web.Components.FullscreenViewer>.Instance);
        var cache = (Dictionary<string, (string Url, long Bytes)>)type.GetField("fitCache", flags)!.GetValue(viewer)!;
        cache[stale.Id] = ("cached-url", 0);
        Task Invoke(string method, params object[] args) => (Task)type.GetMethod(method, flags)!.Invoke(viewer, args)!;
        ImageRecord Current() => (ImageRecord)type.GetProperty("Current", flags)!.GetValue(viewer)!;

        await Invoke("FavoriteAsync");

        Assert.False(Current().IsFavorite);
        Assert.Equal("latest", Current().Description);
        await fixture.Service.UpdateAsync(stale.Id, edit => edit with { Rating = 2, IsFavorite = true });
        var beforeReview = fixture.Service.CaptureReviewState(await fixture.Service.UpdateAsync(stale.Id,
            edit => edit with { IsReviewed = false, Description = "latest review note" }));
        Assert.Equal(3, Current().Rating);
        Assert.Equal(2, beforeReview.Rating);
        Assert.False(beforeReview.IsReviewed);
        await Invoke("DecideAsync", ReviewAction.Rate, 4);
        Assert.Equal(4, Current().Rating);
        Assert.True(Current().IsReviewed);
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
            Rating = rating, IsMarkedForDeletion = markedForDeletion, IsFavorite = favorite
        });
        var unreviewed = await fixture.Service.UpdateAsync(image.Id, edit => edit with { IsReviewed = false });
        var snapshot = fixture.Service.CaptureReviewState(unreviewed);
        await fixture.Service.UpdateAsync(image.Id, edit => fixture.Service.ApplyReviewDecision(edit, ReviewAction.Rate, 4));
        await fixture.Service.UpdateAsync(image.Id, edit => edit with { Description = "new note", Tags = "new tag" });

        var restored = await fixture.Service.RestoreReviewStateAsync(image.Id, snapshot);
        var stored = Assert.Single(await fixture.Service.ListAsync());

        Assert.Equal(snapshot, fixture.Service.CaptureReviewState(restored));
        Assert.Equal(snapshot, fixture.Service.CaptureReviewState(stored));
        Assert.False(restored.IsReviewed);
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

        var invalidRating = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.RestoreReviewStateAsync("image", new(false, false, false, rating)));
        AsserterExceptionContract.AssertMessageContains(
            [rating.ToString(System.Globalization.CultureInfo.InvariantCulture), "rating", "0", "5"], invalidRating.Message);

        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task EditProjection_UsesTheSameDomainRulesAsPersistedUpdates()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "projection.png", "folder");
        ImageRecordEdit Edit(ImageRecordEdit edit) => fixture.Service.AddCollection(
            edit with { Rating = 4, IsFavorite = true }, " collection ");

        var projected = fixture.Service.ApplyEdit(image, Edit);
        await fixture.Service.UpdateAsync(image.Id, Edit);
        var stored = Assert.Single(await fixture.Service.ListAsync());

        Assert.Equal(4, projected.Rating);
        Assert.True(projected.IsFavorite);
        Assert.True(projected.IsReviewed);
        Assert.Equal(["collection"], projected.Collections.ToArray());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(stored),
            System.Text.Json.JsonSerializer.Serialize(projected));
        Assert.False(image.IsReviewed);
        Assert.Empty(image.Collections);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void CollectionChanges_RejectInvalidBoundaryValuesWithoutAccessingTheVault(string collection)
    {
        using var fixture = new TestVault();
        var edit = new ImageRecordEdit(new ImageRecord());

        var expectedPhrases = new[] { $"'{collection}'", "collection", "1", "80" };
        var invalidAddition = Assert.Throws<InvalidOperationException>(() => fixture.Service.AddCollection(edit, collection));
        var invalidRemoval = Assert.Throws<InvalidOperationException>(() => fixture.Service.RemoveCollection(edit, collection));
        AsserterExceptionContract.AssertMessageContains(expectedPhrases, invalidAddition.Message);
        AsserterExceptionContract.AssertMessageContains(expectedPhrases, invalidRemoval.Message);
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
        await fixture.Service.UpdateAsync(marked.Id, image => image with { IsMarkedForDeletion = true });

        var stored = (await fixture.Service.ListAsync()).ToDictionary(image => image.Id);
        Assert.True(stored[rated.Id].IsReviewed);
        Assert.True(stored[marked.Id].IsReviewed);
    }

    [Fact]
    public async Task Review_WhenUndoIsApplied_RestoresCurationWithoutChangingNotesOrFiles()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "undo.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image with { Rating = 3, IsFavorite = true, Tags = "original" });
        var before = fixture.Service.CaptureReviewState(Assert.Single(await fixture.Service.ListAsync()));
        // Act
        await fixture.Service.UpdateAsync(record.Id, image => fixture.Service.ApplyReviewDecision(image, ReviewAction.MarkDeletion));
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
        Assert.True(stored.IsReviewed);
        Assert.Equal(action == ReviewAction.MarkDeletion, stored.IsMarkedForDeletion);
        Assert.Equal(action == ReviewAction.Favorite, stored.IsFavorite);
        Assert.Equal(action == ReviewAction.Rate ? 4 : 0, stored.Rating);
        Assert.True(File.Exists(stored.StoredPath));
    }

    [Fact]
    public void ReviewDecision_WhenDeletionIsRevisedToKeep_UnmarksWithoutErasingOtherCuration()
    {
        // Arrange
        using var fixture = new TestVault();
        var record = new ImageRecordEdit(new ImageRecord { Rating = 5, IsFavorite = true, IsMarkedForDeletion = true });

        // Act
        record = fixture.Service.ApplyReviewDecision(record, ReviewAction.Keep);

        // Assert
        Assert.True(record.IsReviewed);
        Assert.False(record.IsMarkedForDeletion);
        Assert.True(record.IsFavorite);
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
        var reviewedAfterInvalidRating = record.IsReviewed;
        var actionError = Record.Exception(() => fixture.Service.ApplyReviewDecision(record, (ReviewAction)99));

        // Assert
        Assert.IsType<InvalidOperationException>(ratingError);
        Assert.Contains("Rating 0", ratingError!.Message, StringComparison.Ordinal);
        Assert.Contains("between 1 and 5", ratingError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(reviewedAfterInvalidRating);
        Assert.False(record.IsReviewed);
        Assert.IsType<InvalidOperationException>(actionError);
        Assert.Contains("99", actionError!.Message, StringComparison.Ordinal);
        Assert.Contains("Keep", actionError.Message, StringComparison.Ordinal);
        Assert.Contains("Favorite", actionError.Message, StringComparison.Ordinal);
        Assert.Contains("MarkDeletion", actionError.Message, StringComparison.Ordinal);
        Assert.Contains("Rate", actionError.Message, StringComparison.Ordinal);
    }
}
