using Gallery.Web.Models;

namespace Gallery.Web.Services.Processors;

internal static class GalleryDisplayFormatter
{
    public static string FormatDuration(ImageRecord image) => RenderDuration(NormalizeDuration(image.DurationSeconds));

    private static TimeSpan NormalizeDuration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));

    private static string RenderDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    public static string FormatBytes(long bytes) => RenderBytes(bytes, ScaleBytes(bytes));

    private static (double Value, string Unit) ScaleBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double scaledBytes = Math.Max(0, bytes);
        var unit = 0;
        while (scaledBytes >= 1024 && unit < units.Length - 1)
        {
            scaledBytes /= 1024;
            unit++;
        }
        return (scaledBytes, units[unit]);
    }

    private static string RenderBytes(long bytes, (double Value, string Unit) scaled) =>
        scaled.Unit == "B"
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes:N0} B")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{scaled.Value:0.##} {scaled.Unit}");
}
