using SixLabors.ImageSharp.Formats;
using Gallery.Core.Services.Gateways;

namespace Gallery.Core.Services.Validators;

internal static class ImportMediaValidator
{
    private const int MaxMediaDimensionPixels = 16_384;
    private const long MaxImagePixels = 40_000_000;
    public static string RequireSupportedContentType(IImageFormat? imageFormat) =>
        imageFormat?.DefaultMimeType switch
        {
            "image/png" => "image/png",
            "image/jpeg" => "image/jpeg",
            "image/webp" => "image/webp",
            _ => throw new InvalidOperationException($"Image format '{imageFormat?.Name ?? "unknown"}' is not supported. Only PNG, JPEG and WebP images are supported.")
        };


    public static void ValidateImageDimensions(int width, int height)
    {
        if (ExceedsSupportedImageDimensions(width, height))
            throw new InvalidOperationException(
                $"Image dimensions {width} x {height} exceed the 40 megapixel / 16384 pixel dimension limit.");
    }

    private static bool ExceedsSupportedImageDimensions(int width, int height) =>
        (long)width * height > MaxImagePixels || width > MaxMediaDimensionPixels || height > MaxMediaDimensionPixels;

    public static void ValidateImageFrames(int frameCount)
    {
        if (frameCount != 1) throw new InvalidOperationException("Animated images are not supported.");
    }

    public static void ValidateVideo(VideoTools.ProbeResult probeResult)
    {
        if (!probeResult.FormatName.Split(',').Any(format => format is "mp4" or "mov"))
            throw new InvalidOperationException("File is not a valid MP4 video.");
        if (!HasVideoStream(probeResult.Width, probeResult.Height)) throw new InvalidOperationException("No video stream was found.");
        if (ExceedsSupportedVideoDimensions(probeResult.Width, probeResult.Height))
            throw new InvalidOperationException("Video exceeds the 16384 pixel dimension limit.");
    }

    private static bool HasVideoStream(int width, int height) => width > 0 && height > 0;

    private static bool ExceedsSupportedVideoDimensions(int width, int height) =>
        width > MaxMediaDimensionPixels || height > MaxMediaDimensionPixels;
}
