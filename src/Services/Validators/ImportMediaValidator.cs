using Gallery.Managers.Gateways;

namespace Gallery.Services.Validators;

internal static class ImportMediaValidator
{
    public static void ValidateImageDimensions(int width, int height)
    {
        if ((long)width * height > 40_000_000 || width > 16_384 || height > 16_384)
            throw new InvalidOperationException(
                $"Image dimensions {width} x {height} exceed the 40 megapixel / 16384 pixel dimension limit.");
    }

    public static void ValidateImageFrames(int frameCount)
    {
        if (frameCount != 1) throw new InvalidOperationException("Animated images are not supported.");
    }

    public static void ValidateVideo(VideoTools.ProbeResult probe)
    {
        if (!probe.FormatName.Split(',').Any(format => format is "mp4" or "mov"))
            throw new InvalidOperationException("File is not a valid MP4 video.");
        if (probe.Width <= 0 || probe.Height <= 0) throw new InvalidOperationException("No video stream was found.");
        if (probe.Width > 16_384 || probe.Height > 16_384)
            throw new InvalidOperationException("Video exceeds the 16384 pixel dimension limit.");
    }
}
