using System.Security.Cryptography;
using Gallery.Core.Features.Ingestion;
using Gallery.Core.Models;
using Gallery.Web.Components.Features.Ingestion;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class IngestionSessionTests
{
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
}
