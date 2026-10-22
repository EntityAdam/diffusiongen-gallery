using System.Collections;
using System.Reflection;
using System.Text.Json;
using Gallery.Web.Components;
using Gallery.Web.Components.Pages;
using Gallery.Core.Models;
using Microsoft.JSInterop;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class BrowsingProjectionTests
{
    [Fact]
    public void Home_IngestionDefaultsAndProfiles_PreserveExpectedSettings()
    {
        var home = new Home();
        Assert.True(GetField<bool>(home, "recursive"));
        Assert.False(GetField<bool>(home, "deleteSources"));
        Assert.False(GetField<bool>(home, "recordTimings"));
        Assert.Equal("hdd", GetField<string>(home, "ingestionProfile"));
        Assert.Equal(IngestionOptions.Hdd.Workers, GetField<int>(home, "ingestionWorkers"));
        Assert.Equal(IngestionOptions.Hdd.CatalogBatchSize, GetField<int>(home, "ingestionBatchSize"));

        Invoke(home, "ApplyIngestionProfile", IngestionOptions.Ssd, "ssd");
        Assert.Equal("ssd", GetField<string>(home, "ingestionProfile"));
        Assert.Equal(IngestionOptions.Ssd.Workers, GetField<int>(home, "ingestionWorkers"));

        Invoke(home, "ApplyIngestionProfile", new IngestionOptions { Workers = 7, CatalogBatchSize = 21 }, null);
        Assert.Equal("custom", GetField<string>(home, "ingestionProfile"));
        Assert.Equal(7, GetField<int>(home, "ingestionWorkers"));
        Assert.Equal(21, GetField<int>(home, "ingestionBatchSize"));

        Invoke(home, "ApplyIngestionProfile", IngestionOptions.Hdd, null);
        Assert.Equal("hdd", GetField<string>(home, "ingestionProfile"));
    }

    [Fact]
    public void Home_IngestionScanGatesIngest_AndClearsOnSourceScopeChanges()
    {
        var home = new Home();
        var scan = new IngestionScan("source", FolderSearchScope.Descendants, "catalog", "media",
            [new IngestionScanFile("source.png", 100, DateTime.UtcNow, false)]);
        var space = Gallery.Core.Services.GalleryService.CalculateIngestionSpace(1, 100,
            long.MaxValue, long.MaxValue, true);
        Assert.False(GetProperty<bool>(home, "CanIngestScan"));
        SetField(home, "ingestionScan", scan);
        SetField(home, "ingestionSpace", space);
        Assert.True(GetProperty<bool>(home, "CanIngestScan"));

        SetField(home, "busy", true);
        Assert.False(GetProperty<bool>(home, "CanIngestScan"));
        SetField(home, "busy", false);
        Assert.Equal(space.EstimatedBytes, GetProperty<long>(home, "EstimatedIngestionChangeBytes"));
        Invoke(home, "ToggleSourceDeletion");
        Assert.True(GetProperty<bool>(home, "CanIngestScan"));
        Assert.Equal(space.EstimatedBytes - space.SelectedSourceBytes, GetProperty<long>(home, "EstimatedIngestionChangeBytes"));
        Invoke(home, "ToggleSourceDeletion");
        Assert.Equal(space.EstimatedBytes, GetProperty<long>(home, "EstimatedIngestionChangeBytes"));

        SetField(home, "ingestionSpace", space with { MediaAvailableBytes = 0 });
        Assert.False(GetProperty<bool>(home, "CanIngestScan"));
        Invoke(home, "ToggleSourceDeletion");
        Assert.False(GetProperty<bool>(home, "CanIngestScan"));
        SetField(home, "ingestionSpace", space with { FileCount = 0 });
        Assert.False(GetProperty<bool>(home, "CanIngestScan"));
        Invoke(home, "ToggleImportRecursion");
        Assert.False(GetField<bool>(home, "recursive"));
        Assert.Null(GetField<IngestionScan?>(home, "ingestionScan"));
        Assert.Null(GetField<IngestionSpaceEstimate?>(home, "ingestionSpace"));
        Assert.False(GetProperty<bool>(home, "CanIngestScan"));
    }

    [Fact]
    public void Projection_WhenCalledWithIndependentInputs_ThenProducesIndependentImmutableSnapshots()
    {
        // Arrange
        using var fixture = new TestVault();
        var firstRecords = new CountingRecords([
            new() { Id = "first", Generation = new() { Prompt = "shared" } },
            new() { Id = "second", Generation = new() { Prompt = "shared" } }
        ]);
        var secondRecords = new CountingRecords([new() { Id = "third", Rating = 5 }]);

        // Act
        var firstProjection = GalleryBrowsingProjector.Create(fixture.Service, firstRecords, new GalleryFilter(), true);
        var secondProjection = GalleryBrowsingProjector.Create(fixture.Service, secondRecords, new GalleryFilter { MinRating = 3 }, false);
        firstRecords[0] = firstRecords[0] with { Id = "replacement" };

        // Assert
        Assert.Equal(["first", "second"], firstProjection.Images.Select(image => image.Id).Order(StringComparer.Ordinal));
        Assert.True(secondProjection.Images.Count == 1, $"Expected 1 image in the independent projection; Actual: {secondProjection.Images.Count} images.");
        Assert.Equal("third", secondProjection.Images[0].Id);
        Assert.Same(firstProjection.PromptGroupsByImageId["first"], firstProjection.PromptGroupsByImageId["second"]);
        Assert.True(secondProjection.PromptGroupsByImageId.Count == 0, $"Expected 0 groups for ungrouped projection; Actual: {secondProjection.PromptGroupsByImageId.Count} groups.");
        Assert.Equal(1, firstRecords.Enumerations);
        Assert.Equal(1, secondRecords.Enumerations);
    }

    [Fact]
    public void Projection_WhenFilterOrGroupingChanges_ThenProducesTheRequestedSnapshot()
    {
        // Arrange
        using var fixture = new TestVault();
        ImageRecord[] records = [
            new() { Id = "low", Rating = 1, Generation = new() { Prompt = "shared" } },
            new() { Id = "high", Rating = 5, Generation = new() { Prompt = "shared" } }
        ];
        var filter = new GalleryFilter { Sort = "rating" };

        // Act
        var sortedProjection = GalleryBrowsingProjector.Create(fixture.Service, records, filter, false);
        var filteredProjection = GalleryBrowsingProjector.Create(fixture.Service, records, filter with { MinRating = 3 }, false);
        var groupedProjection = GalleryBrowsingProjector.Create(fixture.Service, records, filter, true);
        records[0] = records[0] with { Generation = new() { Prompt = "shared", Model = "Flux" } };
        records[1] = records[1] with { Generation = new() { Prompt = "shared", Model = "SDXL" } };
        var modelGroupedProjection = GalleryBrowsingProjector.Create(fixture.Service, records, filter, "model");

        // Assert
        Assert.Equal(["high", "low"], sortedProjection.Images.Select(image => image.Id));
        Assert.True(filteredProjection.Images.Count == 1, $"Expected 1 filtered image; Actual: {filteredProjection.Images.Count} images.");
        Assert.Equal("high", filteredProjection.Images[0].Id);
        Assert.Equal(2, groupedProjection.PromptGroupsByImageId["high"].Images.Length);
        Assert.True(sortedProjection.PromptGroupsByImageId.Count == 0, $"Expected 0 groups for ungrouped projection; Actual: {sortedProjection.PromptGroupsByImageId.Count} groups.");
        Assert.Equal(["high", "low"], modelGroupedProjection.Images.Select(image => image.Id));
        Assert.Equal("SDXL", modelGroupedProjection.DiffusionModelGroupsByImageId["high"].Model);
        Assert.Equal("Flux", modelGroupedProjection.DiffusionModelGroupsByImageId["low"].Model);
    }

    [Fact]
    public void Home_ReusesTheProjectionAcrossPaginationAndInvalidatesOnEditsAndLock()
    {
        using var fixture = new TestVault();
        var records = Enumerable.Range(0, 60).Select(index => new ImageRecord
        {
            Id = index.ToString(), Rating = index == 0 ? 5 : 0,
            Generation = new() { Prompt = "shared" }
        }).ToList();
        var home = CreateHome(fixture, records);
        var filtered = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered");
        SetField(home, "page", 1);
        SetField(home, "density", "compact");

        Assert.Same(filtered, GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered"));
        Assert.Equal(2, GetProperty<int>(home, "PageCount"));
        Assert.Equal(11, GetProperty<IEnumerable<ImageRecord>>(home, "PageImages").Count());

        SetField(home, "filter", new GalleryFilter { MinRating = 5 });
        var actualFilteredImages = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered");
        Assert.True(actualFilteredImages.Count == 1, $"Expected 1 image matching rating filter; Actual: {actualFilteredImages.Count} images.");
        Assert.Equal("0", actualFilteredImages[0].Id);
        Invoke(home, "ViewerImageChanged", records[1] with { Rating = 5 });
        Assert.Equal(2, GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered").Count);

        SetField(home, "grouping", "prompt");
        var groups = GetProperty<IReadOnlyDictionary<string, PromptGroup>>(home, "promptGroupOf");
        Assert.Equal(2, groups["0"].Images.Length);
        Assert.Same(groups["0"], groups["1"]);

        Invoke(home, "Lock");

        var actualLockedImages = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered");
        Assert.True(actualLockedImages.Count == 0, $"Expected 0 visible images after locking; Actual: {actualLockedImages.Count} images.");
        var actualLockedGroups = GetProperty<IReadOnlyDictionary<string, PromptGroup>>(home, "promptGroupOf");
        Assert.True(actualLockedGroups.Count == 0, $"Expected 0 prompt groups after locking; Actual: {actualLockedGroups.Count} groups.");
    }

    [Fact]
    public async Task Home_UsesCommittedResultsWithoutReplayingTheCallbackOnAStaleSnapshot()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var stale = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "stale.png", "folder");
        await fixture.Service.UpdateAsync(stale.Id, edit => edit with { Description = "latest note" });
        var home = CreateHome(fixture, [stale]);
        SetField(home, "filter", new GalleryFilter { MinRating = 5 });
        var calls = 0;
        ImageRecordEdit Edit(ImageRecordEdit edit) =>
            edit with { Rating = ++calls, IsFavorite = !edit.IsFavorite };

        await (Task)Invoke(home, "CurateAsync", new[] { stale.Id, stale.Id },
            (Func<ImageRecordEdit, ImageRecordEdit>)Edit, "saved")!;

        var actualStoredImages = await fixture.Service.ListAsync();
        var actualDisplayedImages = GetField<List<ImageRecord>>(home, "images");
        Assert.True(actualStoredImages.Count == 1, $"Expected 1 stored committed image; Actual: {actualStoredImages.Count} images.");
        Assert.True(actualDisplayedImages.Count == 1, $"Expected 1 displayed committed image; Actual: {actualDisplayedImages.Count} images.");
        var stored = actualStoredImages[0];
        var displayed = actualDisplayedImages[0];
        Assert.Equal(1, calls);
        Assert.Equal("latest note", displayed.Description);
        Assert.True(displayed.IsFavorite);
        Assert.Equal(JsonSerializer.Serialize(stored), JsonSerializer.Serialize(displayed));
    }

    [Fact]
    public void GuidedReview_DefaultGoal_SelectsTenEligibleImagesInSortOrderAcrossPages()
    {
        using var fixture = new TestVault();
        var records = Enumerable.Range(0, 30).Select(index => new ImageRecord
        {
            Id = index.ToString("D2"), Name = index.ToString("D2"),
            IsReviewed = index == 0, IsMarkedForDeletion = index == 1
        }).ToList();
        var home = CreateHome(fixture, records);
        SetField(home, "filter", new GalleryFilter { Sort = "name" });
        SetField(home, "density", "detail");
        SetField(home, "page", 1);

        Assert.Equal(10, GetProperty<int>(home, "ReviewGoal"));
        Invoke(home, "StartReview");

        var session = GetField<List<ImageRecord>>(home, "viewerImages");
        Assert.Equal(records.Skip(2).Take(10).Select(image => image.Id), session.Select(image => image.Id));
        Assert.Equal(session[0].Id, GetField<string>(home, "viewerInitialId"));
        Assert.True(GetField<bool>(home, "reviewMode"));
        Assert.NotSame(records[2], session[0]);
    }

    [Theory]
    [InlineData(100, 6)]
    [InlineData(3, 3)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void GuidedReview_GoalIsClampedToEligibleFilteredPool(int requested, int expected)
    {
        using var fixture = new TestVault();
        var records = Enumerable.Range(0, 20).Select(index => new ImageRecord
        {
            Id = index.ToString(), IsFavorite = index < 8,
            IsReviewed = index == 0, IsMarkedForDeletion = index == 1
        }).ToList();
        var home = CreateHome(fixture, records);
        SetField(home, "filter", new GalleryFilter { IncludesFavoritesOnly = true });
        typeof(Home).GetProperty("ReviewGoal", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, requested);

        Assert.Equal(expected, GetProperty<int>(home, "ReviewGoal"));
        Invoke(home, "StartReview");
        var session = GetField<List<ImageRecord>>(home, "viewerImages");
        Assert.Equal(expected, session.Count);
        Assert.All(session, image => Assert.True(image.IsFavorite && !image.IsReviewed && !image.IsMarkedForDeletion));
    }

    [Fact]
    public void GuidedReview_RandomOrder_SelectsGoalWithoutDuplicatesOrChangingTheLibrary()
    {
        using var fixture = new TestVault();
        var records = Enumerable.Range(0, 80).Select(index => new ImageRecord
        {
            Id = index.ToString(), IsFavorite = index % 2 == 0,
            IsReviewed = index < 10, IsMarkedForDeletion = index > 70
        }).ToList();
        var home = CreateHome(fixture, records);
        SetField(home, "filter", new GalleryFilter { IncludesFavoritesOnly = true });
        SetField(home, "reviewRandomOrder", true);
        SetField(home, "reviewGoal", 7);
        var originalOrder = records.Select(image => image.Id).ToArray();
        var eligible = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered")
            .Where(image => !image.IsReviewed && !image.IsMarkedForDeletion).Select(image => image.Id).ToHashSet();

        Invoke(home, "StartReview");

        var session = GetField<List<ImageRecord>>(home, "viewerImages");
        Assert.Equal(7, session.Count);
        Assert.Equal(7, session.Select(image => image.Id).Distinct().Count());
        Assert.All(session, image => Assert.Contains(image.Id, eligible));
        Assert.Equal(originalOrder, records.Select(image => image.Id));
    }

    [Fact]
    public void GuidedReview_WhenEligiblePoolShrinks_ThenDisplayedGoalAndSessionAreCapped()
    {
        using var fixture = new TestVault();
        var records = Enumerable.Range(0, 20).Select(index => new ImageRecord
        {
            Id = index.ToString(), IsFavorite = index < 3
        }).ToList();
        var home = CreateHome(fixture, records);
        Assert.Equal(10, GetProperty<int>(home, "ReviewGoal"));
        SetField(home, "filter", new GalleryFilter { IncludesFavoritesOnly = true });
        Assert.Equal(3, GetProperty<int>(home, "ReviewGoal"));
        Invoke(home, "StartReview");
        Assert.Equal(3, GetField<List<ImageRecord>>(home, "viewerImages").Count);

        SetField(home, "filter", new GalleryFilter { MinRating = 5 });
        Assert.Equal(0, GetProperty<int>(home, "ReviewGoal"));
        Invoke(home, "StartReview");
        Assert.Empty(GetField<List<ImageRecord>>(home, "viewerImages"));
    }

    [Fact]
    public void Trash_UsesStandardSelectionAndRangeWithoutChangingLibrarySelection()
    {
        using var fixture = new TestVault();
        var records = Enumerable.Range(0, 4).Select(index => new ImageRecord
        {
            Id = index.ToString(), Name = index.ToString(), IsMarkedForDeletion = true
        }).ToList();
        var home = CreateHome(fixture, records);
        SetField(home, "activeTool", "trash");
        SetField(home, "filter", new GalleryFilter { Deletion = "marked", Sort = "name" });
        Invoke(home, "SelectCard", "0", false);
        Invoke(home, "SelectCard", "2", true);

        Assert.Equal(["0", "1", "2"], GetField<HashSet<string>>(home, "removalSelection").Order());
        Assert.Empty(GetField<HashSet<string>>(home, "librarySelection"));
        Invoke(home, "SelectCard", "1", false);
        Assert.DoesNotContain("1", GetField<HashSet<string>>(home, "removalSelection"));
        Invoke(home, "ClearSelection");
        Assert.Empty(GetField<HashSet<string>>(home, "removalSelection"));
    }

    [Fact]
    public async Task TrashTool_SwapsToMarkedImagesAndRestoresTheBrowsingFilter()
    {
        using var fixture = new TestVault();
        var records = new List<ImageRecord>
        {
            new() { Id = "kept", Name = "kept" },
            new() { Id = "marked", Name = "marked", IsMarkedForDeletion = true }
        };
        var home = CreateHome(fixture, records);
        var browsing = new GalleryFilter { Search = "kept" };
        SetField(home, "filter", browsing);

        await InvokeAsync(home, "SetActiveToolAsync", "trash");
        Assert.Equal("marked", GetField<GalleryFilter>(home, "filter").Deletion);
        Assert.Equal(["marked"], GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered").Select(image => image.Id));

        await InvokeAsync(home, "SetActiveToolAsync", "views");
        Assert.Equal(browsing, GetField<GalleryFilter>(home, "filter"));
        Assert.Null(GetField<GalleryFilter?>(home, "browsingFilter"));
    }
    private static Home CreateHome(TestVault fixture, List<ImageRecord> records)
    {
        var home = new Home();
        typeof(Home).GetProperty("Gallery", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, fixture.Service);
        typeof(Home).GetProperty("JS", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, new NoOpJsRuntime());
        SetField(home, "images", records);
        SetField(home, "imagesById", records.ToDictionary(image => image.Id));
        return home;
    }

    private static void SetField(Home home, string name, object value) =>
        typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, value);

    private static T GetField<T>(Home home, string name) =>
        (T)typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(home)!;

    private static T GetProperty<T>(Home home, string name) =>
        (T)typeof(Home).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(home)!;

    private static object? Invoke(Home home, string name, params object?[] args) =>
        typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, args);

    private static Task InvokeAsync(Home home, string name, params object?[] args) => (Task)Invoke(home, name, args)!;

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class CountingRecords(List<ImageRecord> records) : IReadOnlyList<ImageRecord>
    {
        public int Enumerations { get; private set; }
        public int Count => records.Count;
        public ImageRecord this[int index] { get => records[index]; set => records[index] = value; }

        public IEnumerator<ImageRecord> GetEnumerator()
        {
            Enumerations++;
            return records.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
