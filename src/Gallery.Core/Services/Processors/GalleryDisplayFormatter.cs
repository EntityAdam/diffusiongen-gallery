using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class GalleryDisplayFormatter
{
    private const string ModelExtension = ".safetensors";

    public static string FormatModelFilename(string model) => RemoveModelExtension(SelectFilename(model));

    private static string SelectFilename(string model) => model.Replace('\\', '/').Split('/')[^1];

    private static string RemoveModelExtension(string filename) =>
        filename.EndsWith(ModelExtension, StringComparison.OrdinalIgnoreCase) ? filename[..^ModelExtension.Length] : filename;

    public static string FormatDuration(ImageRecord imageRecord) => RenderDuration(NormalizeDuration(imageRecord.DurationSeconds));

    private static TimeSpan NormalizeDuration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));

    private static string RenderDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}")
            : duration.ToString(@"m\:ss");

    public static string FormatBytes(long bytes) => RenderBytes(bytes, ScaleBytes(bytes));

    private static (double Value, string Unit) ScaleBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double scaledBytes = Math.Max(0, bytes);
        var unitIndex = 0;
        while (scaledBytes >= 1024 && unitIndex < units.Length - 1)
        {
            scaledBytes /= 1024;
            unitIndex++;
        }
        return (scaledBytes, units[unitIndex]);
    }

    private static string RenderBytes(long bytes, (double Value, string Unit) scaled) =>
        scaled.Unit == "B"
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes:N0} B")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{scaled.Value:0.##} {scaled.Unit}");
}
