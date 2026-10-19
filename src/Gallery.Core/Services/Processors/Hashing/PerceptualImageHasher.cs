using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Core.Services.Processors.Hashing;

internal static class PerceptualImageHasher
{
    private const int DifferenceHashDimension = 8;

    public static ulong DifferenceHash(ReadOnlySpan<byte> imageBytes)
    {
        using var image = LoadNormalizedImage(imageBytes);
        return ComputeDifferenceHash(image);
    }

    private static Image<L8> LoadNormalizedImage(ReadOnlySpan<byte> imageBytes)
    {
        var image = Image.Load<L8>(imageBytes);
        try
        {
            image.Mutate(imageProcessingContext => imageProcessingContext.Resize(new ResizeOptions { Size = new Size(DifferenceHashDimension + 1, DifferenceHashDimension), Mode = ResizeMode.Stretch }));
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static ulong ComputeDifferenceHash(Image<L8> image)
    {
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < DifferenceHashDimension; y++)
            for (var x = 0; x < DifferenceHashDimension; x++, bit++)
                if (image[x, y].PackedValue > image[x + 1, y].PackedValue) hash |= 1UL << bit;
        return hash;
    }

}
