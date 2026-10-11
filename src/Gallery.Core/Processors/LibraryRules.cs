using Gallery.Models;

namespace Gallery.Core;

internal static class LibraryRules
{
    public static string MergeTags(string existing, string additions) =>
        string.Join(", ", (existing + "," + additions).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public static void SetOrganization(ImageRecordEdit image, int rating, bool reviewed, string? collection)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.Rating = rating;
        image.Reviewed = reviewed;
        if (collection is not null && !image.Collections.Contains(collection.Trim(), StringComparer.OrdinalIgnoreCase))
            image.Collections.Add(collection.Trim());
    }

    public static void SetCuration(ImageRecordEdit image, int? rating, bool? reviewed)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (rating is not null) image.Rating = rating.Value;
        if (reviewed is not null) image.Reviewed = reviewed.Value;
    }
}
