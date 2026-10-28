// Class tests pin raw configuration snapshots and source precedence that cannot be observed through HTTP.
using System.Security.Cryptography;
using Gallery.Core.Services;
using Microsoft.Extensions.Configuration;
using ConfigurationProvider = Gallery.Core.Services.ConfigurationProviders.ConfigurationProvider;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class ConfigurationTests
{
    [Fact]
    public void RuntimeOptions_WhenConfigurationIsNull_ThenIdentifiesTheMissingArgument()
    {
        // Arrange
        const string ExpectedParameterName = "configuration";

        // Act
        var actualArgumentNullException = Assert.Throws<ArgumentNullException>(
            () => GalleryService.CaptureRuntimeOptions(null!));

        // Assert
        Assert.Equal(ExpectedParameterName, actualArgumentNullException.ParamName);
    }

    // Missing and blank state directories retain their exact configured values.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RuntimeOptions_WhenStateDirectoryIsOptional_ThenPreservesTheConfiguredValue(string? expectedStateDirectory)
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:StateDirectory"] = expectedStateDirectory,
            ["PATH"] = ""
        }).Build();
        var expectedScenario = ExpectedScenario.CreateFrom("Missing and blank state-directory settings remain unchanged and do not invent a tool path.",
            new { StateDirectory = expectedStateDirectory, FFmpegPath = (string?)null });

        // Act
        var actualGalleryRuntimeOptions = GalleryService.CaptureRuntimeOptions(configuration);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario,
            new { actualGalleryRuntimeOptions.StateDirectory, actualGalleryRuntimeOptions.FFmpegPath });
    }

    [Fact]
    public void RuntimeOptions_WhenStateDirectoryIsConfigured_ThenPreservesItsExactValue()
    {
        // Arrange
        var expectedStateDirectory = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:StateDirectory"] = expectedStateDirectory,
            ["PATH"] = ""
        }).Build();

        // Act
        var actualGalleryRuntimeOptions = GalleryService.CaptureRuntimeOptions(configuration);

        // Assert
        Assert.Equal(expectedStateDirectory, actualGalleryRuntimeOptions.StateDirectory);
    }

    [Fact]
    public void RuntimeOptions_WhenSourcesAreLayered_ThenCapturesOverridesAndPreservesRawToolPaths()
    {
        // Arrange
        const string StateDirectorySettingName = "Gallery:StateDirectory";
        const string FFmpegPathSettingName = "Gallery:FFmpegPath";
        const string PathSettingName = "PATH";
        var expectedStateDirectory = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expectedFFmpegPath = $"  \" {Convert.ToHexString(RandomNumberGenerator.GetBytes(16))} \"  ";
        string[] expectedSearchPaths =
        [
            $"\" {Convert.ToHexString(RandomNumberGenerator.GetBytes(16))} \"",
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
        ];
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [StateDirectorySettingName] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                [FFmpegPathSettingName] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                [PathSettingName] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [StateDirectorySettingName] = expectedStateDirectory,
                [FFmpegPathSettingName] = expectedFFmpegPath,
                [PathSettingName] = string.Join(Path.PathSeparator, " ", $"  {expectedSearchPaths[0]}  ", expectedSearchPaths[1])
            }).Build();
        var expectedScenario = ExpectedScenario.CreateFrom("Later configuration sources override earlier ones while runtime capture retains raw tool paths.", new
        {
            StateDirectory = expectedStateDirectory,
            FFmpegPath = expectedFFmpegPath,
            SearchPaths = expectedSearchPaths
        });

        // Act
        var actualGalleryRuntimeOptions = GalleryService.CaptureRuntimeOptions(configuration);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            actualGalleryRuntimeOptions.StateDirectory,
            actualGalleryRuntimeOptions.FFmpegPath,
            SearchPaths = actualGalleryRuntimeOptions.ToolSearchPaths.Take(expectedSearchPaths.Length).ToArray()
        });
    }

    // Changing any source setting after capture cannot change the captured options.
    [Theory]
    [InlineData("Gallery:StateDirectory")]
    [InlineData("Gallery:FFmpegPath")]
    [InlineData("PATH")]
    public void RuntimeOptions_WhenConfigurationChanges_ThenRetainsTheCapturedValues(string settingName)
    {
        // Arrange
        var expectedStateDirectory = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expectedFFmpegPath = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expectedSearchPath = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var replacementSetting = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:StateDirectory"] = expectedStateDirectory,
            ["Gallery:FFmpegPath"] = expectedFFmpegPath,
            ["PATH"] = expectedSearchPath
        }).Build();
        var expectedScenario = ExpectedScenario.CreateFrom("Captured runtime options are immutable snapshots of the original source settings.", new
        {
            StateDirectory = expectedStateDirectory,
            FFmpegPath = expectedFFmpegPath,
            SearchPath = expectedSearchPath
        });

        // Act
        var actualGalleryRuntimeOptions = GalleryService.CaptureRuntimeOptions(configuration);

        // Post-capture stimulus
        configuration[settingName] = replacementSetting;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            actualGalleryRuntimeOptions.StateDirectory,
            actualGalleryRuntimeOptions.FFmpegPath,
            SearchPath = actualGalleryRuntimeOptions.ToolSearchPaths[0]
        });
    }

    // Blank and absent tool locations both normalize to an absent value.
    [Theory]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void VideoSettings_WhenToolPathIsBlankOrMissing_ThenNormalizesTheConfiguredPathToNull(string? configuredPath, string? expectedPath)
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:FFmpegPath"] = configuredPath,
            ["PATH"] = ""
        }).Build();
        var configurationProvider = new ConfigurationProvider(configuration);

        // Act
        var actualVideoToolsOptions = configurationProvider.GetVideoToolsOptions();

        // Assert
        Assert.Equal(expectedPath, actualVideoToolsOptions.ConfiguredPath);
    }

    [Fact]
    public void VideoSettings_WhenToolPathHasWhitespaceAndQuotes_ThenRemovesOnlyTheOuterPadding()
    {
        // Arrange
        var expectedPath = Path.Combine(Path.GetTempPath(), "tool directory " + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:FFmpegPath"] = $"  \"{expectedPath}\"  ",
            ["PATH"] = ""
        }).Build();
        var configurationProvider = new ConfigurationProvider(configuration);

        // Act
        var actualVideoToolsOptions = configurationProvider.GetVideoToolsOptions();

        // Assert
        Assert.Equal(expectedPath, actualVideoToolsOptions.ConfiguredPath);
    }

    [Fact]
    public void VideoSettings_WhenSearchPathIsConfigured_ThenPreservesSourcePrecedenceAndEntryOrder()
    {
        // Arrange
        string[] expectedPaths =
        [
            Path.Combine(Path.GetTempPath(), Convert.ToHexString(RandomNumberGenerator.GetBytes(16))),
            Path.Combine(Path.GetTempPath(), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))
        ];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PATH"] = string.Join(Path.PathSeparator, expectedPaths)
        }).Build();
        var configurationProvider = new ConfigurationProvider(configuration);

        // Act
        var actualVideoToolsOptions = configurationProvider.GetVideoToolsOptions();

        // Assert
        Assert.Equal(expectedPaths, actualVideoToolsOptions.SearchPaths.Take(expectedPaths.Length));
    }

    [Fact]
    public void VideoSettings_WhenSearchPathHasNoOverride_ThenUsesTheEnvironmentAtTheSourceBoundary()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        string[] expectedPaths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim('"'))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        var configurationProvider = new ConfigurationProvider(configuration);

        // Act
        var actualVideoToolsOptions = configurationProvider.GetVideoToolsOptions();

        // Assert
        Assert.Equal(expectedPaths, actualVideoToolsOptions.SearchPaths.Take(expectedPaths.Length));
    }
}
