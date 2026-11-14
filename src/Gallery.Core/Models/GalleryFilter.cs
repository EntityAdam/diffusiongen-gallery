namespace Gallery.Core.Models;

public sealed record GalleryFilter
{
    public const string AnyValue = "all";
    public const string AwaitingApproval = "awaiting-approval";
    public string Search { get; init; } = "";
    public VisionQuery? Vision { get; init; }
    public string Folder { get; init; } = "";
    // Comma-separated exact tags; every selected tag must match. Keeps existing saved views compatible.
    public string Tag { get; init; } = "";
    public string Model { get; init; } = "";
    public string DiffusionModel { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("FavoritesOnly")]
    public bool IncludesFavoritesOnly { get; init; }
    public string Deletion { get; init; } = "active";
    public string Analysis { get; init; } = AnyValue;
    public string Orientation { get; init; } = AnyValue;
    public int MinWidth { get; init; }
    public int MinHeight { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string Sort { get; init; } = "newest";
    public string Collection { get; init; } = "";
    public int MinRating { get; init; }
    public string Review { get; init; } = AnyValue;
    [System.Text.Json.Serialization.JsonPropertyName("UnorganizedOnly")]
    public bool IncludesUnorganizedOnly { get; init; }
    public string Media { get; init; } = AnyValue;
    public string Prompt { get; init; } = "";

    public GalleryFilter WithRenamedTag(string original, string replacement)
    {
        var tags = Tag.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return tags.Contains(original, StringComparer.OrdinalIgnoreCase)
            ? this with { Tag = string.Join(", ", tags.Select(tag => tag.Equals(original, StringComparison.OrdinalIgnoreCase) ? replacement : tag)
                .Distinct(StringComparer.OrdinalIgnoreCase)) }
            : this;
    }
}
