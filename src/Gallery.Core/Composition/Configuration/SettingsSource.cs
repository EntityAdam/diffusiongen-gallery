using Gallery.Core.Features.Ingestion.Video;
using Microsoft.Extensions.Configuration;

namespace Gallery.Core.Composition.Configuration;

internal sealed class SettingsSource(IConfiguration configuration)
{
    private const string FFmpegPathSettingName = "Gallery:FFmpegPath";
    private const string PathEnvironmentVariableName = "PATH";

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
