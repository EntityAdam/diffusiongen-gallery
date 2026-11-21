using System.Collections;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Web.Components.Features.Browsing;
using Gallery.Web.Components.Features.Vision;
using static Gallery.Tests.BrowsingWorkspaceFixture;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class BrowsingProjectionTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("prompt")]
    [InlineData("model")]
    public void AwaitingApproval_FiltersDraftsBeforeGroupingAndCombinesOtherCriteria(string grouping)
    {
        using var vault = new TestVault();
        List<ImageRecord> images =
        [
            new() { Id = "new-draft", IsFavorite = true },
            new() { Id = "reanalyzed", Description = "Approved notes", IsFavorite = true },
            new() { Id = "no-draft", IsFavorite = true },
            new() { Id = "not-favorite" },
            new() { Id = "deleted", IsFavorite = true, IsMarkedForDeletion = true }
        ];
        var filter = new GalleryFilter { Analysis = GalleryFilter.AwaitingApproval, IncludesFavoritesOnly = true };
        var draftIds = new HashSet<string> { "new-draft", "reanalyzed", "not-favorite", "deleted", "missing" };

        var projection = GalleryBrowsingProjector.Create(vault.Scope.Catalog, images, filter, grouping, draftIds);

        Assert.Equal(new[] { "new-draft", "reanalyzed" }, projection.Images.Select(image => image.Id).Order());
        Assert.Empty(vault.Scope.Catalog.Filter(images, filter));
        Assert.Empty(GalleryBrowsingProjector.Create(vault.Scope.Catalog, images, filter, grouping, new HashSet<string>()).Images);
    }

    [Fact]
    public async Task AwaitingApproval_RefreshesCachedResultsAndClampsPageWhenDraftsChange()
    {
        using var vault = new TestVault();
        var images = CreateImages(60);
        using var home = await OpenSessionAsync(vault, images);
        await home.Browser.ApplyFilterAsync(new GalleryFilter { Analysis = GalleryFilter.AwaitingApproval });
        Assert.Empty(home.Browser.Filtered);
        var suggestions = images.Select(image => new OrganizationSuggestion(image.Id, image.Name, "Draft", "", "vision")).ToArray();

        await home.Browser.SuggestionsChangedAsync(suggestions);
        Assert.Equal(60, home.Browser.Filtered.Count);
        await home.Browser.GoToPageAsync(4);
        Assert.Equal(4, home.Browser.Page);

        await home.Browser.SuggestionsChangedAsync(suggestions.Take(1).ToArray());
        Assert.Equal(images[0].Id, Assert.Single(home.Browser.Filtered).Id);
        Assert.Equal(0, home.Browser.Page);

        await home.Browser.SuggestionsChangedAsync(Array.Empty<OrganizationSuggestion>());
        Assert.Empty(home.Browser.Filtered);
        Assert.Equal(0, home.Browser.Page);
    }

    [Fact]
    public void Projection_WhenAnotherSnapshotIsCreated_ThenKeepsTheOriginalImmutableAndIndependent()
    {
        using var vault = new TestVault();
        var source = new CountingImages([new() { Id = "original", Generation = new() { Prompt = "cat" } }]);
        var other = new CountingImages([new() { Id = "other" }]);
        var original = GalleryBrowsingProjector.Create(vault.Scope.Catalog, source, new(), "prompt");
        source[0] = source[0] with { Id = "changed" };
        var independent = GalleryBrowsingProjector.Create(vault.Scope.Catalog, other, new(), "none");

        Assert.Equal("original", Assert.Single(original.Images).Id);
        Assert.Equal("original", Assert.Single(original.PromptGroupsByImageId).Key);
        Assert.Equal("other", Assert.Single(independent.Images).Id);
        Assert.Equal(1, source.Enumerations);
        Assert.Equal(1, other.Enumerations);
    }

    [Fact]
    public void ProjectionContract_WhenSourceCollectionsChange_ThenRetainsTheCapturedImagesAndBothIndexes()
    {
        // Arrange
        var sourceImageRecords = CreateRatingImages().ToList();
        var promptGroup = new PromptGroup(PromptGroup.Shared,
            sourceImageRecords[0].Generation!.Prompt, sourceImageRecords.ToImmutableArray());
        var sourcePromptGroupsByImageId = sourceImageRecords.ToDictionary(imageRecord => imageRecord.Id, _ => promptGroup);
        var sourceDiffusionModelGroupsByImageId = sourceImageRecords.ToDictionary(imageRecord => imageRecord.Id,
            imageRecord => new DiffusionModelGroup(imageRecord.Generation!.Model, [imageRecord]));
        var expectedScenario = ExpectedScenario.CreateFrom("Projection snapshots retain complete images and indexes independently of mutable source collections.", new
        {
            Images = sourceImageRecords,
            PromptIndex = sourcePromptGroupsByImageId,
            ModelIndex = sourceDiffusionModelGroupsByImageId
        });

        // Act
        var actualGalleryBrowsingProjection = new GalleryBrowsingProjection(sourceImageRecords.ToImmutableArray(),
            sourcePromptGroupsByImageId.ToImmutableDictionary(), sourceDiffusionModelGroupsByImageId.ToImmutableDictionary());

        // Post-capture stimulus
        sourceImageRecords.Clear();
        sourcePromptGroupsByImageId.Clear();
        sourceDiffusionModelGroupsByImageId.Clear();

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            actualGalleryBrowsingProjection.Images,
            PromptIndex = actualGalleryBrowsingProjection.PromptGroupsByImageId,
            ModelIndex = actualGalleryBrowsingProjection.DiffusionModelGroupsByImageId
        });
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unsupported-grouping")]
    public void Projection_WithoutARecognizedGroup_ThenPreservesRatingOrderWithoutGroupIndexes(string grouping)
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages();
        var expectedScenario = ExpectedScenario.CreateFrom("Ungrouped projection preserves rating order and has no grouping indexes.", new
        {
            ImageIds = new[] { imageRecords[1].Id, imageRecords[0].Id }, PromptGroupCount = 0, ModelGroupCount = 0
        });

        // Act
        var actualGalleryBrowsingProjection = GalleryBrowsingProjector.Create(
            testVault.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating" }, grouping);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ImageIds = actualGalleryBrowsingProjection.Images.Select(imageRecord => imageRecord.Id).ToArray(),
            PromptGroupCount = actualGalleryBrowsingProjection.PromptGroupsByImageId.Count,
            ModelGroupCount = actualGalleryBrowsingProjection.DiffusionModelGroupsByImageId.Count
        });
    }

    [Fact]
    public void Projection_WhenMinimumRatingIsSet_ThenExcludesLowerRatedImages()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages();
        string[] expectedImageIds = [imageRecords[1].Id];

        // Act
        var actualGalleryBrowsingProjection = GalleryBrowsingProjector.Create(
            testVault.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating", MinRating = 3 }, "none");

        // Assert
        Assert.Equal(expectedImageIds, actualGalleryBrowsingProjection.Images.Select(imageRecord => imageRecord.Id));
    }

    [Fact]
    public void Projection_WhenGroupedByPrompt_ThenIndexesBothImagesInTheSameGroup()
    {
        using var vault = new TestVault();
        var images = CreateRatingImages();
        var projection = GalleryBrowsingProjector.Create(vault.Scope.Catalog, images, new(), "prompt");

        var group = projection.PromptGroupsByImageId[images[0].Id];
        Assert.Same(group, projection.PromptGroupsByImageId[images[1].Id]);
        Assert.Equal(images.Select(image => image.Id).Order(), group.Images.Select(image => image.Id).Order());
    }

    [Fact]
    public void Projection_WhenGroupedByModel_ThenIndexesTheModelsInRatingOrder()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages();
        var sharedPrompt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expectedLowerRatedModel = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expectedHigherRatedModel = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        imageRecords[0] = imageRecords[0] with { Generation = new() { Prompt = sharedPrompt, Model = expectedLowerRatedModel } };
        imageRecords[1] = imageRecords[1] with { Generation = new() { Prompt = sharedPrompt, Model = expectedHigherRatedModel } };
        var expectedScenario = ExpectedScenario.CreateFrom("Model grouping retains the requested rating order and the correct model for every image.", new
        {
            ImageIds = new[] { imageRecords[1].Id, imageRecords[0].Id },
            HigherRatedModel = expectedHigherRatedModel, LowerRatedModel = expectedLowerRatedModel
        });

        // Act
        var actualGalleryBrowsingProjection = GalleryBrowsingProjector.Create(
            testVault.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating" }, "model");

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ImageIds = actualGalleryBrowsingProjection.Images.Select(imageRecord => imageRecord.Id).ToArray(),
            HigherRatedModel = actualGalleryBrowsingProjection.DiffusionModelGroupsByImageId[imageRecords[1].Id].Model,
            LowerRatedModel = actualGalleryBrowsingProjection.DiffusionModelGroupsByImageId[imageRecords[0].Id].Model
        });
    }

    // Every grouping mode returns empty images and indexes when the filter excludes all inputs.
    [Theory]
    [InlineData("none")]
    [InlineData("prompt")]
    [InlineData("model")]
    public void Projection_WhenNoImagesMatch_ThenProducesEmptyImagesAndGroupIndexes(string grouping)
    {
        // Arrange
        using var testVault = new TestVault();
        ImageRecord[] imageRecords = [new() { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Rating = 1 }];
        var expectedScenario = ExpectedScenario.CreateFrom("Filtering out every image also empties every grouping index.",
            new { ImageCount = 0, PromptGroupCount = 0, ModelGroupCount = 0 });

        // Act
        var actualGalleryBrowsingProjection = GalleryBrowsingProjector.Create(
            testVault.Scope.Catalog, imageRecords, new GalleryFilter { MinRating = 5 }, grouping);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ImageCount = actualGalleryBrowsingProjection.Images.Count,
            PromptGroupCount = actualGalleryBrowsingProjection.PromptGroupsByImageId.Count,
            ModelGroupCount = actualGalleryBrowsingProjection.DiffusionModelGroupsByImageId.Count
        });
    }

    [Fact]
    public async Task HomeProjection_WhenPaginationChanges_ThenReusesTheSnapshotAndShowsTheCorrectPageCounts()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(60);
        using var home = await OpenSessionAsync(testVault, imageRecords);
        var priorImageRecords = home.Browser.Filtered;
        await home.Browser.SetDensityAsync("compact");
        await home.Browser.GoToPageAsync(1);
        var expectedScenario = ExpectedScenario.CreateFrom("Pagination reuses the filtered snapshot while calculating the current page boundaries.",
            new { ReusesSnapshot = true, PageCount = 2, PageImageCount = 11 });

        // Act
        var actualImageRecords = home.Browser.Filtered;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ReusesSnapshot = ReferenceEquals(priorImageRecords, actualImageRecords),
            PageCount = home.Browser.PageCount,
            PageImageCount = home.Browser.PageImages.Count()
        });
    }

    [Fact]
    public async Task HomeProjection_WhenRatingFilterChanges_ThenReplacesTheSnapshotWithMatchingImages()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages().ToList();
        using var home = await OpenSessionAsync(testVault, imageRecords);
        var priorImageRecords = home.Browser.Filtered;
        await home.Browser.ApplyFilterAsync(new GalleryFilter { MinRating = 5 });
        var expectedScenario = ExpectedScenario.CreateFrom("Changing the rating filter replaces the cached snapshot with matching images.",
            new { ReusesSnapshot = false, ImageIds = new[] { imageRecords[1].Id } });

        // Act
        var actualImageRecords = home.Browser.Filtered;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ReusesSnapshot = ReferenceEquals(priorImageRecords, actualImageRecords),
            ImageIds = actualImageRecords.Select(imageRecord => imageRecord.Id).ToArray()
        });
    }

    [Fact]
    public async Task HomeProjection_WhenAnImageIsEdited_ThenIncludesTheNewlyMatchingImage()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages().ToList();
        using var home = await OpenSessionAsync(testVault, imageRecords);
        await home.Browser.ApplyFilterAsync(new GalleryFilter { MinRating = 5 });
        _ = home.Browser.Filtered;
        var updatedImageRecord = imageRecords[0] with { Rating = 5 };
        var expectedImageIds = imageRecords.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray();

        // Act
        await home.Catalog.UpdateAsync(updatedImageRecord);
        await home.Browser.PauseAsync();

        // Assert - gather the updated projection
        var actualImageRecords = home.Browser.Filtered;

        // Assert
        Assert.Equal(expectedImageIds, actualImageRecords.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task HomeProjection_WhenPromptGroupingIsSelected_ThenRebuildsTheGroupIndex()
    {
        using var vault = new TestVault();
        using var home = await OpenSessionAsync(vault, CreateRatingImages().ToList());
        Assert.Empty(home.Browser.Browsing.PromptGroupsByImageId);

        await home.Browser.SetGroupingAsync("prompt");

        Assert.NotEmpty(home.Browser.Browsing.PromptGroupsByImageId);
    }

    [Fact]
    public async Task WorkspaceProjection_WhenDisposed_ThenClearsImagesAndGroups()
    {
        // Arrange
        using var testVault = new TestVault();
        using var home = await OpenSessionAsync(testVault, CreateRatingImages().ToList());
        await home.Browser.SetGroupingAsync("prompt");
        _ = home.Browser.Filtered;
        var expectedScenario = ExpectedScenario.CreateFrom("Locking the gallery removes visible images and their grouping indexes.",
            new { ImageCount = 0, GroupCount = 0 });

        // Act
        home.Dispose();

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ImageCount = home.Browser.Filtered.Count,
            GroupCount = home.Browser.Browsing.PromptGroupsByImageId.Count
        });
    }

    [Fact]
    public async Task Partial_catalog_rejects_page_and_grouping_changes()
    {
        using var vault = new TestVault();
        var source = new BrowsingCatalogSource(vault.Scope.Catalog, CreateImages(15), Task.Delay(Timeout.Infinite));
        using var home = await OpenSessionAsync(vault, source, initialPageSize: 14);
        var browser = home.Browser;
        var selected = browser.Filtered[0].Id;
        browser.SelectCard(selected, range: false);

        Assert.True(home.Catalog.IsPartial);
        Assert.Equal(2, browser.PageCount); // Page 1 is in range, but navigation must still wait.
        await browser.GoToPageAsync(1);
        Assert.Equal(0, browser.Page);
        await browser.SetGroupingAsync("prompt");
        Assert.Equal("none", browser.Grouping);
        Assert.Contains(selected, browser.LibrarySelection);
    }

    [Fact]
    public async Task Pagination_preserves_selection_and_changing_grouping_resets_the_view()
    {
        using var vault = new TestVault();
        using var home = await OpenSessionAsync(vault, CreateImages(15));
        var browser = home.Browser;
        var selected = browser.Filtered[0].Id;
        browser.SelectCard(selected, range: false);

        await browser.GoToPageAsync(1);
        Assert.Equal(1, browser.Page);
        Assert.Contains(selected, browser.LibrarySelection);

        await browser.GoToPageAsync(-1);
        Assert.Equal(1, browser.Page);
        await browser.GoToPageAsync(browser.PageCount);
        Assert.Equal(1, browser.Page);

        await browser.SetGroupingAsync("prompt");
        Assert.Equal("prompt", browser.Grouping);
        Assert.Equal(0, browser.Page);
        Assert.Empty(browser.LibrarySelection);
        Assert.Null(browser.SelectionAnchor);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("trash")]
    [InlineData("delete")]
    public async Task HomeProjection_WhenPendingPromptGroupShrinks_ThenKeepsItsPlaceAndLastItem(string action)
    {
        using var testVault = new TestVault();
        await testVault.InitializeAsync();
        var images = CreateImages(5);
        for (var index = 0; index < images.Count; index++)
            images[index] = images[index] with { Generation = new() { Prompt = index < 3 ? "First prompt" : "Second prompt" } };
        var catalog = new BrowsingCatalogSource(testVault.Scope.Catalog, images);
        using var home = await OpenSessionAsync(testVault, catalog);
        await home.Browser.SetGroupingAsync("prompt");
        await home.Browser.ApplyFilterAsync(new GalleryFilter { Review = "pending", Sort = "name" });
        Assert.Equal(images.Select(image => image.Id), home.Browser.Filtered.Select(image => image.Id));

        if (action == "delete")
        {
            catalog.Records = images.Skip(2).ToList();
            await home.Catalog.ReloadAsync();
        }
        else
        {
            await home.Catalog.UpdateAsync(images.Take(2).Select(image => image with
            {
                IsReviewed = action == "review", IsMarkedForDeletion = action == "trash"
            }).ToArray(), reloadThumbnails: false);
        }
        await home.Browser.PauseAsync();

        Assert.Equal(images.Skip(2).Select(image => image.Id), home.Browser.Filtered.Select(image => image.Id));
        var group = home.Browser.Browsing.PromptGroupsByImageId[images[2].Id];
        Assert.Equal("First prompt", group.Prompt);
        Assert.Equal(images[2].Id, Assert.Single(group.Images).Id);

        await home.Browser.ApplyFilterAsync(home.Browser.Filter with { Sort = "oldest" });
        Assert.Equal(images.Skip(3).Append(images[2]).Select(image => image.Id), home.Browser.Filtered.Select(image => image.Id));
        Assert.Equal(PromptGroup.Unique, home.Browser.Browsing.PromptGroupsByImageId[images[2].Id].Kind);
    }

    [Fact]
    public async Task HomeCuration_WhenTheDisplaySnapshotIsStale_ThenUsesTheCommittedResultWithoutReplayingTheEdit()
    {
        // Arrange
        using var testVault = new TestVault();
        await testVault.InitializeAsync();
        var sourceFileName = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".png";
        var sourceFolder = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expectedDescription = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var staleImageRecord = await testVault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), sourceFileName, sourceFolder);
        await testVault.Scope.Records.UpdateAsync(staleImageRecord.Id, imageRecordEdit => imageRecordEdit with { Description = expectedDescription });
        using var home = await OpenSessionAsync(testVault, [staleImageRecord]);
        await home.Browser.ApplyFilterAsync(new GalleryFilter { MinRating = 5 });
        var actualEditInvocationCount = 0;
        ImageRecordEdit Edit(ImageRecordEdit imageRecordEdit) =>
            imageRecordEdit with { Rating = ++actualEditInvocationCount, IsFavorite = !imageRecordEdit.IsFavorite };
        var expectedScenario = ExpectedScenario.CreateFrom("Curation applies an edit once to current state and displays the committed record without losing unrelated changes.", new
        {
            StoredCount = 1, DisplayedCount = 1, EditInvocationCount = 1,
            Description = expectedDescription, IsFavorite = true, StoredMatchesDisplayed = true
        });

        // Act
        await home.Curation.CurateAsync([staleImageRecord.Id, staleImageRecord.Id], Edit, "saved");

        // Assert - gather the persisted and displayed records
        var actualImageRecordsStored = await testVault.Scope.Catalog.ListAsync();
        var actualImageRecordsDisplayed = home.Catalog.Images;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            StoredCount = actualImageRecordsStored.Count, DisplayedCount = actualImageRecordsDisplayed.Count,
            EditInvocationCount = actualEditInvocationCount,
            Description = actualImageRecordsDisplayed.FirstOrDefault()?.Description,
            IsFavorite = actualImageRecordsDisplayed.FirstOrDefault()?.IsFavorite,
            StoredMatchesDisplayed = JsonSerializer.Serialize(actualImageRecordsStored) == JsonSerializer.Serialize(actualImageRecordsDisplayed)
        });
    }

    [Fact]
    public async Task TrashSelection_WhenARangeIsSelected_ThenIncludesTheRangeWithoutChangingLibrarySelection()
    {
        using var vault = new TestVault();
        var records = CreateImages(3).Select(image => image with { IsMarkedForDeletion = true }).ToList();
        using var home = await OpenSessionAsync(vault, records);
        var browser = home.Browser;
        await browser.SetActiveToolAsync("trash");
        var visible = browser.Filtered;
        browser.SelectCard(visible[0].Id, range: false);
        browser.SelectCard(visible[2].Id, range: true);

        Assert.Contains(visible[1].Id, browser.RemovalSelection);
        Assert.Empty(browser.LibrarySelection);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Changing_view_starts_a_new_range(bool inTrash, bool resetFilters)
    {
        using var vault = new TestVault();
        var records = CreateImages(4).Select(r => r with { IsMarkedForDeletion = inTrash }).ToList();
        using var session = await OpenSessionAsync(vault, records);
        var browser = session.Browser;
        if (inTrash) await browser.SetActiveToolAsync("trash");
        var selection = () => inTrash ? browser.RemovalSelection : browser.LibrarySelection;
        var first = browser.Filtered[0].Id;
        var next = browser.Filtered[^1].Id;
        browser.SelectCard(first, range: false);
        Assert.Equal(first, Assert.Single(selection()));

        if (resetFilters) await browser.ResetFiltersAsync();
        else await browser.SetSortAsync("name");

        Assert.Empty(selection());
        browser.SelectCard(next, range: true);
        Assert.Equal(next, Assert.Single(selection()));
    }

    [Fact]
    public async Task TrashSelection_WhenCleared_ThenRemovesTheSelectedCards()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(4).Select(imageRecord => imageRecord with { IsMarkedForDeletion = true }).ToList();
        using var home = await OpenSessionAsync(testVault, imageRecords);
        await home.Browser.SetActiveToolAsync("trash");
        home.Browser.SelectCard(imageRecords[0].Id, false);
        string[] expectedSelectedIds = [];

        // Act
        home.Browser.ClearSelection();

        // Assert
        Assert.Equal(expectedSelectedIds, home.Browser.RemovalSelection);
    }

    [Fact]
    public async Task TrashTool_WhenSelected_ThenShowsOnlyMarkedImages()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(2);
        imageRecords[1] = imageRecords[1] with { IsMarkedForDeletion = true };
        using var home = await OpenSessionAsync(testVault, imageRecords);
        await home.Browser.ApplyFilterAsync(new GalleryFilter { Search = imageRecords[0].Name });
        var expectedScenario = ExpectedScenario.CreateFrom("Selecting Trash replaces the browsing filter with marked images.",
            new { Deletion = "marked", ImageIds = new[] { imageRecords[1].Id } });

        // Act
        await home.Browser.SetActiveToolAsync("trash");

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Deletion = home.Browser.Filter.Deletion,
            ImageIds = home.Browser.Filtered.Select(imageRecord => imageRecord.Id).ToArray()
        });
    }

    [Fact]
    public async Task TrashTool_WhenBrowsingResumes_ThenRestoresTheBrowsingFilter()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(2);
        imageRecords[1] = imageRecords[1] with { IsMarkedForDeletion = true };
        using var home = await OpenSessionAsync(testVault, imageRecords);
        var galleryFilter = new GalleryFilter { Search = imageRecords[0].Name };
        await home.Browser.ApplyFilterAsync(galleryFilter);
        await home.Browser.SetActiveToolAsync("trash");
        var expectedScenario = ExpectedScenario.CreateFrom("Leaving Trash restores the exact browsing filter and clears its saved copy.",
            new { FilterRestored = true, HasSavedBrowsingFilter = false });

        // Act
        await home.Browser.SetActiveToolAsync("views");

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            FilterRestored = galleryFilter == home.Browser.Filter,
            HasSavedBrowsingFilter = home.Browser.BrowsingFilter is not null
        });
    }

    private static ImageRecord[] CreateRatingImages()
    {
        var sharedPrompt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return
        [
            new() { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Rating = 1, Generation = new() { Prompt = sharedPrompt } },
            new() { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Rating = 5, Generation = new() { Prompt = sharedPrompt } }
        ];
    }


    private sealed class CountingImages(List<ImageRecord> imageRecords) : IReadOnlyList<ImageRecord>
    {
        public int Enumerations { get; private set; }
        public int Count => imageRecords.Count;
        public ImageRecord this[int index] { get => imageRecords[index]; set => imageRecords[index] = value; }

        public IEnumerator<ImageRecord> GetEnumerator()
        {
            Enumerations++;
            return imageRecords.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
