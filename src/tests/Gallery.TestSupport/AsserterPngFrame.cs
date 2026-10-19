using SixLabors.ImageSharp;
using Xunit;

namespace Gallery.Tests;

internal sealed record ExpectedPngFrame(int Width, int Height);

internal static class AsserterPngFrame
{
    public static void AssertMatches(ExpectedPngFrame expected, string actualDataUri)
    {
        const string prefix = "data:image/png;base64,";
        Assert.True(actualDataUri.StartsWith(prefix, StringComparison.Ordinal),
            $"Expected a PNG data URI beginning '{prefix}'; Actual: '{actualDataUri}'.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(actualDataUri[prefix.Length..]); }
        catch (FormatException exception)
        {
            Assert.Fail($"Expected a valid base64 PNG payload; Actual: {exception.Message}.");
            return;
        }
        Assert.True(bytes.Length > 0, $"Expected a nonempty PNG payload; Actual: {bytes.Length} bytes.");
        try
        {
            using var frame = Image.Load(bytes);
            var discrepancies = new List<string>();
            if (frame.Metadata.DecodedImageFormat?.DefaultMimeType != "image/png")
                discrepancies.Add($"Format: Expected: image/png; Actual: {frame.Metadata.DecodedImageFormat?.DefaultMimeType}.");
            if (frame.Width != expected.Width)
                discrepancies.Add($"Width: Expected: {expected.Width}; Actual: {frame.Width}.");
            if (frame.Height != expected.Height)
                discrepancies.Add($"Height: Expected: {expected.Height}; Actual: {frame.Height}.");
            Assert.True(discrepancies.Count == 0, "PNG FRAME ASSERTION FAILED\n" + string.Join("\n", discrepancies));
        }
        catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException)
        {
            Assert.Fail($"Expected a decodable PNG frame; Actual: {exception.GetType().Name}: {exception.Message}.");
        }
    }
}
