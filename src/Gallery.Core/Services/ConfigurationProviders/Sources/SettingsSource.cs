using Microsoft.Extensions.Configuration;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Gateways;

namespace Gallery.Core.Services.ConfigurationProviders.Sources;

internal sealed class SettingsSource(IConfiguration configuration)
{
    public VideoToolsOptions ReadVideoToolsOptions() =>
        VideoToolsOptionsNormalizer.Normalize(configuration["Gallery:FFmpegPath"], DiscoverSearchPaths());

    private IEnumerable<string> DiscoverSearchPaths() =>
        ReadSearchPathSources().SelectMany(ParseSearchPaths);

    private IEnumerable<string?> ReadSearchPathSources()
    {
        yield return configuration["PATH"] ?? Environment.GetEnvironmentVariable("PATH");
        if (!OperatingSystem.IsWindows()) yield break;
        yield return Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        yield return Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
    }

    private static IEnumerable<string> ParseSearchPaths(string? searchPathValue) =>
        string.IsNullOrWhiteSpace(searchPathValue) ? [] : searchPathValue.Split(Path.PathSeparator,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
