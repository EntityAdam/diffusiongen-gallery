using System.Collections.Immutable;

namespace Gallery.Core.Features.Ingestion.Normalizers.SettingsModels;

internal sealed class VideoToolsOptions
{
    internal VideoToolsOptions(string? configuredPath = null, IEnumerable<string>? searchPaths = null)
    {
        ConfiguredPath = configuredPath;
        SearchPaths = searchPaths?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
    }

    internal string? ConfiguredPath { get; }
    internal ImmutableArray<string> SearchPaths { get; }
}
