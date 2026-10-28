// Class tests pin projection identity, enumeration and private component transitions that HTTP cannot expose.
using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Web.Components;
using Gallery.Web.Components.Pages;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class BrowsingProjectionTests
{
    [Fact]
    public void IngestionDefaults_WhenHomeIsCreated_ThenUsesTheSafeHddSettings()
    {
        // Arrange
        var expectedScenario = ExpectedScenario.CreateFrom("Ingestion starts recursively with safe HDD settings and no source deletion.", new
        {
            Recursive = true, DeleteSources = false, RecordTimings = false, Profile = "hdd",
            Workers = IngestionOptions.Hdd.Workers, BatchSize = IngestionOptions.Hdd.CatalogBatchSize
        });

        // Act
        var home = new Home();

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Recursive = GetField<bool>(home, "recursive"), DeleteSources = GetField<bool>(home, "deleteSources"),
            RecordTimings = GetField<bool>(home, "recordTimings"), Profile = GetField<string>(home, "ingestionProfile"),
            Workers = GetField<int>(home, "ingestionWorkers"), BatchSize = GetField<int>(home, "ingestionBatchSize")
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
        var home = new Home();
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
        typeof(Home).GetMethod("ApplyIngestionProfile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(home, [ingestionOptions, profileOverride]);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Profile = GetField<string>(home, "ingestionProfile"),
            Workers = GetField<int>(home, "ingestionWorkers"), BatchSize = GetField<int>(home, "ingestionBatchSize")
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
        var home = new Home();
        var ingestionScan = new IngestionScan(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), FolderSearchScope.Descendants,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            [new IngestionScanFile(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".png", 100, DateTime.UtcNow, false)]);
        var ingestionSpaceEstimate = GalleryService.CalculateIngestionSpace(1, 100,
            long.MaxValue, long.MaxValue, true) with { MediaAvailableBytes = mediaAvailableBytes, FileCount = fileCount };
        if (hasScan) SetField(home, "ingestionScan", ingestionScan);
        SetField(home, "ingestionSpace", ingestionSpaceEstimate);
        SetField(home, "busy", isBusy);
        SetField(home, "deleteSources", deletesSources);

        // Act
        var actualCanIngest = (bool)typeof(Home).GetProperty("CanIngestScan", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(home)!;

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
        var home = new Home();
        var ingestionSpaceEstimate = GalleryService.CalculateIngestionSpace(1, 100, long.MaxValue, long.MaxValue, true);
        SetField(home, "ingestionSpace", ingestionSpaceEstimate);
        SetField(home, "deleteSources", initiallyDeletesSources);
        var expectedScenario = ExpectedScenario.CreateFrom("Source deletion subtracts selected source bytes from the estimated storage change.", new
        {
            DeletesSources = !initiallyDeletesSources,
            ChangeBytes = initiallyDeletesSources
                ? ingestionSpaceEstimate.EstimatedBytes
                : ingestionSpaceEstimate.EstimatedBytes - ingestionSpaceEstimate.SelectedSourceBytes
        });

        // Act
        typeof(Home).GetMethod("ToggleSourceDeletion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, null);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            DeletesSources = GetField<bool>(home, "deleteSources"),
            ChangeBytes = GetProperty<long>(home, "EstimatedIngestionChangeBytes")
        });
    }

    [Fact]
    public void IngestionScan_WhenRecursionChanges_ThenDisablesRecursionAndIngestionAndClearsTheScanAndSpaceEstimate()
    {
        // Arrange
        var home = new Home();
        SetField(home, "ingestionScan", new IngestionScan(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), FolderSearchScope.Descendants,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), []));
        SetField(home, "ingestionSpace", GalleryService.CalculateIngestionSpace(1, 100, long.MaxValue, long.MaxValue, true));
        var expectedScenario = ExpectedScenario.CreateFrom("Changing recursion invalidates the scan and estimate and disables ingestion.", new
        {
            Recursive = false, HasScan = false, HasSpaceEstimate = false, CanIngest = false
        });

        // Act
        typeof(Home).GetMethod("ToggleImportRecursion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, null);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Recursive = GetField<bool>(home, "recursive"),
            HasScan = GetField<IngestionScan?>(home, "ingestionScan") is not null,
            HasSpaceEstimate = GetField<IngestionSpaceEstimate?>(home, "ingestionSpace") is not null,
            CanIngest = GetProperty<bool>(home, "CanIngestScan")
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
            testVault.Service, countingImagesOriginal, new GalleryFilter());
        countingImagesOriginal[0] = countingImagesOriginal[0] with { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) };
        var expectedScenario = ExpectedScenario.CreateFrom("Independent projections enumerate each source once and preserve immutable images and group identity.", new
        {
            OriginalIds = new[] { imageIdOriginal, imageIdPeer }.Order(StringComparer.Ordinal).ToArray(),
            IndependentIds = new[] { imageIdIndependent }, SharedGroup = true, IndependentGroupCount = 0,
            OriginalEnumerations = 1, IndependentEnumerations = 1
        });

        // Act
        var actualGalleryBrowsingProjectionIndependent = GalleryBrowsingProjector.CreateUngrouped(
            testVault.Service, countingImagesIndependent, new GalleryFilter { MinRating = 3 });

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
            testVault.Service, imageRecords, new GalleryFilter { Sort = "rating" });

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
            testVault.Service, imageRecords, new GalleryFilter { Sort = "rating", MinRating = 3 });

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
            testVault.Service, imageRecords, new GalleryFilter { Sort = "rating" });

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
            testVault.Service, imageRecords, new GalleryFilter { Sort = "rating" }, "model");

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
            testVault.Service, imageRecords, new GalleryFilter { MinRating = 5 }, grouping);

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
            testVault.Service, imageRecords, new GalleryFilter { Sort = "rating" }, invalidGrouping);

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
        var home = CreateHome(testVault, imageRecords);
        var priorImageRecords = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered");
        SetField(home, "page", 1);
        SetField(home, "density", "compact");
        var expectedScenario = ExpectedScenario.CreateFrom("Pagination reuses the filtered snapshot while calculating the current page boundaries.",
            new { ReusesSnapshot = true, PageCount = 2, PageImageCount = 11 });

        // Act
        var actualImageRecords = (IReadOnlyList<ImageRecord>)typeof(Home)
            .GetProperty("Filtered", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(home)!;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ReusesSnapshot = ReferenceEquals(priorImageRecords, actualImageRecords),
            PageCount = GetProperty<int>(home, "PageCount"),
            PageImageCount = GetProperty<IEnumerable<ImageRecord>>(home, "PageImages").Count()
        });
    }

    [Fact]
    public void HomeProjection_WhenRatingFilterChanges_ThenReplacesTheSnapshotWithMatchingImages()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages().ToList();
        var home = CreateHome(testVault, imageRecords);
        var priorImageRecords = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered");
        SetField(home, "filter", new GalleryFilter { MinRating = 5 });
        var expectedScenario = ExpectedScenario.CreateFrom("Changing the rating filter replaces the cached snapshot with matching images.",
            new { ReusesSnapshot = false, ImageIds = new[] { imageRecords[1].Id } });

        // Act
        var actualImageRecords = (IReadOnlyList<ImageRecord>)typeof(Home)
            .GetProperty("Filtered", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(home)!;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ReusesSnapshot = ReferenceEquals(priorImageRecords, actualImageRecords),
            ImageIds = actualImageRecords.Select(imageRecord => imageRecord.Id).ToArray()
        });
    }

    [Fact]
    public void HomeProjection_WhenAnImageIsEdited_ThenIncludesTheNewlyMatchingImage()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages().ToList();
        var home = CreateHome(testVault, imageRecords);
        SetField(home, "filter", new GalleryFilter { MinRating = 5 });
        _ = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered");
        var updatedImageRecord = imageRecords[0] with { Rating = 5 };
        var expectedImageIds = imageRecords.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray();

        // Act
        typeof(Home).GetMethod("ViewerImageChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, [updatedImageRecord]);

        // Assert - gather the updated projection
        var actualImageRecords = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered");

        // Assert
        Assert.Equal(expectedImageIds, actualImageRecords.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void HomeProjection_WhenPromptGroupingIsSelected_ThenRebuildsTheGroupIndex()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateRatingImages().ToList();
        var home = CreateHome(testVault, imageRecords);
        _ = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered");
        SetField(home, "grouping", "prompt");
        var expectedScenario = ExpectedScenario.CreateFrom("Selecting prompt grouping replaces the empty index with complete shared groups.",
            new
            {
                GroupSize = 2, SharedGroup = true,
                GroupImageIds = imageRecords.Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray()
            });

        // Act
        var actualPromptGroupsByImageId = (IReadOnlyDictionary<string, PromptGroup>)typeof(Home)
            .GetProperty("promptGroupOf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(home)!;

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
    public void HomeProjection_WhenLocked_ThenClearsImagesAndGroups()
    {
        // Arrange
        using var testVault = new TestVault();
        var home = CreateHome(testVault, CreateRatingImages().ToList());
        SetField(home, "grouping", "prompt");
        _ = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered");
        var expectedScenario = ExpectedScenario.CreateFrom("Locking the gallery removes visible images and their grouping indexes.",
            new { ImageCount = 0, GroupCount = 0 });

        // Act
        typeof(Home).GetMethod("Lock", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, null);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            ImageCount = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered").Count,
            GroupCount = GetProperty<IReadOnlyDictionary<string, PromptGroup>>(home, "promptGroupOf").Count
        });
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
        var home = CreateHome(testVault, [staleImageRecord]);
        SetField(home, "filter", new GalleryFilter { MinRating = 5 });
        var actualEditInvocationCount = 0;
        ImageRecordEdit Edit(ImageRecordEdit imageRecordEdit) =>
            imageRecordEdit with { Rating = ++actualEditInvocationCount, IsFavorite = !imageRecordEdit.IsFavorite };
        var expectedScenario = ExpectedScenario.CreateFrom("Curation applies an edit once to current state and displays the committed record without losing unrelated changes.", new
        {
            StoredCount = 1, DisplayedCount = 1, EditInvocationCount = 1,
            Description = expectedDescription, IsFavorite = true, StoredMatchesDisplayed = true
        });

        // Act
        await (Task)typeof(Home).GetMethod("CurateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(home, [new[] { staleImageRecord.Id, staleImageRecord.Id }, (Func<ImageRecordEdit, ImageRecordEdit>)Edit, "saved"])!;

        // Assert - gather the persisted and displayed records
        var actualImageRecordsStored = await testVault.Service.ListAsync();
        var actualImageRecordsDisplayed = GetField<List<ImageRecord>>(home, "images");

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
        var home = CreateHome(testVault, imageRecords);
        SetField(home, "filter", new GalleryFilter { Sort = "name" });
        SetField(home, "density", "detail");
        SetField(home, "page", 1);
        const int ExpectedReviewGoal = 10;
        var expectedImageIds = imageRecords.Skip(2).Take(ExpectedReviewGoal).Select(imageRecord => imageRecord.Id).ToArray();
        var expectedScenario = ExpectedScenario.CreateFrom("Default review selects ten eligible images in library sort order, independent of pagination, in a detached session.", new
        {
            Goal = ExpectedReviewGoal, ImageIds = expectedImageIds, InitialId = expectedImageIds[0], ReviewMode = true, IsDetachedSnapshot = true
        });

        // Act
        typeof(Home).GetMethod("StartReview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, null);

        // Assert - gather the review session
        var actualImageRecords = GetField<List<ImageRecord>>(home, "viewerImages");

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Goal = GetProperty<int>(home, "ReviewGoal"), ImageIds = actualImageRecords.Select(imageRecord => imageRecord.Id).ToArray(),
            InitialId = GetField<string>(home, "viewerInitialId"), ReviewMode = GetField<bool>(home, "reviewMode"),
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
        var home = CreateHome(testVault, imageRecords);
        SetField(home, "filter", new GalleryFilter { IncludesFavoritesOnly = true });
        typeof(Home).GetProperty("ReviewGoal", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, requestedGoal);
        var expectedScenario = ExpectedScenario.CreateFrom("Review goals and session sizes stay within the eligible filtered pool.",
            new { Goal = expectedGoal, SessionCount = expectedGoal, AllEligible = true });

        // Act
        typeof(Home).GetMethod("StartReview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, null);

        // Assert - gather the review session
        var actualImageRecords = GetField<List<ImageRecord>>(home, "viewerImages");

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Goal = GetProperty<int>(home, "ReviewGoal"), SessionCount = actualImageRecords.Count,
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
        var home = CreateHome(testVault, imageRecords);
        SetField(home, "filter", new GalleryFilter { IncludesFavoritesOnly = true });
        SetField(home, "reviewRandomOrder", true);
        SetField(home, "reviewGoal", ExpectedReviewGoal);
        var eligibleImageIds = imageRecords.Where(imageRecord => imageRecord.IsFavorite && !imageRecord.IsReviewed && !imageRecord.IsMarkedForDeletion)
            .Select(imageRecord => imageRecord.Id).ToHashSet(StringComparer.Ordinal);
        var expectedScenario = ExpectedScenario.CreateFrom("Random review selects unique eligible images without reordering the library.", new
        {
            SessionCount = ExpectedReviewGoal, UniqueCount = ExpectedReviewGoal, AllEligible = true,
            LibraryOrder = imageRecords.Select(imageRecord => imageRecord.Id).ToArray()
        });

        // Act
        typeof(Home).GetMethod("StartReview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, null);

        // Assert - gather the review session
        var actualImageRecords = GetField<List<ImageRecord>>(home, "viewerImages");

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
        var home = CreateHome(testVault, imageRecords);
        SetField(home, "filter", includesFavorites
            ? new GalleryFilter { IncludesFavoritesOnly = true }
            : new GalleryFilter { MinRating = 5 });
        var expectedScenario = ExpectedScenario.CreateFrom("A reduced eligible pool caps both the displayed goal and the next review session.",
            new { Goal = expectedCount, SessionCount = expectedCount });

        // Act
        typeof(Home).GetMethod("StartReview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, null);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario,
            new { Goal = GetProperty<int>(home, "ReviewGoal"), SessionCount = GetField<List<ImageRecord>>(home, "viewerImages").Count });
    }

    [Fact]
    public void TrashSelection_WhenARangeIsSelected_ThenIncludesTheRangeWithoutChangingLibrarySelection()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(4).Select(imageRecord => imageRecord with { IsMarkedForDeletion = true }).ToList();
        var home = CreateHome(testVault, imageRecords);
        SetField(home, "activeTool", "trash");
        SetField(home, "filter", new GalleryFilter { Deletion = "marked", Sort = "name" });
        Invoke(home, "SelectCard", imageRecords[0].Id, false);
        var expectedScenario = ExpectedScenario.CreateFrom("Trash range selection includes the full range without changing library selection.", new
        {
            SelectedIds = imageRecords.Take(3).Select(imageRecord => imageRecord.Id).Order(StringComparer.Ordinal).ToArray(),
            LibrarySelectionCount = 0
        });

        // Act
        typeof(Home).GetMethod("SelectCard", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, [imageRecords[2].Id, true]);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            SelectedIds = GetField<HashSet<string>>(home, "removalSelection").Order(StringComparer.Ordinal).ToArray(),
            LibrarySelectionCount = GetField<HashSet<string>>(home, "librarySelection").Count
        });
    }

    [Fact]
    public void TrashSelection_WhenASelectedCardIsClicked_ThenRemovesOnlyThatCard()
    {
        // Arrange
        const string SelectCardMethod = "SelectCard";
        using var testVault = new TestVault();
        var imageRecords = CreateImages(4).Select(imageRecord => imageRecord with { IsMarkedForDeletion = true }).ToList();
        var home = CreateHome(testVault, imageRecords);
        SetField(home, "activeTool", "trash");
        SetField(home, "filter", new GalleryFilter { Deletion = "marked", Sort = "name" });
        Invoke(home, SelectCardMethod, imageRecords[0].Id, false);
        Invoke(home, SelectCardMethod, imageRecords[2].Id, true);
        var expectedSelectedIds = new[] { imageRecords[0].Id, imageRecords[2].Id }.Order(StringComparer.Ordinal);

        // Act
        typeof(Home).GetMethod(SelectCardMethod, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, [imageRecords[1].Id, false]);

        // Assert
        Assert.Equal(expectedSelectedIds, GetField<HashSet<string>>(home, "removalSelection").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WorkbenchSavedView_WhenNameIsEmpty_ThenDisplaysTheNameValidationMessage()
    {
        // Arrange
        const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;
        using var testVault = new TestVault();
        using var libraryWorkbench = new LibraryWorkbench();
        typeof(LibraryWorkbench).GetProperty("Gallery", InstanceMembers)!.SetValue(libraryWorkbench, testVault.Service);
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
        var home = CreateHome(testVault, imageRecords);
        SetField(home, "activeTool", "trash");
        Invoke(home, "SelectCard", imageRecords[0].Id, false);
        string[] expectedSelectedIds = [];

        // Act
        typeof(Home).GetMethod("ClearSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, null);

        // Assert
        Assert.Equal(expectedSelectedIds, GetField<HashSet<string>>(home, "removalSelection"));
    }

    [Fact]
    public async Task TrashTool_WhenSelected_ThenShowsOnlyMarkedImages()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(2);
        imageRecords[1] = imageRecords[1] with { IsMarkedForDeletion = true };
        var home = CreateHome(testVault, imageRecords);
        SetField(home, "filter", new GalleryFilter { Search = imageRecords[0].Name });
        var expectedScenario = ExpectedScenario.CreateFrom("Selecting Trash replaces the browsing filter with marked images.",
            new { Deletion = "marked", ImageIds = new[] { imageRecords[1].Id } });

        // Act
        await (Task)typeof(Home).GetMethod("SetActiveToolAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, ["trash"])!;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            Deletion = GetField<GalleryFilter>(home, "filter").Deletion,
            ImageIds = GetProperty<IReadOnlyList<ImageRecord>>(home, "Filtered").Select(imageRecord => imageRecord.Id).ToArray()
        });
    }

    [Fact]
    public async Task TrashTool_WhenBrowsingResumes_ThenRestoresTheBrowsingFilter()
    {
        // Arrange
        using var testVault = new TestVault();
        var imageRecords = CreateImages(2);
        imageRecords[1] = imageRecords[1] with { IsMarkedForDeletion = true };
        var home = CreateHome(testVault, imageRecords);
        var galleryFilter = new GalleryFilter { Search = imageRecords[0].Name };
        SetField(home, "filter", galleryFilter);
        await (Task)Invoke(home, "SetActiveToolAsync", "trash")!;
        var expectedScenario = ExpectedScenario.CreateFrom("Leaving Trash restores the exact browsing filter and clears its saved copy.",
            new { FilterRestored = true, HasSavedBrowsingFilter = false });

        // Act
        await (Task)typeof(Home).GetMethod("SetActiveToolAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, ["views"])!;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            FilterRestored = galleryFilter == GetField<GalleryFilter>(home, "filter"),
            HasSavedBrowsingFilter = GetField<GalleryFilter?>(home, "browsingFilter") is not null
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

    private static Home CreateHome(TestVault testVault, List<ImageRecord> imageRecords)
    {
        var home = new Home();
        typeof(Home).GetProperty("Gallery", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, testVault.Service);
        typeof(Home).GetProperty("JS", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, new NoOpJsRuntime());
        SetField(home, "images", imageRecords);
        SetField(home, "imagesById", imageRecords.ToDictionary(imageRecord => imageRecord.Id));
        return home;
    }

    private static void SetField(Home home, string name, object value) =>
        typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, value);

    private static T GetField<T>(Home home, string name) =>
        (T)typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(home)!;

    private static T GetProperty<T>(Home home, string name) =>
        (T)typeof(Home).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(home)!;

    private static object? Invoke(Home home, string name, params object?[] arguments) =>
        typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, arguments);

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
