using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record GalleryRuntimeOptions
{
    public CatalogBackend CatalogBackend { get; init; } = CatalogBackend.Sqlite;
    public string? StateDirectory { get; init; }
    public string? FFmpegPath { get; init; }
    public ImmutableArray<string> ToolSearchPaths { get; init; } = [];
}
