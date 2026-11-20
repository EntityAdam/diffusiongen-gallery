using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Features.Collections;
using Gallery.Web.Components.Features.Curation;
using Gallery.Web.Components.Features.Media;
using Gallery.Web.Components.Features.Vision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShadCn.Blazor.Components;

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
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "organize.png", "folder");
        image = await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with
        {
            Description = "preserve", Tags = "existing", Rating = 3, IsFavorite = true
        });
        var services = new ServiceCollection();
        services.AddShadCnBlazorComponents();
        using var provider = services.BuildServiceProvider();
        using var workbench = CreateWorkbench(fixture.Scope, provider.GetRequiredService<ISonnerService>());
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = workbench.GetType();
        type.GetProperty(nameof(workbench.SelectedIds))!.SetValue(workbench, new[] { image.Id });
        var collection = await fixture.Scope.Collections.CreateCollectionAsync("new collection");
        var set = await fixture.Scope.Collections.CreateSetAsync(collection.Id, "New set");
        type.GetField("tags", flags)!.SetValue(workbench, "new tag");
        var moved = await fixture.Scope.Collections.MoveMediaToSetAsync([image.Id], set.Id);
        await (Task)type.GetMethod("DestinationSavedAsync", flags)!.Invoke(workbench, [moved])!;
        var collected = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        Assert.Contains("new collection", collected.Collections);
        Assert.Equal("existing", collected.Tags);
        await (Task)type.GetMethod("ApplyTagsAsync", flags)!.Invoke(workbench, null)!;
        Assert.Equal("", type.GetField("error", flags)!.GetValue(workbench));
        var tagged = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        Assert.Contains("existing", tagged.Tags);
        Assert.Contains("new tag", tagged.Tags);
        Assert.Equal(collected.Collections.ToArray(), tagged.Collections.ToArray());
        Assert.Equal(set.Id, tagged.SetId);
        Assert.Equal(image.Description, tagged.Description);
        Assert.Equal(image.Rating, tagged.Rating);
        Assert.Equal(image.IsReviewed, tagged.IsReviewed);
        Assert.Equal(image.IsFavorite, tagged.IsFavorite);
    }

    [Fact]
    public async Task Organize_ApprovingSuggestionWithoutCollection_SavesDescriptionAndTagsWithoutAddingCollection()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var imageRecord = await fixture.Scope.Ingestion.ImportAsync(
            new MemoryStream(GalleryTests.MakePng()), "approval.png", "folder");
        imageRecord = await fixture.Scope.Records.UpdateAsync(imageRecord.Id, existingImageRecord => existingImageRecord with
        {
            Tags = "existing",
            Collections = ["Originals"]
        });
        var checkpoint = await fixture.Scope.Vision.OpenVisionCheckpointAsync();
        await checkpoint.StageAsync(new(imageRecord.Id, imageRecord.Name, "A green hillside.", "landscape", "vision"));
        using var analysis = new LibraryAnalysis(fixture.Scope.Catalog, fixture.Scope.Vision, NullLogger.Instance);
        await analysis.InitializeAsync();

        // Act
        var approvedImageRecord = await analysis.ApproveAsync(Assert.Single(analysis.Suggestions));
        var storedImageRecord = Assert.Single(await fixture.Scope.Catalog.ListAsync());

        // Assert
        Assert.Equal("A green hillside.", Assert.IsType<ImageRecord>(approvedImageRecord).Description);
        Assert.Equal("A green hillside.", storedImageRecord.Description);
        Assert.Equal("existing, landscape", storedImageRecord.Tags);
        Assert.Equal("Originals", Assert.Single(storedImageRecord.Collections));
        Assert.Equal("vision", storedImageRecord.AnalysisModel);
    }

    [Fact]
    public async Task Organize_TagChipsListSelectedTagsAndRemoveOnlyThatTagFromSelectedImages()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(9, 9)), "second.png", "folder");
        var other = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(11, 11)), "other.png", "folder");
        first = await fixture.Scope.Records.UpdateAsync(first.Id, edit => edit with { Tags = "warm, Portrait", Rating = 4 });
        second = await fixture.Scope.Records.UpdateAsync(second.Id, edit => edit with { Tags = "portrait" });
        other = await fixture.Scope.Records.UpdateAsync(other.Id, edit => edit with { Tags = "warm" });
        var services = new ServiceCollection();
        services.AddShadCnBlazorComponents();
        using var provider = services.BuildServiceProvider();
        using var workbench = CreateWorkbench(fixture.Scope, provider.GetRequiredService<ISonnerService>());
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = workbench.GetType();
        type.GetProperty(nameof(workbench.Images))!.SetValue(workbench, new List<ImageRecord> { first, second, other });
        type.GetProperty(nameof(workbench.SelectedIds))!.SetValue(workbench, new[] { first.Id, second.Id });

        var chips = ((IReadOnlyList<(string Tag, int Count)>)type.GetProperty("SelectedTags", flags)!.GetValue(workbench)!).ToArray();
        Assert.Equal([("Portrait", 2), ("warm", 1)], chips);

        await (Task)type.GetMethod("RemoveTagAsync", flags)!.Invoke(workbench, ["warm"])!;
        Assert.Equal("", type.GetField("error", flags)!.GetValue(workbench));
        var stored = (await fixture.Scope.Catalog.ListAsync()).ToDictionary(image => image.Id);
        Assert.Equal("Portrait", stored[first.Id].Tags);
        Assert.Equal(4, stored[first.Id].Rating);
        Assert.Equal("portrait", stored[second.Id].Tags);
        Assert.Equal("warm", stored[other.Id].Tags);
    }

    private static LibraryWorkbench CreateWorkbench(GalleryScope gallery, ISonnerService toasts)
    {
        var workbench = new LibraryWorkbench();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(LibraryWorkbench);
        GalleryCapabilityTestSetup.InjectGalleryCapabilities(workbench, gallery);
        type.GetProperty("OrganizationHistory", flags)!.SetValue(workbench, new OrganizationHistory(gallery.Collections));
        type.GetProperty("Toasts", flags)!.SetValue(workbench, toasts);
        type.GetProperty("Logger", flags)!.SetValue(workbench, NullLogger<LibraryWorkbench>.Instance);
        return workbench;
    }

    [Theory]
    [InlineData(false, 2, 1)]
    [InlineData(true, 2, 0)]
    [InlineData(false, 1, 0)]
    public async Task Viewer_CelebratesOnceOnlyForAnEntirelyReviewedSession(bool hasSkipped, int reviewed, int expectedBursts)
    {
        var viewer = new Gallery.Web.Components.Features.Media.FullscreenViewer();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = viewer.GetType();
        ImageRecord[] images = [new() { Id = "first" }, new() { Id = "last" }];
        type.GetProperty(nameof(viewer.Images))!.SetValue(viewer, images);
        type.GetProperty(nameof(viewer.ReviewMode))!.SetValue(viewer, true);
        var progress = (ViewerReviewProgress)type.GetField("review", flags)!.GetValue(viewer)!;
        if (hasSkipped) progress.RecordSkip(images[0].Id);
        for (var index = 0; index < reviewed; index++)
            progress.RecordDecision(images[index].Id, index, new ReviewState(false, false, false, 0));
        progress.Advance(images, images.Length - 1);
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
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var last = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "last.png", "folder");
        var viewer = new Gallery.Web.Components.Features.Media.FullscreenViewer();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = viewer.GetType();
        type.GetProperty(nameof(viewer.Images))!.SetValue(viewer, new[] { first, last });
        GalleryCapabilityTestSetup.InjectGalleryCapabilities(viewer, fixture.Scope);
        type.GetProperty("JS", flags)!.SetValue(viewer, new ComponentRenderFixture.NoOpJsRuntime());
        type.GetField("index", flags)!.SetValue(viewer, 1);
        Task Skip() => (Task)type.GetMethod("SkipAsync", flags)!.Invoke(viewer, null)!;
        var progress = (ViewerReviewProgress)type.GetField("review", flags)!.GetValue(viewer)!;

        await using var viewerLifetime = viewer;
        await Skip();

        Assert.False(progress.IsComplete);
        Assert.Equal(0, type.GetField("index", flags)!.GetValue(viewer));
        Assert.Equal("0 Reviewed + 1 Skipped / 2", type.GetProperty("ReviewProgressText", flags)!.GetValue(viewer));

        await Skip();

        Assert.True(progress.IsComplete);
        Assert.Equal("0 Reviewed + 2 Skipped / 2", type.GetProperty("ReviewProgressText", flags)!.GetValue(viewer));
    }

    [Fact]
    public void ReviewProgress_WhenUndoingADecisionOnASkippedImage_RestoresTheSkipAndReopens()
    {
        ImageRecord[] images = [new() { Id = "first" }, new() { Id = "last" }];
        var progress = new ViewerReviewProgress();
        var previous = new ReviewState(false, false, false, 0);
        progress.RecordSkip(images[0].Id);
        progress.RecordDecision(images[1].Id, 1, previous);
        Assert.Null(progress.Advance(images, 1));

        progress.Reopen();
        progress.RecordDecision(images[0].Id, 0, previous);
        Assert.Null(progress.Advance(images, 0));
        Assert.Equal(2, progress.ReviewedCount);
        Assert.Equal(0, progress.SkippedCount);

        progress.RecordUndo(images[0].Id);

        Assert.False(progress.IsComplete);
        Assert.Equal(1, progress.ReviewedCount);
        Assert.Equal(1, progress.SkippedCount);
        Assert.Equal(1, progress.PendingUndo?.Index);
    }

    [Fact]
    public async Task Viewer_UsesSavedToggleResultsAndCapturesUndoFromTheLatestPersistedRecord()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var stale = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "stale-review.png", "folder");
        await fixture.Scope.Records.UpdateAsync(stale.Id, edit => edit with { IsFavorite = true, Rating = 3 });
        await fixture.Scope.Records.UpdateAsync(stale.Id, edit => edit with { IsReviewed = false, Description = "latest" });
        var viewer = new Gallery.Web.Components.Features.Media.FullscreenViewer();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = viewer.GetType();
        type.GetProperty(nameof(viewer.Images))!.SetValue(viewer, new List<ImageRecord> { stale });
        GalleryCapabilityTestSetup.InjectGalleryCapabilities(viewer, fixture.Scope);
        type.GetProperty("JS", flags)!.SetValue(viewer, new ComponentRenderFixture.NoOpJsRuntime());
        type.GetProperty("Logger", flags)!.SetValue(viewer,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gallery.Web.Components.Features.Media.FullscreenViewer>.Instance);
        Task Invoke(string method, params object[] args) => (Task)type.GetMethod(method, flags)!.Invoke(viewer, args)!;
        ImageRecord Current() => (ImageRecord)type.GetProperty("Current", flags)!.GetValue(viewer)!;

        await using var viewerLifetime = viewer;
        await Invoke("FavoriteAsync");

        Assert.False(Current().IsFavorite);
        Assert.Equal("latest", Current().Description);
        await fixture.Scope.Records.UpdateAsync(stale.Id, edit => edit with { Rating = 2, IsFavorite = true });
        var beforeReview = fixture.Scope.Records.CaptureReviewState(await fixture.Scope.Records.UpdateAsync(stale.Id,
            edit => edit with { IsReviewed = false, Description = "latest review note" }));
        Assert.Equal(3, Current().Rating);
        Assert.Equal(2, beforeReview.Rating);
        Assert.False(beforeReview.IsReviewed);
        await Invoke("DecideAsync", ReviewAction.Rate, 4);
        Assert.Equal(4, Current().Rating);
        Assert.True(Current().IsReviewed);
        await Invoke("UndoAsync");

        Assert.Equal("", type.GetField("error", flags)!.GetValue(viewer));
        Assert.Equal(beforeReview, fixture.Scope.Records.CaptureReviewState(Current()));
        Assert.Equal(beforeReview, fixture.Scope.Records.CaptureReviewState(Assert.Single(await fixture.Scope.Catalog.ListAsync())));
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
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "undo-rating.png", "folder");
        await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with
        {
            Rating = rating, IsMarkedForDeletion = markedForDeletion, IsFavorite = favorite
        });
        var unreviewed = await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { IsReviewed = false });
        var snapshot = fixture.Scope.Records.CaptureReviewState(unreviewed);
        await fixture.Scope.Records.UpdateAsync(image.Id, edit => fixture.Scope.Records.ApplyReviewDecision(edit, ReviewAction.Rate, 4));
        await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Description = "new note", Tags = "new tag" });

        var restored = await fixture.Scope.Records.RestoreReviewStateAsync(image.Id, snapshot);
        var stored = Assert.Single(await fixture.Scope.Catalog.ListAsync());

        Assert.Equal(snapshot, fixture.Scope.Records.CaptureReviewState(restored));
        Assert.Equal(snapshot, fixture.Scope.Records.CaptureReviewState(stored));
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
            fixture.Scope.Records.RestoreReviewStateAsync("image", new(false, false, false, rating)));
        AsserterExceptionContract.AssertMessageContains(
            [rating.ToString(System.Globalization.CultureInfo.InvariantCulture), "rating", "0", "5"], invalidRating.Message);

        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task EditProjection_UsesTheSameDomainRulesAsPersistedUpdates()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "projection.png", "folder");
        var image = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        ImageRecordEdit Edit(ImageRecordEdit edit) => edit with { Rating = 4, IsFavorite = true, Collections = ["collection"] };

        var projected = fixture.Scope.Records.ApplyEdit(image, Edit);
        await fixture.Scope.Records.UpdateAsync(image.Id, Edit);
        var stored = Assert.Single(await fixture.Scope.Catalog.ListAsync());

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
        var image = new ImageRecord();

        var expectedPhrases = new[] { $"'{collection}'", "collection", "1", "80" };
        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Scope.Records.ApplyEdit(image, edit => edit with { Collections = [collection] }));
        AsserterExceptionContract.AssertMessageContains(expectedPhrases, error.Message);
        Assert.Empty(image.Collections);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task Curation_WhenRatingOrDeletionMarkIsApplied_PersistsAsReviewed()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var rated = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 20)), "rated.png", "folder");
        var marked = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 10)), "marked.png", "folder");

        await fixture.Scope.Records.UpdateAsync(rated.Id, image => image with { Rating = 3 });
        await fixture.Scope.Records.UpdateAsync(marked.Id, image => image with { IsMarkedForDeletion = true });

        var stored = (await fixture.Scope.Catalog.ListAsync()).ToDictionary(image => image.Id);
        Assert.True(stored[rated.Id].IsReviewed);
        Assert.True(stored[marked.Id].IsReviewed);
    }

    [Fact]
    public async Task Review_WhenUndoIsApplied_RestoresCurationWithoutChangingNotesOrFiles()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "undo.png", "folder");
        await fixture.Scope.Records.UpdateAsync(record.Id, image => image with { Rating = 3, IsFavorite = true, Tags = "original" });
        var before = fixture.Scope.Records.CaptureReviewState(Assert.Single(await fixture.Scope.Catalog.ListAsync()));
        // Act
        await fixture.Scope.Records.UpdateAsync(record.Id, image => fixture.Scope.Records.ApplyReviewDecision(image, ReviewAction.MarkDeletion));
        await fixture.Scope.Records.UpdateAsync(record.Id, image => image with { Description = "New note" });
        await fixture.Scope.Records.RestoreReviewStateAsync(record.Id, before);
        // Assert
        var restored = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        Assert.Equal(before, fixture.Scope.Records.CaptureReviewState(restored));
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
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "review.png", "folder");
        // Act
        await fixture.Scope.Records.UpdateAsync(record.Id, image => fixture.Scope.Records.ApplyReviewDecision(image, action, 4));

        // Assert
        var stored = Assert.Single(await fixture.Scope.Catalog.ListAsync());
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
        record = fixture.Scope.Records.ApplyReviewDecision(record, ReviewAction.Keep);

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
        var ratingError = Record.Exception(() => fixture.Scope.Records.ApplyReviewDecision(record, ReviewAction.Rate, 0));
        var reviewedAfterInvalidRating = record.IsReviewed;
        var actionError = Record.Exception(() => fixture.Scope.Records.ApplyReviewDecision(record, (ReviewAction)99));

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
