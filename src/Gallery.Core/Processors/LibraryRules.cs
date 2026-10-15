using Gallery.Core.Models;

namespace Gallery.Core.Processors;

public static class LibraryRules
{
    public static ImageRecord ApplyEdit(ImageRecord original, ImageRecordEdit edit)
    {
        var updated = edit.ToRecord(original);
        return (updated.Rating > 0 && updated.Rating != original.Rating)
            || (updated.MarkedForDeletion && !original.MarkedForDeletion)
            ? updated with { Reviewed = true }
            : updated;
    }

    public static string MergeTags(string existing, string additions) =>
        string.Join(", ", (existing + "," + additions).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public static ImageRecordEdit SetOrganization(ImageRecordEdit imageRecordEdit, int rating, bool imageIsReviewed, string? collection)
    {
        var organized = collection is null ? imageRecordEdit : AddCollection(imageRecordEdit, collection);
        return organized with { Rating = rating, Reviewed = imageIsReviewed };
    }

    public static ImageRecordEdit AddCollection(ImageRecordEdit image, string collection) =>
        image.Collections.Contains(collection.Trim(), StringComparer.OrdinalIgnoreCase)
            ? image
            : image with { Collections = image.Collections.Add(collection.Trim()) };

    public static ImageRecordEdit RemoveCollection(ImageRecordEdit image, string collection) =>
        image with
        {
            Collections = image.Collections.RemoveAll(name =>
                name.Equals(collection.Trim(), StringComparison.OrdinalIgnoreCase))
        };

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
