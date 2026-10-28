using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Gateways;

namespace Gallery.Core.Services.ConfigurationProviders.Sources;

internal sealed class SettingsSource(IConfiguration configuration)
{
    private const string FFmpegPathSettingName = "Gallery:FFmpegPath";
    private const string PathEnvironmentVariableName = "PATH";

    public GalleryRuntimeOptions ReadGalleryRuntimeOptions() => new()
    {
        StateDirectory = configuration["Gallery:StateDirectory"],
        FFmpegPath = configuration[FFmpegPathSettingName],
        ToolSearchPaths = DiscoverSearchPaths().ToImmutableArray()
    };

    public VideoToolsOptions ReadVideoToolsOptions() =>
        VideoToolsOptionsNormalizer.Normalize(configuration[FFmpegPathSettingName], DiscoverSearchPaths());

    private IEnumerable<string> DiscoverSearchPaths() =>
        ReadSearchPathSources().SelectMany(ParseSearchPaths);

    private IEnumerable<string?> ReadSearchPathSources()
    {
        yield return configuration[PathEnvironmentVariableName] ?? Environment.GetEnvironmentVariable(PathEnvironmentVariableName);
        if (!OperatingSystem.IsWindows()) yield break;
        yield return Environment.GetEnvironmentVariable(PathEnvironmentVariableName, EnvironmentVariableTarget.User);
        yield return Environment.GetEnvironmentVariable(PathEnvironmentVariableName, EnvironmentVariableTarget.Machine);
    }

    private static IEnumerable<string> ParseSearchPaths(string? searchPathValue) =>
        string.IsNullOrWhiteSpace(searchPathValue) ? [] : searchPathValue.Split(Path.PathSeparator,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
