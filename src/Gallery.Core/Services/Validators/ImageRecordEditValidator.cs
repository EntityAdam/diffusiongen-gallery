using Gallery.Core.Models;

namespace Gallery.Core.Services.Validators;

internal static class ImageRecordEditValidator
{
    public static void Validate(ImageRecordEdit? imageRecordEdit, string expectedId)
    {
        if (imageRecordEdit is null)
            throw new InvalidOperationException("Media edit 'null' is invalid; a media update must return an edit.");
        if (imageRecordEdit.Id != expectedId)
            throw new InvalidOperationException($"Media identity '{imageRecordEdit.Id}' is invalid; a media update must preserve the expected identity '{expectedId}'.");
        ValidateRating(imageRecordEdit.Rating);
        if (imageRecordEdit.Description is null)
            throw new InvalidOperationException("Media description 'null' is invalid; provide a non-null description.");
        if (imageRecordEdit.UserDescription is null)
            throw new InvalidOperationException("Media user description 'null' is invalid; provide a non-null user description.");
        if (imageRecordEdit.Tags is null)
            throw new InvalidOperationException("Media tags 'null' is invalid; provide non-null tags.");
        if (imageRecordEdit.AnalysisModel is null)
            throw new InvalidOperationException("Media analysis model 'null' is invalid; provide a non-null model.");
        if (imageRecordEdit.Collections.IsDefault)
            throw new InvalidOperationException("Media collections must be initialized.");
        var collectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in imageRecordEdit.Collections)
        {
            if (IsInvalidCollectionName(name))
                throw new InvalidOperationException($"Collection name '{name ?? "null"}' is invalid; collection names must have 1 to 80 characters.");
            if (name != name.Trim())
                throw new InvalidOperationException($"Collection name '{name}' is invalid; remove leading or trailing whitespace.");
            if (!collectionNames.Add(name))
                throw new InvalidOperationException($"Collection name '{name}' is duplicated; collection names must be unique regardless of case.");
        }
    }

    public static void ValidateRating(int? rating)
    {
        if (rating is < 0 or > 5)
            throw new InvalidOperationException(string.Format(
                System.Globalization.CultureInfo.InvariantCulture, "Rating {0} must be between 0 and 5.", rating));
    }

    public static bool IsInvalidCollectionName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80;
}
