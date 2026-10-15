using System.Collections;
using System.Reflection;
using System.Text.Json;
using Gallery.Web.Components;
using Gallery.Web.Components.Pages;
using Gallery.Web.Models;
using Microsoft.JSInterop;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class BrowsingProjectionTests
{
    [Fact]
    public void RepeatedReads_EnumerateAndBuildTheBrowsingProjectionOnlyOnce()
    {
        using var fixture = new TestVault();
        var records = new CountingRecords([
            new() { Id = "first", Generation = new() { Prompt = "shared" } },
            new() { Id = "second", Generation = new() { Prompt = "shared" } },
            new() { Id = "third" }
        ]);
        var filter = new WebGalleryFilter();
        var projection = new GalleryBrowsingProjection();
        projection.Update(fixture.Service, records, filter, true);
        var images = projection.Images;
        var groups = projection.PromptGroupsByImageId;

        for (var render = 0; render < 10; render++)
        {
            projection.Update(fixture.Service, records, filter with { }, true);
            Assert.Same(images, projection.Images);
            Assert.Same(groups, projection.PromptGroupsByImageId);
            Assert.Equal(3, projection.Images.Count);
            Assert.Equal(2, projection.Images.Skip(1).Take(2).Count());
            Assert.Same(groups["first"], groups["second"]);
        }

        Assert.Equal(1, records.Enumerations);
    }

    [Fact]
    public void FilterSortGroupingAndRecordChanges_RebuildTheCachedResults()
    {
        using var fixture = new TestVault();
        var records = new CountingRecords([
            new() { Id = "low", Rating = 1, Generation = new() { Prompt = "shared" } },
            new() { Id = "high", Rating = 5, Generation = new() { Prompt = "shared" } }
        ]);
        var projection = new GalleryBrowsingProjection();
        var filter = new WebGalleryFilter { Sort = "rating" };
        projection.Update(fixture.Service, records, filter, false);
        Assert.Equal(["high", "low"], projection.Images.Select(image => image.Id));

        projection.Update(fixture.Service, records, filter with { MinRating = 3 }, false);
        Assert.Equal("high", Assert.Single(projection.Images).Id);

        projection.Update(fixture.Service, records, filter, true);
        Assert.Equal(2, projection.PromptGroupsByImageId["high"].Images.Length);
        Assert.Equal(3, records.Enumerations);

        records[0] = records[0] with { Rating = 4, Generation = new() { Prompt = "changed" } };
        projection.Invalidate();
        projection.Update(fixture.Service, records, filter, true);
        Assert.Equal(4, projection.Images.Single(image => image.Id == "low").Rating);
        Assert.Equal(WebPromptGroup.Unique, projection.PromptGroupsByImageId["low"].Kind);
        Assert.Equal(4, records.Enumerations);

        projection.Update(fixture.Service, records, filter, false);
        Assert.Empty(projection.PromptGroupsByImageId);
        Assert.Equal(5, records.Enumerations);

        var replacement = new CountingRecords([new() { Id = "new" }]);
        projection.Update(fixture.Service, replacement, filter, false);
        Assert.Equal("new", Assert.Single(projection.Images).Id);
        Assert.Equal(1, replacement.Enumerations);
    }

    [Fact]
    public void Home_ReusesTheProjectionAcrossPaginationAndInvalidatesOnEditsAndLock()
    {
        using var fixture = new TestVault();
        var records = Enumerable.Range(0, 60).Select(index => new WebImageRecord
        {
            Id = index.ToString(), Rating = index == 0 ? 5 : 0,
            Generation = new() { Prompt = "shared" }
        }).ToList();
        var home = CreateHome(fixture, records);
        var filtered = GetProperty<IReadOnlyList<WebImageRecord>>(home, "Filtered");
        SetField(home, "page", 1);
        SetField(home, "density", "compact");

        Assert.Same(filtered, GetProperty<IReadOnlyList<WebImageRecord>>(home, "Filtered"));
        Assert.Equal(2, GetProperty<int>(home, "PageCount"));
        Assert.Equal(11, GetProperty<IEnumerable<WebImageRecord>>(home, "PageImages").Count());

        SetField(home, "filter", new WebGalleryFilter { MinRating = 5 });
        Assert.Equal("0", Assert.Single(GetProperty<IReadOnlyList<WebImageRecord>>(home, "Filtered")).Id);
        Invoke(home, "ViewerImageChanged", records[1] with { Rating = 5 });
        Assert.Equal(2, GetProperty<IReadOnlyList<WebImageRecord>>(home, "Filtered").Count);

        SetField(home, "grouping", "prompt");
        var groups = GetProperty<IReadOnlyDictionary<string, WebPromptGroup>>(home, "promptGroupOf");
        Assert.Equal(2, groups["0"].Images.Length);
        Assert.Same(groups["0"], groups["1"]);

        Invoke(home, "Lock");

        Assert.Empty(GetProperty<IReadOnlyList<WebImageRecord>>(home, "Filtered"));
        Assert.Empty(GetProperty<IReadOnlyDictionary<string, WebPromptGroup>>(home, "promptGroupOf"));
    }

    [Fact]
    public async Task Home_UsesCommittedResultsWithoutReplayingTheCallbackOnAStaleSnapshot()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var stale = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "stale.png", "folder");
        await fixture.Service.UpdateAsync(stale.Id, edit => edit with { Description = "latest note" });
        var home = CreateHome(fixture, [stale]);
        SetField(home, "filter", new WebGalleryFilter { MinRating = 5 });
        var calls = 0;
        WebImageRecordEdit Edit(WebImageRecordEdit edit) =>
            edit with { Rating = ++calls, Favorite = !edit.Favorite };

        await (Task)Invoke(home, "CurateAsync", new[] { stale.Id, stale.Id },
            (Func<WebImageRecordEdit, WebImageRecordEdit>)Edit, "saved")!;

        var stored = Assert.Single(await fixture.Service.ListAsync());
        var displayed = Assert.Single(GetField<List<WebImageRecord>>(home, "images"));
        Assert.Equal(1, calls);
        Assert.Equal("latest note", displayed.Description);
        Assert.True(displayed.Favorite);
        Assert.Equal(JsonSerializer.Serialize(stored), JsonSerializer.Serialize(displayed));
    }

    private static Home CreateHome(TestVault fixture, List<WebImageRecord> records)
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

    private sealed class CountingRecords(List<WebImageRecord> records) : IReadOnlyList<WebImageRecord>
    {
        public int Enumerations { get; private set; }
        public int Count => records.Count;
        public WebImageRecord this[int index] { get => records[index]; set => records[index] = value; }

        public IEnumerator<WebImageRecord> GetEnumerator()
        {
            Enumerations++;
            return records.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
