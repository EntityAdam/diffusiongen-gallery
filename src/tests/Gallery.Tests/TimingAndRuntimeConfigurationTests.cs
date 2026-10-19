using System.Collections.Immutable;
using System.Diagnostics;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class TimingAndRuntimeConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GalleryCreation_WhenRuntimeToolPathsAreQuoted_ThenImportsThroughNormalizedTools(bool configuredPathIsSupplied)
    {
        // Arrange
        using var fixture = new TestVault();
        using var processTransport = new MediaProcessTransport(MediaAndComfyTests.MakeProbeMetadata().ToJsonString());
        var toolDirectory = Path.GetDirectoryName(processTransport.VideoTools.FindTool("ffmpeg"))!;
        var quotedPath = "  \"" + toolDirectory + "\"  ";
        var options = new GalleryRuntimeOptions
        {
            StateDirectory = Path.Combine(fixture.Root, "application-state"),
            FFmpegPath = configuredPathIsSupplied ? quotedPath : " ",
            ToolSearchPaths = [" ", quotedPath]
        };
        using var service = GalleryService.Create(fixture.Root, options, new HttpClient());
        await service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);

        // Act
        var importedImage = await service.ImportAsync(new MemoryStream([1, 2, 3]), "configured-video.mp4", "source");

        // Assert
        Assert.Equal((1280, 720, 3d, "video/mp4"),
            (importedImage.Width, importedImage.Height, importedImage.DurationSeconds, importedImage.ContentType));
    }

    [Fact]
    public async Task FolderTiming_WhenOneObserverReceivesTwoRuns_ThenEachReportContainsOnlyItsOwnMeasurements()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var firstFolder = Path.Combine(fixture.Root, "first-source");
        var secondFolder = Path.Combine(fixture.Root, "second-source");
        Directory.CreateDirectory(firstFolder);
        Directory.CreateDirectory(secondFolder);
        await File.WriteAllBytesAsync(Path.Combine(firstFolder, "first.png"), GalleryTests.MakePng(24, 16));
        await File.WriteAllBytesAsync(Path.Combine(firstFolder, "second.png"), GalleryTests.MakePng(30, 18));
        var secondRunBytes = GalleryTests.MakePng(36, 20);
        await File.WriteAllBytesAsync(Path.Combine(secondFolder, "third.png"), secondRunBytes);
        var actualReports = new List<IngestionTimingReport>();
        Action<IngestionTimingReport> observeReport = actualReports.Add;

        // Act
        await fixture.Service.ImportFolderAsync(firstFolder, FolderSearchScope.SelectedFolder, reportTimings: observeReport);
        await fixture.Service.ImportFolderAsync(secondFolder, FolderSearchScope.SelectedFolder, reportTimings: observeReport);

        // Assert
        Assert.True(actualReports.Count == 2, $"Expected 2 completed timing reports; Actual: {actualReports.Count} reports.");
        var firstReport = actualReports[0];
        var secondReport = actualReports[1];
        Assert.Equal((2, 2, 0), (firstReport.FilesFound, firstReport.Imported, firstReport.Failed));
        Assert.Equal((1, 1, 0), (secondReport.FilesFound, secondReport.Imported, secondReport.Failed));
        Assert.Equal(secondRunBytes.LongLength, secondReport.SourceBytes);
        Assert.Equal(2, firstReport.Stages.Single(stage => stage.Stage == IngestionStage.File).Count);
        Assert.Equal(1, secondReport.Stages.Single(stage => stage.Stage == IngestionStage.File).Count);
        Assert.DoesNotContain(firstFolder, firstReport.ToString());
        Assert.DoesNotContain(secondFolder, secondReport.ToString());
    }

}

[Trait("Category", "class_test")]
public sealed class CompletedIngestionMeasurementTests
{
    [Fact]
    public void TimingReport_WhenGivenACompletedMeasurement_ThenUsesOnlyTheExplicitSnapshot()
    {
        // Arrange
        var start = new IngestionMeasurementStart(0, "source-drive", "gallery-drive", "catalog-drive", 8);
        var observations = ImmutableArray.Create(
            new IngestionTimingObservation(Stage: IngestionStage.Decode, ElapsedMs: 10),
            new IngestionTimingObservation(Stage: IngestionStage.Decode, ElapsedMs: 30),
            new IngestionTimingObservation(SourceBytes: 100),
            new IngestionTimingObservation(SourceBytes: 200),
            new IngestionTimingObservation(FilesFound: 2, Workers: 2),
            new IngestionTimingObservation(Imported: 2, Failed: 0));
        var completedMeasurement = new CompletedIngestionMeasurement(start, Stopwatch.Frequency, observations);
        var expectedStage = new IngestionStageTiming(IngestionStage.Decode, 2, 40, 20, 10, 30, 30);

        // Act
        var actualReport = IngestionTimingReports.CreateReport(completedMeasurement);

        // Assert
        Assert.Equal(("source-drive", "gallery-drive", "catalog-drive", 8, 2, 2, 2, 0, 300L, 1000d),
            (actualReport.SourceDrive, actualReport.GalleryDrive, actualReport.CatalogDrive, actualReport.ProcessorCount,
                actualReport.Workers, actualReport.FilesFound, actualReport.Imported, actualReport.Failed,
                actualReport.SourceBytes, actualReport.WallMs));
        Assert.Equal(new[] { expectedStage }, actualReport.Stages);
    }
}
