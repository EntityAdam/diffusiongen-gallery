using Gallery.Models;

namespace Gallery.Core;

public static class LibraryRules
{
    public static string MergeTags(string existing, string additions) =>
        string.Join(", ", (existing + "," + additions).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public static void SetOrganization(ImageRecord image, int rating, bool reviewed, string? collection)
    {
        if (rating is < 0 or > 5) throw new InvalidOperationException("Rating must be between 0 and 5.");
        if (collection is not null && (string.IsNullOrWhiteSpace(collection) || collection.Trim().Length > 80))
            throw new InvalidOperationException("Collection names must have 1 to 80 characters.");
        image.Rating = rating;
        image.Reviewed = reviewed;
        if (collection is not null && !image.Collections.Contains(collection.Trim(), StringComparer.OrdinalIgnoreCase))
            image.Collections.Add(collection.Trim());
    }

    public static void SetCuration(ImageRecord image, int? rating, bool? reviewed)
    {
        if (rating is < 0 or > 5) throw new InvalidOperationException("Rating must be between 0 and 5.");
        if (rating is not null) image.Rating = rating.Value;
        if (reviewed is not null) image.Reviewed = reviewed.Value;
    }
}
