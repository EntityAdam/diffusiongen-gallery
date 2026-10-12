using Gallery.Services.ConfigurationProviders;
using Microsoft.Extensions.Configuration;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class ConfigurationTests
{
    // Optional tool locations normalize configured, empty and absent values consistently.
    [Theory]
    [InlineData("  \"/configured/tools\"  ", "/configured/tools")]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void VideoSettings_WhenToolPathIsOptional_ThenReturnsNormalizedTypedOptions(string? configuredPath, string? expectedPath)
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:FFmpegPath"] = configuredPath,
            ["PATH"] = ""
        }).Build();
        var configurationProvider = new Gallery.Services.ConfigurationProviders.ConfigurationProvider(configuration);

        // Act
        var videoToolsOptions = configurationProvider.GetVideoToolsOptions();

        // Assert
        Assert.Equal(expectedPath, videoToolsOptions.ConfiguredPath);
    }

    [Fact]
    public void VideoSettings_WhenSearchPathIsConfigured_ThenPreservesSourcePrecedenceAndEntryOrder()
    {
        // Arrange
        string[] expectedPaths = [Path.Combine(Path.GetTempPath(), "first-tools"), Path.Combine(Path.GetTempPath(), "second-tools")];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PATH"] = string.Join(Path.PathSeparator, expectedPaths)
        }).Build();
        var configurationProvider = new Gallery.Services.ConfigurationProviders.ConfigurationProvider(configuration);

        // Act
        var videoToolsOptions = configurationProvider.GetVideoToolsOptions();

        // Assert
        Assert.Equal(expectedPaths, videoToolsOptions.SearchPaths.Take(expectedPaths.Length));
    }

    [Fact]
    public void VideoSettings_WhenSearchPathHasNoOverride_ThenUsesTheEnvironmentAtTheSourceBoundary()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        string[] expectedPaths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        var configurationProvider = new Gallery.Services.ConfigurationProviders.ConfigurationProvider(configuration);

        // Act
        var videoToolsOptions = configurationProvider.GetVideoToolsOptions();

        // Assert
        Assert.Equal(expectedPaths, videoToolsOptions.SearchPaths.Take(expectedPaths.Length));
    }
}
