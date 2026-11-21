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
        // Arrange
        using var testVault = new TestVault();
        var imageIdOriginal = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var imageIdPeer = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var imageIdIndependent = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var sharedPrompt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var countingImagesOriginal = new CountingImages([
            new() { Id = imageIdOriginal, Generation = new() { Prompt = sharedPrompt } },
            new() { Id = imageIdPeer, Generation = new() { Prompt = sharedPrompt } }
        ]);
        var countingImagesIndependent = new CountingImages([new() { Id = imageIdIndependent, Rating = 5 }]);
        var actualGalleryBrowsingProjectionOriginal = GalleryBrowsingProjector.Create(
            testVault.Scope.Catalog, countingImagesOriginal, new GalleryFilter(), "prompt");
        countingImagesOriginal[0] = countingImagesOriginal[0] with { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) };
        var expectedScenario = ExpectedScenario.CreateFrom("Independent projections enumerate each source once and preserve immutable images and group identity.", new
        {
            OriginalIds = new[] { imageIdOriginal, imageIdPeer }.Order(StringComparer.Ordinal).ToArray(),
            IndependentIds = new[] { imageIdIndependent }, SharedGroup = true, IndependentGroupCount = 0,
            OriginalEnumerations = 1, IndependentEnumerations = 1
        });

        // Act
        var actualGalleryBrowsingProjectionIndependent = GalleryBrowsingProjector.Create(
            testVault.Scope.Catalog, countingImagesIndependent, new GalleryFilter { MinRating = 3 }, "none");

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            OriginalIds = actualGalleryBrowsingProjectionOriginal.Images.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray(),
            IndependentIds = actualGalleryBrowsingProjectionIndependent.Images.Select(imageRecord => imageRecord.Id).ToArray(),
            SharedGroup = ReferenceEquals(actualGalleryBrowsingProjectionOriginal.PromptGroupsByImageId[imageIdOriginal],
                actualGalleryBrowsingProjectionOriginal.PromptGroupsByImageId[imageIdPeer]),
            IndependentGroupCount = actualGalleryBrowsingProjectionIndependent.PromptGroupsByImageId.Count,
            OriginalEnumerations = countingImagesOriginal.Enumerations, IndependentEnumerations = countingImagesIndependent.Enumerations
        });
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

    [Fact]
    public void Projection_WhenUngrouped_ThenPreservesRatingOrderWithoutGroupIndexes()
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
            testVault.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating" }, "none");

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
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages();
        var expectedScenario = ExpectedScenario.CreateFrom("Images with the same prompt share one complete group.",
            new
            {
                GroupSize = 2, SharedGroup = true,
                GroupImageIds = imageRecords.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray()
            });

        // Act
        var actualGalleryBrowsingProjection = GalleryBrowsingProjector.Create(
            testVault.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating" }, "prompt");

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            GroupSize = actualGalleryBrowsingProjection.PromptGroupsByImageId[imageRecords[1].Id].Images.Length,
            GroupImageIds = actualGalleryBrowsingProjection.PromptGroupsByImageId[imageRecords[1].Id].Images
                .Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray(),
            SharedGroup = ReferenceEquals(actualGalleryBrowsingProjection.PromptGroupsByImageId[imageRecords[0].Id],
                actualGalleryBrowsingProjection.PromptGroupsByImageId[imageRecords[1].Id])
        });
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
    public void Projection_WhenGroupingIsUnrecognized_ThenPreservesSortOrderWithoutGroupIndexes()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages();
        var invalidGrouping = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expectedScenario = ExpectedScenario.CreateFrom("Unknown grouping values retain ungrouped sort order without group indexes.", new
        {
            ImageIds = new[] { imageRecords[1].Id, imageRecords[0].Id }, PromptGroupCount = 0, ModelGroupCount = 0
        });

        // Act
        var actualGalleryBrowsingProjection = GalleryBrowsingProjector.Create(
            testVault.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating" }, invalidGrouping);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ImageIds = actualGalleryBrowsingProjection.Images.Select(imageRecord => imageRecord.Id).ToArray(),
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
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages().ToList();
        using var home = await OpenSessionAsync(testVault, imageRecords);
        _ = home.Browser.Filtered;
        await home.Browser.SetGroupingAsync("prompt");
        var expectedScenario = ExpectedScenario.CreateFrom("Selecting prompt grouping replaces the empty index with complete shared groups.",
            new
            {
                GroupSize = 2, SharedGroup = true,
                GroupImageIds = imageRecords.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray()
            });

        // Act
        var actualPromptGroupsByImageId = home.Browser.Browsing.PromptGroupsByImageId;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            GroupSize = actualPromptGroupsByImageId[imageRecords[0].Id].Images.Length,
            GroupImageIds = actualPromptGroupsByImageId[imageRecords[0].Id].Images
                .Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray(),
            SharedGroup = ReferenceEquals(actualPromptGroupsByImageId[imageRecords[0].Id], actualPromptGroupsByImageId[imageRecords[1].Id])
        });
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
    public async Task Browser_only_navigates_to_available_pages_and_resets_the_view_when_grouping_changes()
    {
        using var vault = new TestVault();
        var images = CreateImages(15);
        var releaseCatalog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BrowsingCatalogSource(vault.Scope.Catalog, images, releaseCatalog.Task);
        using var home = await OpenSessionAsync(vault, source);
        var browser = home.Browser;
        var fullyLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        home.Catalog.Changed += () =>
        {
            if (!home.Catalog.IsPartial) fullyLoaded.TrySetResult();
            return Task.CompletedTask;
        };
        browser.SelectCard(images[0].Id, range: false);

        Assert.True(home.Catalog.IsPartial);
        Assert.Equal(browser.PageSize, home.Catalog.Images.Count);
        await browser.GoToPageAsync(1);
        await browser.SetGroupingAsync("prompt");
        Assert.Equal(0, browser.Page);
        Assert.Equal("none", browser.Grouping);
        Assert.Contains(images[0].Id, browser.LibrarySelection);

        releaseCatalog.SetResult();
        await fullyLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(images.Count, home.Catalog.Images.Count);
        await browser.GoToPageAsync(1);
        Assert.Equal(1, browser.Page);
        Assert.Contains(images[0].Id, browser.LibrarySelection);

        await browser.GoToPageAsync(-1);
        await browser.GoToPageAsync(browser.PageCount);
        Assert.Equal(1, browser.Page);

        await browser.SetGroupingAsync("prompt");
        Assert.Equal("prompt", browser.Grouping);
        Assert.Equal(0, browser.Page);
        Assert.Empty(browser.LibrarySelection);
        Assert.Null(browser.SelectionAnchor);
        await browser.PauseAsync();
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
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(4).Select(imageRecord => imageRecord with { IsMarkedForDeletion = true }).ToList();
        using var home = await OpenSessionAsync(testVault, imageRecords);
        await home.Browser.SetActiveToolAsync("trash");
        await home.Browser.ApplyFilterAsync(new GalleryFilter { Deletion = "marked", Sort = "name" });
        home.Browser.SelectCard(imageRecords[0].Id, false);
        var expectedScenario = ExpectedScenario.CreateFrom("Trash range selection includes the full range without changing library selection.", new
        {
            SelectedIds = imageRecords.Take(3).Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray(),
            LibrarySelectionCount = 0
        });

        // Act
        home.Browser.SelectCard(imageRecords[2].Id, true);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            SelectedIds = home.Browser.RemovalSelection.Order(StringComparer.Ordinal).ToArray(),
            LibrarySelectionCount = home.Browser.LibrarySelection.Count
        });
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
    public async Task TrashSelection_WhenASelectedCardIsClicked_ThenRemovesOnlyThatCard()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(4).Select(imageRecord => imageRecord with { IsMarkedForDeletion = true }).ToList();
        using var home = await OpenSessionAsync(testVault, imageRecords);
        await home.Browser.SetActiveToolAsync("trash");
        await home.Browser.ApplyFilterAsync(new GalleryFilter { Deletion = "marked", Sort = "name" });
        home.Browser.SelectCard(imageRecords[0].Id, false);
        home.Browser.SelectCard(imageRecords[2].Id, true);
        var expectedSelectedIds = new[] { imageRecords[0].Id, imageRecords[2].Id }.Order(StringComparer.Ordinal);

        // Act
        home.Browser.SelectCard(imageRecords[1].Id, false);

        // Assert
        Assert.Equal(expectedSelectedIds, home.Browser.RemovalSelection.Order(StringComparer.Ordinal));
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
