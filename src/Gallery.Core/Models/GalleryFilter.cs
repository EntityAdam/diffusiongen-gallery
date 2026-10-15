namespace Gallery.Core.Models;

public sealed record GalleryFilter
{
    public const string AnyValue = "all";
    public string Search { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Model { get; init; } = "";
    public bool FavoritesOnly { get; init; }
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
    public bool UnorganizedOnly { get; init; }
    public string Media { get; init; } = AnyValue;
    public string Prompt { get; init; } = "";
}
