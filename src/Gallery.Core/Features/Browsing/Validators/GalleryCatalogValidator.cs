using Gallery.Core.Models;

namespace Gallery.Core.Features.Browsing.Validators;

internal static class GalleryCatalogValidator
{
    public static void ValidatePageSize(int pageSize)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
    }

    public static void ValidateFilterArguments(IEnumerable<ImageRecord> records, GalleryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(filter);
    }

    public static void ValidateRecords(IEnumerable<ImageRecord> records) =>
        ArgumentNullException.ThrowIfNull(records);
}
