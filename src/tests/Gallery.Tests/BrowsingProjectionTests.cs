using Gallery.Core.Services.Processors;
using Gallery.Web.Components.Workspace;
// Class tests pin projection identity, enumeration and private component transitions that HTTP cannot expose.
using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Web.Components;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ShadCn.Blazor.Components;
using Microsoft.JSInterop;
using Gallery.Web.Components.Browsing;

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

        var projection = GalleryBrowsingProjector.Create(vault.Service.Scope.Catalog, images, filter, grouping, draftIds);

        Assert.Equal(new[] { "new-draft", "reanalyzed" }, projection.Images.Select(image => image.Id).Order());
        Assert.Empty(vault.Service.Filter(images, filter));
        Assert.Empty(GalleryBrowsingProjector.Create(vault.Service.Scope.Catalog, images, filter, grouping, new HashSet<string>()).Images);
    }

    [Fact]
    public async Task AwaitingApproval_RefreshesCachedResultsAndClampsPageWhenDraftsChange()
    {
        using var vault = new TestVault();
        var images = CreateImages(60);
        using var home = CreateSession(vault, images);
        SetField(home, "Browser.Filter", new GalleryFilter { Analysis = GalleryFilter.AwaitingApproval });
        Assert.Empty(GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered"));
        var suggestions = images.Select(image => new OrganizationSuggestion(image.Id, image.Name, "Draft", "", "vision")).ToArray();

        await (Task)Invoke(home, "Browser.SuggestionsChangedAsync", (object)suggestions)!;
        Assert.Equal(60, GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered").Count);
        SetField(home, "Browser.Page", 4);

        await (Task)Invoke(home, "Browser.SuggestionsChangedAsync", (object)suggestions.Take(1).ToArray())!;
        Assert.Equal(images[0].Id, Assert.Single(GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered")).Id);
        Assert.Equal(0, GetField<int>(home, "Browser.Page"));

        await (Task)Invoke(home, "Browser.SuggestionsChangedAsync", (object)Array.Empty<OrganizationSuggestion>())!;
        Assert.Empty(GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered"));
        Assert.Equal(0, GetField<int>(home, "Browser.Page"));
    }

    [Fact]
    public void IngestionDefaults_WhenSessionIsCreated_ThenUsesTheSafeHddSettings()
    {
        // Arrange
        var expectedScenario = ExpectedScenario.CreateFrom("Ingestion starts recursively with safe HDD settings and no source deletion.", new
        {
            Recursive = true, DeleteSources = false, RecordTimings = false, Profile = "hdd",
            Workers = IngestionOptions.Hdd.Workers, BatchSize = IngestionOptions.Hdd.CatalogBatchSize
        });

        // Act
        var session = new IngestionSession();

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Recursive = session.Recursive, DeleteSources = session.DeleteSources,
            RecordTimings = session.RecordTimings, Profile = session.Profile,
            Workers = session.Workers, BatchSize = session.BatchSize
        });
    }

    // Applying a profile copies its worker and batch settings and identifies the selected profile.
    [Theory]
    [InlineData("ssd")]
    [InlineData("hdd")]
    [InlineData("custom")]
    public void IngestionProfile_WhenApplied_ThenCopiesTheSettingsAndProfileName(string expectedProfile)
    {
        // Arrange
        var session = new IngestionSession();
        var ingestionOptions = expectedProfile switch
        {
            "ssd" => IngestionOptions.Ssd,
            "hdd" => IngestionOptions.Hdd,
            _ => new IngestionOptions { Workers = 7, CatalogBatchSize = 21 }
        };
        var expectedScenario = ExpectedScenario.CreateFrom("Selecting an ingestion profile copies its worker and batch settings.", new
        {
            Profile = expectedProfile, ingestionOptions.Workers, BatchSize = ingestionOptions.CatalogBatchSize
        });
        var profileOverride = expectedProfile == "ssd" ? expectedProfile : null;

        // Act
        session.ApplyProfile(ingestionOptions, profileOverride);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Profile = session.Profile,
            Workers = session.Workers, BatchSize = session.BatchSize
        });
    }

    // Ingestion requires a completed, nonempty, affordable scan while the component is idle.
    [Theory]
    [InlineData(false, false, long.MaxValue, 1, false, false)]
    [InlineData(true, false, long.MaxValue, 1, false, true)]
    [InlineData(true, true, long.MaxValue, 1, false, false)]
    [InlineData(true, false, long.MaxValue, 1, true, true)]
    [InlineData(true, false, 0, 1, false, false)]
    [InlineData(true, false, 0, 1, true, false)]
    [InlineData(true, false, long.MaxValue, 0, true, false)]
    public void IngestionGate_WhenScanConditionsVary_ThenRequiresAllPrerequisites(
        bool hasScan, bool isBusy, long mediaAvailableBytes, int fileCount, bool deletesSources, bool expectedCanIngest)
    {
        // Arrange
        var session = new IngestionSession();
        var ingestionScan = new IngestionScan(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), FolderSearchScope.Descendants,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            [new IngestionScanFile(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".png", 100, DateTime.UtcNow, false)]);
        var ingestionSpaceEstimate = IngestionSourceScanner.CalculateIngestionSpace(1, 100,
            long.MaxValue, long.MaxValue, true) with { MediaAvailableBytes = mediaAvailableBytes, FileCount = fileCount };
        if (hasScan) session.Scan = ingestionScan;
        session.Space = ingestionSpaceEstimate;
        session.DeleteSources = deletesSources;

        // Act
        var actualCanIngest = session.CanIngest(isBusy);

        // Assert
        Assert.Equal(expectedCanIngest, actualCanIngest);
    }

    // Toggling source deletion adds or removes the selected source bytes from the estimated change.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IngestionEstimate_WhenSourceDeletionIsToggled_ThenReflectsTheSelectedSourceBytes(bool initiallyDeletesSources)
    {
        // Arrange
        var session = new IngestionSession();
        var ingestionSpaceEstimate = IngestionSourceScanner.CalculateIngestionSpace(1, 100, long.MaxValue, long.MaxValue, true);
        session.Space = ingestionSpaceEstimate;
        session.DeleteSources = initiallyDeletesSources;
        var expectedScenario = ExpectedScenario.CreateFrom("Source deletion subtracts selected source bytes from the estimated storage change.", new
        {
            DeletesSources = !initiallyDeletesSources,
            ChangeBytes = initiallyDeletesSources
                ? ingestionSpaceEstimate.EstimatedBytes
                : ingestionSpaceEstimate.EstimatedBytes - ingestionSpaceEstimate.SelectedSourceBytes
        });

        // Act
        session.DeleteSources = !session.DeleteSources;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            DeletesSources = session.DeleteSources,
            ChangeBytes = session.EstimatedChangeBytes
        });
    }

    [Fact]
    public void IngestionScan_WhenRecursionChanges_ThenDisablesRecursionAndIngestionAndClearsTheScanAndSpaceEstimate()
    {
        // Arrange
        var session = new IngestionSession();
        session.Scan = new IngestionScan(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), FolderSearchScope.Descendants,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), []);
        session.Space = IngestionSourceScanner.CalculateIngestionSpace(1, 100, long.MaxValue, long.MaxValue, true);
        var expectedScenario = ExpectedScenario.CreateFrom("Changing recursion invalidates the scan and estimate and disables ingestion.", new
        {
            Recursive = false, HasScan = false, HasSpaceEstimate = false, CanIngest = false
        });

        // Act
        session.ToggleRecursion();

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Recursive = session.Recursive,
            HasScan = session.Scan is not null,
            HasSpaceEstimate = session.Space is not null,
            CanIngest = session.CanIngest(false)
        });
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
        var actualGalleryBrowsingProjectionOriginal = GalleryBrowsingProjector.CreateGroupedByPrompt(
            testVault.Service.Scope.Catalog, countingImagesOriginal, new GalleryFilter());
        countingImagesOriginal[0] = countingImagesOriginal[0] with { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) };
        var expectedScenario = ExpectedScenario.CreateFrom("Independent projections enumerate each source once and preserve immutable images and group identity.", new
        {
            OriginalIds = new[] { imageIdOriginal, imageIdPeer }.Order(StringComparer.Ordinal).ToArray(),
            IndependentIds = new[] { imageIdIndependent }, SharedGroup = true, IndependentGroupCount = 0,
            OriginalEnumerations = 1, IndependentEnumerations = 1
        });

        // Act
        var actualGalleryBrowsingProjectionIndependent = GalleryBrowsingProjector.CreateUngrouped(
            testVault.Service.Scope.Catalog, countingImagesIndependent, new GalleryFilter { MinRating = 3 });

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
        var actualGalleryBrowsingProjection = GalleryBrowsingProjector.CreateUngrouped(
            testVault.Service.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating" });

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
        var actualGalleryBrowsingProjection = GalleryBrowsingProjector.CreateUngrouped(
            testVault.Service.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating", MinRating = 3 });

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
        var actualGalleryBrowsingProjection = GalleryBrowsingProjector.CreateGroupedByPrompt(
            testVault.Service.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating" });

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
            testVault.Service.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating" }, "model");

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
            testVault.Service.Scope.Catalog, imageRecords, new GalleryFilter { MinRating = 5 }, grouping);

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
            testVault.Service.Scope.Catalog, imageRecords, new GalleryFilter { Sort = "rating" }, invalidGrouping);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ImageIds = actualGalleryBrowsingProjection.Images.Select(imageRecord => imageRecord.Id).ToArray(),
            PromptGroupCount = actualGalleryBrowsingProjection.PromptGroupsByImageId.Count,
            ModelGroupCount = actualGalleryBrowsingProjection.DiffusionModelGroupsByImageId.Count
        });
    }

    [Fact]
    public void HomeProjection_WhenPaginationChanges_ThenReusesTheSnapshotAndShowsTheCorrectPageCounts()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(60);
        using var home = CreateSession(testVault, imageRecords);
        var priorImageRecords = GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered");
        SetField(home, "Browser.Page", 1);
        SetField(home, "Browser.Density", "compact");
        var expectedScenario = ExpectedScenario.CreateFrom("Pagination reuses the filtered snapshot while calculating the current page boundaries.",
            new { ReusesSnapshot = true, PageCount = 2, PageImageCount = 11 });

        // Act
        var actualImageRecords = (IReadOnlyList<ImageRecord>)ComponentTestAccess.Get<object>(home, "Browser.Filtered")!;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ReusesSnapshot = ReferenceEquals(priorImageRecords, actualImageRecords),
            PageCount = GetProperty<int>(home, "Browser.PageCount"),
            PageImageCount = GetProperty<IEnumerable<ImageRecord>>(home, "Browser.PageImages").Count()
        });
    }

    [Fact]
    public void HomeProjection_WhenRatingFilterChanges_ThenReplacesTheSnapshotWithMatchingImages()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages().ToList();
        using var home = CreateSession(testVault, imageRecords);
        var priorImageRecords = GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered");
        SetField(home, "Browser.Filter", new GalleryFilter { MinRating = 5 });
        var expectedScenario = ExpectedScenario.CreateFrom("Changing the rating filter replaces the cached snapshot with matching images.",
            new { ReusesSnapshot = false, ImageIds = new[] { imageRecords[1].Id } });

        // Act
        var actualImageRecords = (IReadOnlyList<ImageRecord>)ComponentTestAccess.Get<object>(home, "Browser.Filtered")!;

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
        using var home = CreateSession(testVault, imageRecords);
        SetField(home, "Browser.Filter", new GalleryFilter { MinRating = 5 });
        _ = GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered");
        await testVault.InitializeAsync();
        var updatedImageRecord = imageRecords[0] with { Rating = 5 };
        var expectedImageIds = imageRecords.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray();

        // Act
        await home.Catalog.UpdateAsync(updatedImageRecord);
        await home.Browser.PauseAsync();

        // Assert - gather the updated projection
        var actualImageRecords = GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered");

        // Assert
        Assert.Equal(expectedImageIds, actualImageRecords.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void HomeProjection_WhenPromptGroupingIsSelected_ThenRebuildsTheGroupIndex()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages().ToList();
        using var home = CreateSession(testVault, imageRecords);
        _ = GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered");
        SetField(home, "Browser.Grouping", "prompt");
        var expectedScenario = ExpectedScenario.CreateFrom("Selecting prompt grouping replaces the empty index with complete shared groups.",
            new
            {
                GroupSize = 2, SharedGroup = true,
                GroupImageIds = imageRecords.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray()
            });

        // Act
        var actualPromptGroupsByImageId = (IReadOnlyDictionary<string, PromptGroup>)ComponentTestAccess.Get<object>(home, "Browser.Browsing.PromptGroupsByImageId")!;

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
    public void WorkspaceProjection_WhenDisposed_ThenClearsImagesAndGroups()
    {
        // Arrange
        using var testVault = new TestVault();
        using var home = CreateSession(testVault, CreateRatingImages().ToList());
        SetField(home, "Browser.Grouping", "prompt");
        _ = GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered");
        var expectedScenario = ExpectedScenario.CreateFrom("Locking the gallery removes visible images and their grouping indexes.",
            new { ImageCount = 0, GroupCount = 0 });

        // Act
        home.Dispose();

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ImageCount = GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered").Count,
            GroupCount = GetProperty<IReadOnlyDictionary<string, PromptGroup>>(home, "Browser.Browsing.PromptGroupsByImageId").Count
        });
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
        using var home = CreateSession(testVault, images);
        home.Browser.Grouping = "prompt";
        home.Browser.Filter = new GalleryFilter { Review = "pending", Sort = "name" };
        Assert.Equal(images.Select(image => image.Id), home.Browser.Filtered.Select(image => image.Id));

        if (action == "delete")
        {
            var survivors = images.Skip(2).ToArray();
            SetField(home, "Catalog.Images", survivors);
            SetField(home, "Catalog.ImagesById", survivors.ToDictionary(image => image.Id));
            await home.Browser.CatalogChangedAsync();
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

        home.Browser.Filter = home.Browser.Filter with { Sort = "oldest" };
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
        var staleImageRecord = await testVault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), sourceFileName, sourceFolder);
        await testVault.Service.UpdateAsync(staleImageRecord.Id, imageRecordEdit => imageRecordEdit with { Description = expectedDescription });
        using var home = CreateSession(testVault, [staleImageRecord]);
        SetField(home, "Browser.Filter", new GalleryFilter { MinRating = 5 });
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
        var actualImageRecordsStored = await testVault.Service.ListAsync();
        var actualImageRecordsDisplayed = GetField<IReadOnlyList<ImageRecord>>(home, "Catalog.Images");

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
    public void GuidedReview_WhenStartedWithTheDefaultGoal_ThenOpensADetachedReviewOfTenEligibleImagesAcrossPagesInSortOrder()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(30);
        imageRecords[0] = imageRecords[0] with { IsReviewed = true };
        imageRecords[1] = imageRecords[1] with { IsMarkedForDeletion = true };
        using var home = CreateSession(testVault, imageRecords);
        SetField(home, "Browser.Filter", new GalleryFilter { Sort = "name" });
        SetField(home, "Browser.Density", "detail");
        SetField(home, "Browser.Page", 1);
        const int ExpectedReviewGoal = 10;
        var expectedImageIds = imageRecords.Skip(2).Take(ExpectedReviewGoal).Select(imageRecord => imageRecord.Id).ToArray();
        var expectedScenario = ExpectedScenario.CreateFrom("Default review selects ten eligible images in library sort order, independent of pagination, in a detached session.", new
        {
            Goal = ExpectedReviewGoal, ImageIds = expectedImageIds, InitialId = expectedImageIds[0], ReviewMode = true, IsDetachedSnapshot = true
        });

        // Act
        home.Presentation.StartReview(home.Browser.ReviewPool, home.Browser.ReviewGoal, home.Browser.ReviewRandomOrder);

        // Assert - gather the review session
        var actualImageRecords = GetField<List<ImageRecord>>(home, "Presentation.ViewerImages");

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Goal = GetProperty<int>(home, "Browser.ReviewGoal"), ImageIds = actualImageRecords.Select(imageRecord => imageRecord.Id).ToArray(),
            InitialId = GetField<string>(home, "Presentation.ViewerInitialId"), ReviewMode = GetField<bool>(home, "Presentation.ReviewMode"),
            IsDetachedSnapshot = actualImageRecords.Count > 0 && !ReferenceEquals(imageRecords[2], actualImageRecords[0])
        });
    }

    // Review goals are clamped to at least one and at most the eligible filtered pool.
    [Theory]
    [InlineData(100, 6)]
    [InlineData(3, 3)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void GuidedReview_WhenTheGoalIsOutsideThePoolBounds_ThenClampsTheGoalAndSession(int requestedGoal, int expectedGoal)
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(20);
        for (var index = 0; index < imageRecords.Count; index++)
            imageRecords[index] = imageRecords[index] with
            {
                IsFavorite = index < 8, IsReviewed = index == 0, IsMarkedForDeletion = index == 1
            };
        using var home = CreateSession(testVault, imageRecords);
        SetField(home, "Browser.Filter", new GalleryFilter { IncludesFavoritesOnly = true });
        ComponentTestAccess.Set(home, "Browser.ReviewGoal", requestedGoal);
        var expectedScenario = ExpectedScenario.CreateFrom("Review goals and session sizes stay within the eligible filtered pool.",
            new { Goal = expectedGoal, SessionCount = expectedGoal, AllEligible = true });

        // Act
        home.Presentation.StartReview(home.Browser.ReviewPool, home.Browser.ReviewGoal, home.Browser.ReviewRandomOrder);

        // Assert - gather the review session
        var actualImageRecords = GetField<List<ImageRecord>>(home, "Presentation.ViewerImages");

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Goal = GetProperty<int>(home, "Browser.ReviewGoal"), SessionCount = actualImageRecords.Count,
            AllEligible = actualImageRecords.All(imageRecord => imageRecord.IsFavorite && !imageRecord.IsReviewed && !imageRecord.IsMarkedForDeletion)
        });
    }

    [Fact]
    public void GuidedReview_WhenRandomOrderIsSelected_ThenChoosesUniqueEligibleImagesWithoutChangingTheLibrary()
    {
        // Arrange
        const int ExpectedReviewGoal = 7;
        using var testVault = new TestVault();
        var imageRecords = CreateImages(80);
        for (var index = 0; index < imageRecords.Count; index++)
            imageRecords[index] = imageRecords[index] with
            {
                IsFavorite = index % 2 == 0, IsReviewed = index < 10, IsMarkedForDeletion = index > 70
            };
        using var home = CreateSession(testVault, imageRecords);
        SetField(home, "Browser.Filter", new GalleryFilter { IncludesFavoritesOnly = true });
        SetField(home, "Browser.ReviewRandomOrder", true);
        SetField(home, "Browser.ReviewGoal", ExpectedReviewGoal);
        var eligibleImageIds = imageRecords.Where(imageRecord => imageRecord.IsFavorite && !imageRecord.IsReviewed && !imageRecord.IsMarkedForDeletion)
            .Select(imageRecord => imageRecord.Id).ToHashSet(StringComparer.Ordinal);
        var expectedScenario = ExpectedScenario.CreateFrom("Random review selects unique eligible images without reordering the library.", new
        {
            SessionCount = ExpectedReviewGoal, UniqueCount = ExpectedReviewGoal, AllEligible = true,
            LibraryOrder = imageRecords.Select(imageRecord => imageRecord.Id).ToArray()
        });

        // Act
        home.Presentation.StartReview(home.Browser.ReviewPool, home.Browser.ReviewGoal, home.Browser.ReviewRandomOrder);

        // Assert - gather the review session
        var actualImageRecords = GetField<List<ImageRecord>>(home, "Presentation.ViewerImages");

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            SessionCount = actualImageRecords.Count, UniqueCount = actualImageRecords.Select(imageRecord => imageRecord.Id).Distinct().Count(),
            AllEligible = actualImageRecords.All(imageRecord => eligibleImageIds.Contains(imageRecord.Id)),
            LibraryOrder = imageRecords.Select(imageRecord => imageRecord.Id).ToArray()
        });
    }

    // The displayed goal and a new session both shrink to the remaining eligible pool, including zero.
    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    public void GuidedReview_WhenTheEligiblePoolShrinks_ThenCapsTheDisplayedGoalAndSession(bool includesFavorites, int expectedCount)
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(20);
        for (var index = 0; index < 3; index++) imageRecords[index] = imageRecords[index] with { IsFavorite = true };
        using var home = CreateSession(testVault, imageRecords);
        SetField(home, "Browser.Filter", includesFavorites
            ? new GalleryFilter { IncludesFavoritesOnly = true }
            : new GalleryFilter { MinRating = 5 });
        var expectedScenario = ExpectedScenario.CreateFrom("A reduced eligible pool caps both the displayed goal and the next review session.",
            new { Goal = expectedCount, SessionCount = expectedCount });

        // Act
        home.Presentation.StartReview(home.Browser.ReviewPool, home.Browser.ReviewGoal, home.Browser.ReviewRandomOrder);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario,
            new { Goal = GetProperty<int>(home, "Browser.ReviewGoal"), SessionCount = GetField<List<ImageRecord>>(home, "Presentation.ViewerImages").Count });
    }

    [Fact]
    public void TrashSelection_WhenARangeIsSelected_ThenIncludesTheRangeWithoutChangingLibrarySelection()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(4).Select(imageRecord => imageRecord with { IsMarkedForDeletion = true }).ToList();
        using var home = CreateSession(testVault, imageRecords);
        SetField(home, "Browser.ActiveTool", "trash");
        SetField(home, "Browser.Filter", new GalleryFilter { Deletion = "marked", Sort = "name" });
        Invoke(home, "Browser.SelectCard", imageRecords[0].Id, false);
        var expectedScenario = ExpectedScenario.CreateFrom("Trash range selection includes the full range without changing library selection.", new
        {
            SelectedIds = imageRecords.Take(3).Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray(),
            LibrarySelectionCount = 0
        });

        // Act
        ComponentTestAccess.Call(home, "Browser.SelectCard", imageRecords[2].Id, true);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            SelectedIds = GetField<HashSet<string>>(home, "Browser.RemovalSelection").Order(StringComparer.Ordinal).ToArray(),
            LibrarySelectionCount = GetField<HashSet<string>>(home, "Browser.LibrarySelection").Count
        });
    }

    [Fact]
    public void TrashSelection_WhenASelectedCardIsClicked_ThenRemovesOnlyThatCard()
    {
        // Arrange
        const string SelectCardMethod = "Browser.SelectCard";
        using var testVault = new TestVault();
        var imageRecords = CreateImages(4).Select(imageRecord => imageRecord with { IsMarkedForDeletion = true }).ToList();
        using var home = CreateSession(testVault, imageRecords);
        SetField(home, "Browser.ActiveTool", "trash");
        SetField(home, "Browser.Filter", new GalleryFilter { Deletion = "marked", Sort = "name" });
        Invoke(home, SelectCardMethod, imageRecords[0].Id, false);
        Invoke(home, SelectCardMethod, imageRecords[2].Id, true);
        var expectedSelectedIds = new[] { imageRecords[0].Id, imageRecords[2].Id }.Order(StringComparer.Ordinal);

        // Act
        ComponentTestAccess.Call(home, SelectCardMethod, imageRecords[1].Id, false);

        // Assert
        Assert.Equal(expectedSelectedIds, GetField<HashSet<string>>(home, "Browser.RemovalSelection").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WorkbenchSavedView_WhenNameIsEmpty_ThenDisplaysTheNameValidationMessage()
    {
        // Arrange
        const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;
        using var testVault = new TestVault();
        using var libraryWorkbench = new LibraryWorkbench();
        var services = new ServiceCollection();
        services.AddShadCnBlazorComponents();
        using var provider = services.BuildServiceProvider();
        typeof(LibraryWorkbench).GetProperty("Toasts", InstanceMembers)!.SetValue(libraryWorkbench, provider.GetRequiredService<ISonnerService>());
        GalleryCapabilityTestSetup.InjectGalleryCapabilities(libraryWorkbench, testVault.Service);
        typeof(LibraryWorkbench).GetProperty("Logger", InstanceMembers)!
            .SetValue(libraryWorkbench, NullLogger<LibraryWorkbench>.Instance);
        string[] expectedMessagePhrases = ["Saved view name", "''", "1 to 80 characters"];

        // Act
        await (Task)typeof(LibraryWorkbench).GetMethod("SaveViewAsync", InstanceMembers)!.Invoke(libraryWorkbench, null)!;

        // Assert - gather the caller-facing message
        var actualErrorMessage = (string)typeof(LibraryWorkbench).GetField("error", InstanceMembers)!.GetValue(libraryWorkbench)!;

        // Assert
        AsserterExceptionContract.AssertMessageContains(expectedMessagePhrases, actualErrorMessage);
    }

    [Fact]
    public void TrashSelection_WhenCleared_ThenRemovesTheSelectedCards()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(4).Select(imageRecord => imageRecord with { IsMarkedForDeletion = true }).ToList();
        using var home = CreateSession(testVault, imageRecords);
        SetField(home, "Browser.ActiveTool", "trash");
        Invoke(home, "Browser.SelectCard", imageRecords[0].Id, false);
        string[] expectedSelectedIds = [];

        // Act
        home.Browser.ClearSelection();

        // Assert
        Assert.Equal(expectedSelectedIds, GetField<HashSet<string>>(home, "Browser.RemovalSelection"));
    }

    [Fact]
    public async Task TrashTool_WhenSelected_ThenShowsOnlyMarkedImages()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(2);
        imageRecords[1] = imageRecords[1] with { IsMarkedForDeletion = true };
        using var home = CreateSession(testVault, imageRecords);
        SetField(home, "Browser.Filter", new GalleryFilter { Search = imageRecords[0].Name });
        var expectedScenario = ExpectedScenario.CreateFrom("Selecting Trash replaces the browsing filter with marked images.",
            new { Deletion = "marked", ImageIds = new[] { imageRecords[1].Id } });

        // Act
        await (Task)ComponentTestAccess.Call(home, "Browser.SetActiveToolAsync", ["trash"])!;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Deletion = GetField<GalleryFilter>(home, "Browser.Filter").Deletion,
            ImageIds = GetProperty<IReadOnlyList<ImageRecord>>(home, "Browser.Filtered").Select(imageRecord => imageRecord.Id).ToArray()
        });
    }

    [Fact]
    public async Task TrashTool_WhenBrowsingResumes_ThenRestoresTheBrowsingFilter()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(2);
        imageRecords[1] = imageRecords[1] with { IsMarkedForDeletion = true };
        using var home = CreateSession(testVault, imageRecords);
        var galleryFilter = new GalleryFilter { Search = imageRecords[0].Name };
        SetField(home, "Browser.Filter", galleryFilter);
        await (Task)Invoke(home, "Browser.SetActiveToolAsync", "trash")!;
        var expectedScenario = ExpectedScenario.CreateFrom("Leaving Trash restores the exact browsing filter and clears its saved copy.",
            new { FilterRestored = true, HasSavedBrowsingFilter = false });

        // Act
        await (Task)ComponentTestAccess.Call(home, "Browser.SetActiveToolAsync", ["views"])!;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            FilterRestored = galleryFilter == GetField<GalleryFilter>(home, "Browser.Filter"),
            HasSavedBrowsingFilter = GetField<GalleryFilter?>(home, "Browser.BrowsingFilter") is not null
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

    private static List<ImageRecord> CreateImages(int count) =>
        Enumerable.Range(0, count).Select(index => new ImageRecord
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Name = index.ToString("D2")
        }).ToList();

    private static WorkspaceSession CreateSession(TestVault testVault, List<ImageRecord> imageRecords)
    {
        var home = new WorkspaceSession(GalleryCapabilityTestSetup.Workspace(testVault.Service), new(), new(testVault.Service.Scope.Collections),
            new NoOpJsRuntime(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, action => action(), () => { });
        SetField(home, "Catalog.Images", imageRecords);
        SetField(home, "Catalog.ImagesById", imageRecords.ToDictionary(imageRecord => imageRecord.Id));
        return home;
    }

    private static void SetField(WorkspaceSession home, string name, object value) =>
        ComponentTestAccess.Set(home, name, value);

    private static T GetField<T>(WorkspaceSession home, string name) =>
        ComponentTestAccess.Get<T>(home, name);

    private static T GetProperty<T>(WorkspaceSession home, string name) =>
        ComponentTestAccess.Get<T>(home, name);

    private static object? Invoke(WorkspaceSession home, string name, params object?[] arguments) =>
        ComponentTestAccess.Call(home, name, arguments);

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? arguments) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? arguments) =>
            InvokeAsync<TValue>(identifier, arguments);
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
