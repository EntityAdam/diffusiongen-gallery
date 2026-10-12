using Gallery.Models;

namespace Gallery.Core.Processors;

internal static class LibraryRules
{
    public static string MergeTags(string existing, string additions) =>
        string.Join(", ", (existing + "," + additions).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public static ImageRecordEdit SetOrganization(ImageRecordEdit imageRecordEdit, int rating, bool imageIsReviewed, string? collection)
    {
        var collections = imageRecordEdit.Collections;
        if (collection is not null && !collections.Contains(collection.Trim(), StringComparer.OrdinalIgnoreCase))
            collections = collections.Add(collection.Trim());
        return imageRecordEdit with { Rating = rating, Reviewed = imageIsReviewed, Collections = collections };
    }

    public static ImageRecordEdit SetCuration(ImageRecordEdit imageRecordEdit, int? rating, bool? imageIsReviewed) =>
        imageRecordEdit with { Rating = rating ?? imageRecordEdit.Rating,
            Reviewed = (rating is > 0) || (imageIsReviewed ?? imageRecordEdit.Reviewed) };

    public static ImageRecordEdit SetDeletionMark(ImageRecordEdit imageRecordEdit, bool markedForDeletion) =>
        imageRecordEdit with
        {
            MarkedForDeletion = markedForDeletion,
            Reviewed = markedForDeletion || imageRecordEdit.Reviewed
        };
}
