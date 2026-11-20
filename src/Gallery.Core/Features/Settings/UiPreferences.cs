using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record UiPreferences
{
    public const string DetailDensity = "detail";
    public static readonly ImmutableArray<string> Densities = ["compact", DetailDensity];
    public const int DefaultAutoLockMinutes = 15;
    public string GridDensity { get; init; } = DetailDensity;
    public const int DefaultThumbnailScale = 100;
    public const int MinThumbnailScale = 65;
    public const int MaxThumbnailScale = 160;
    public int ThumbnailScale { get; init; } = DefaultThumbnailScale;
    public ImmutableDictionary<string, bool> ExperimentalFeatures { get; init; } = ImmutableDictionary<string, bool>.Empty;
    public int AutoLockMinutes { get; init; } = DefaultAutoLockMinutes;
    public static readonly ImmutableArray<string> ActiveTools = ["views", "filters", "review", "organize", "intelligence", "trash"];
    public string ActiveTool { get; init; } = "";
}
