namespace Gallery.Models;

public sealed record ImageRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string OriginalName { get; init; } = "";
    public string StoredPath { get; set; } = "";
    public string SourceFolder { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Width { get; init; }
    public int Height { get; init; }
    public long OriginalBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public string PngSha256 { get; init; } = "";
    public string Metadata { get; init; } = "";
    public string Description { get; set; } = "";
    public string Tags { get; set; } = "";
    public string AnalysisModel { get; set; } = "";
    public bool Favorite { get; set; }
    public bool MarkedForDeletion { get; set; }
    public List<string> Collections { get; set; } = [];
    public int Rating { get; set; }
    public bool Reviewed { get; set; }
}

public enum ExistingOriginalAction { Retain, Delete }
public enum MissingOriginalAction { RestorePng, Discard }
public sealed record RemovalItem(string Id, string Name, string OriginalPath, bool OriginalExists);
public sealed record RemovalResult(string Id, bool Removed, string? RestoredPath, string? Error);

public sealed record ImportProgress(int? Total, int Completed, int Imported, int Failed, string Status);

public sealed record StorageOverview(
    long DatabaseBytes,
    int ImageFileCount,
    long ImageBytes,
    int VaultFileCount,
    long VaultFileBytes,
    int MissingImageFiles,
    DateTimeOffset UpdatedAt)
{
    public int TotalFileCount => ImageFileCount + VaultFileCount;
    public long TotalBytes => ImageBytes + VaultFileBytes;

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes:N0} B")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }
}

public sealed class GalleryFilter
{
    public string Search { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Model { get; set; } = "";
    public bool FavoritesOnly { get; set; }
    public string Deletion { get; set; } = "active";
    public string Analysis { get; set; } = "all";
    public string Orientation { get; set; } = "all";
    public int MinWidth { get; set; }
    public int MinHeight { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string Sort { get; set; } = "newest";
    public string Collection { get; set; } = "";
    public int MinRating { get; set; }
    public string Review { get; set; } = "all";
    public bool UnorganizedOnly { get; set; }

    public IEnumerable<ImageRecord> Apply(IEnumerable<ImageRecord> images)
    {
        var result = images.Where(image =>
            (Search.Length == 0 || $"{image.Name} {image.OriginalName} {image.Metadata} {image.Description} {image.Tags}"
                .Contains(Search, StringComparison.OrdinalIgnoreCase))
            && (Folder.Length == 0 || image.SourceFolder.Contains(Folder, StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(image.StoredPath)!.Contains(Folder, StringComparison.OrdinalIgnoreCase))
            && (Tag.Length == 0 || image.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(Tag, StringComparer.OrdinalIgnoreCase))
            && (Model.Length == 0 || image.AnalysisModel.Contains(Model, StringComparison.OrdinalIgnoreCase))
            && (!FavoritesOnly || image.Favorite)
            && (Collection.Length == 0 || image.Collections.Contains(Collection, StringComparer.OrdinalIgnoreCase))
            && image.Rating >= MinRating
            && (Review == "all" || image.Reviewed == (Review == "reviewed"))
            && (!UnorganizedOnly || image.Collections.Count == 0)
            && (Deletion == "all" || image.MarkedForDeletion == (Deletion == "marked"))
            && (Analysis == "all" || (image.Description.Length > 0) == (Analysis == "analyzed"))
            && (Orientation == "all" || Orientation == "square" && image.Width == image.Height
                || Orientation == "landscape" && image.Width > image.Height
                || Orientation == "portrait" && image.Height > image.Width)
            && image.Width >= MinWidth && image.Height >= MinHeight
            && (From is null || image.ImportedAt.LocalDateTime.Date >= From.Value.Date)
            && (To is null || image.ImportedAt.LocalDateTime.Date <= To.Value.Date));
        return Sort switch
        {
            "oldest" => result.OrderBy(image => image.ImportedAt),
            "name" => result.OrderBy(image => image.Name, StringComparer.OrdinalIgnoreCase),
            "size" => result.OrderByDescending(image => image.OriginalBytes),
            "rating" => result.OrderByDescending(image => image.Rating).ThenByDescending(image => image.ImportedAt),
            _ => result.OrderByDescending(image => image.ImportedAt)
        };
    }
}

public sealed record VisionSettings
{
    public const int MinRequestTimeoutSeconds = 10;
    public const int MaxRequestTimeoutSeconds = 3600;
    public string Endpoint { get; set; } = "http://127.0.0.1:1234/v1/";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int RequestTimeoutSeconds { get; set; } = 180;
}
