using Gallery.Core.Features.Formatters;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

public static class GalleryDisplay
{
    public static string FormatModelFilename(string model) => GalleryDisplayFormatter.FormatModelFilename(model);

    public static string FormatDuration(ImageRecord image) => GalleryDisplayFormatter.FormatDuration(image);

    public static string FormatBytes(long bytes) => GalleryDisplayFormatter.FormatBytes(bytes);
}
