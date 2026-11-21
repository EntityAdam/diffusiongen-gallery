using System.Collections.Immutable;

using Gallery.Core.Features.Ingestion.Normalizers.SettingsModels;

namespace Gallery.Core.Features.Ingestion.Normalizers;

internal static class VideoToolsOptionsNormalizer
{
    public static VideoToolsOptions Normalize(string? configuredPath, IEnumerable<string> searchPaths) =>
        new(string.IsNullOrWhiteSpace(configuredPath) ? null : NormalizePath(configuredPath),
            searchPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).ToImmutableArray());

    private static string NormalizePath(string path) => path.Trim().Trim('"');
}
