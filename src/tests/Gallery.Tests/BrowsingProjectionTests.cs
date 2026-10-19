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

        // Assert
        Assert.Equal(["high", "low"], sortedProjection.Images.Select(image => image.Id));
        Assert.True(filteredProjection.Images.Count == 1, $"Expected 1 filtered image; Actual: {filteredProjection.Images.Count} images.");
        Assert.Equal("high", filteredProjection.Images[0].Id);
        Assert.Equal(2, groupedProjection.PromptGroupsByImageId["high"].Images.Length);
        Assert.True(sortedProjection.PromptGroupsByImageId.Count == 0, $"Expected 0 groups for ungrouped projection; Actual: {sortedProjection.PromptGroupsByImageId.Count} groups.");
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

    private static object? Invoke(Home home, string name, params object[] args) =>
        typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, args);

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
