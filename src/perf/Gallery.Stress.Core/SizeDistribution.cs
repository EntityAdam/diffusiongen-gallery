using System.Globalization;
using System.Text.RegularExpressions;

namespace Gallery.Stress.Core;

public sealed record SizeDistribution(long MinBytes, long MaxBytes, int? Width = null, int? Height = null)
{
    public long Draw(Random random) => MinBytes == MaxBytes ? MinBytes : random.NextInt64(MinBytes, checked(MaxBytes + 1));

    public static SizeDistribution Parse(string text)
    {
        var parts = text.Split(':', 2);
        if (parts.Length != 2) throw new ArgumentException("Size distribution must be fixed:1MB, uniform:8KB-60KB, or tier:4MB@2048x2048.");
        if (parts[0] == "fixed") return new(ParseBytes(parts[1]), ParseBytes(parts[1]));
        if (parts[0] == "uniform")
        {
            var range = parts[1].Split('-', 2);
            if (range.Length != 2) throw new ArgumentException("A uniform distribution requires minimum-maximum.");
            var low = ParseBytes(range[0]);
            var high = ParseBytes(range[1]);
            if (high < low) throw new ArgumentException("The maximum must be at least the minimum.");
            return new(low, high);
        }
        if (parts[0] == "tier")
        {
            var tier = parts[1].Split('@', 2);
            var dimensions = tier.Length == 2 ? tier[1].Split('x', 2) : [];
            if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out var width) || !int.TryParse(dimensions[1], out var height)
                || width <= 0 || height <= 0 || width > 20000 || height > 20000)
                throw new ArgumentException("A tier requires positive dimensions, for example tier:4MB@2048x2048.");
            var bytes = ParseBytes(tier[0]);
            return new(bytes, bytes, width, height);
        }
        throw new ArgumentException("Unknown size distribution.");
    }

    public static long ParseBytes(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(\d+(?:\.\d+)?)\s*(B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)?$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new ArgumentException("Invalid byte size; use a positive value such as 1MB.");
        var exponent = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "" or "B" => 0, "KB" or "KIB" => 1, "MB" or "MIB" => 2, "GB" or "GIB" => 3, "TB" or "TIB" => 4, _ => 0
        };
        return Positive(decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * (decimal)Math.Pow(1024, exponent));
    }

    public static long ParseCount(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(\d+(?:\.\d+)?)\s*([kKmM])?$");
        if (!match.Success) throw new ArgumentException("Invalid item count; use a positive count such as 100k or 2.1M.");
        return Positive(decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * (match.Groups[2].Value.ToUpperInvariant() switch { "K" => 1000, "M" => 1000000, _ => 1 }));
    }

    private static long Positive(decimal value)
    {
        if (value < 1 || value > long.MaxValue || value != decimal.Truncate(value)) throw new ArgumentException("The value must be a positive whole number.");
        return (long)value;
    }
}
