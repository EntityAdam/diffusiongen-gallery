using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;

using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Stress.Core;

public sealed record GeneratedPng(long Bytes, int Width, int Height);

public static class SyntheticPngWriter
{
    private static readonly (int Width, int Height)[] Dimensions = [(1024, 1024), (832, 1216), (1216, 832), (768, 1344), (1344, 768)];

    public static async Task<GeneratedPng> WriteAsync(string path, int seed, long index, SizeDistribution sizes,
        SizeDistribution? metadata = null, CancellationToken cancellationToken = default)
    {
        var random = new Random(ComfyMetadataFaker.Seed(seed, index));
        var target = sizes.Draw(random);
        var dimensions = sizes.Width is int width && sizes.Height is int height ? (width, height) : Dimensions[random.Next(Dimensions.Length)];
        var metadataSize = checked((int)(metadata ?? SizeDistribution.Parse("uniform:8KB-60KB")).Draw(random));
        var documents = ComfyMetadataFaker.Create(seed, index, metadataSize, dimensions.Item1, dimensions.Item2);
        var rawBytes = dimensions.Item1 * (long)dimensions.Item2 * 3;
        if (target > rawBytes + metadataSize + rawBytes / 50) throw new ArgumentException("Target PNG size exceeds the RGB capacity of these dimensions.");
        if (target < metadataSize + 8192) throw new ArgumentException("Target PNG size must leave room for image data beyond its metadata.");
        var fraction = Math.Clamp((target - metadataSize - 20000.0) / rawBytes, 0.001, 1.0);
        using var image = new Image<Rgb24>(dimensions.Item1, dimensions.Item2);
        image.Metadata.GetPngMetadata().TextData.Add(new PngTextData("prompt", documents.Prompt, "", ""));
        image.Metadata.GetPngMetadata().TextData.Add(new PngTextData("workflow", documents.Workflow, "", ""));
        using var encoded = new MemoryStream();
        var encoder = new PngEncoder { CompressionLevel = PngCompressionLevel.Level1, ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Fill(image, ComfyMetadataFaker.Seed(seed, index), fraction);
            encoded.SetLength(0);
            encoded.Position = 0;
            await image.SaveAsync(encoded, encoder, cancellationToken);
            if (Math.Abs(encoded.Length - target) <= target * 0.08) break;
            fraction = Math.Clamp(fraction * (target - metadataSize) / Math.Max(1, encoded.Length - metadataSize), 0.0001, 1.0);
        }
        if (Math.Abs(encoded.Length - target) > target * 0.10)
            throw new InvalidOperationException("Unable to reach the requested PNG size within 10%; change the target or dimensions.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        encoded.Position = 0;
        await encoded.CopyToAsync(file, cancellationToken);
        return new(encoded.Length, image.Width, image.Height);
    }

    private static void Fill(Image<Rgb24> image, int seed, double fraction)
    {
        // A moving band of seeded noise over a low-frequency colour field makes both the full image and thumbnail nontrivial.
        // Its coverage controls compressed size without appending non-image padding to the PNG.
        var random = new Random(seed);
        var noisyWidth = (int)(image.Width * fraction);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = (int)(image.Width * (0.5 + 0.25 * Math.Sin(y / 140.0 + seed % 17)));
                for (var x = 0; x < row.Length; x++)
                {
                    if ((x + offset) % image.Width < noisyWidth)
                        row[x] = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                    else
                        row[x] = new((byte)(40 + (x / 16 + y / 24 + (seed & 63)) % 160),
                            (byte)(35 + (x / 32 + y / 16 + ((seed >> 8) & 63)) % 170),
                            (byte)(45 + (x / 24 + y / 32 + ((seed >> 16) & 63)) % 150));
                }
            }
        });
    }
}
