using System.Numerics;
using Gallery.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Core;

public static class DuplicateRules
{
    public const int Threshold = 6;

    public static ulong DifferenceHash(byte[] image)
    {
        using var pixels = Image.Load<L8>(image);
        pixels.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(9, 8), Mode = ResizeMode.Stretch }));
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++, bit++)
                if (pixels[x, y].PackedValue > pixels[x + 1, y].PackedValue) hash |= 1UL << bit;
        return hash;
    }

    public static int HammingDistance(ulong left, ulong right) => BitOperations.PopCount(left ^ right);

    public static List<DuplicateGroup> GroupDuplicates(IReadOnlyList<ImageRecord> records,
        Func<ImageRecord, ulong> hashOf, Func<string, long?> fileLength, int threshold = Threshold)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(hashOf);
        ArgumentNullException.ThrowIfNull(fileLength);
        if (threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold));

        var hashes = records.Select(hashOf).ToArray();
        var parent = Enumerable.Range(0, records.Count).ToArray();
        int Root(int index)
        {
            while (parent[index] != index) index = parent[index] = parent[parent[index]];
            return index;
        }

        for (var left = 0; left < records.Count; left++)
            for (var right = left + 1; right < records.Count; right++)
            {
                if (records[left].MediaType != records[right].MediaType) continue;
                var exact = records[left].PngSha256.Length > 0 && records[left].PngSha256 == records[right].PngSha256;
                if (exact || (HammingDistance(hashes[left], hashes[right]) <= threshold
                    && SimilarShape(records[left], records[right])))
                    parent[Root(right)] = Root(left);
            }

        var groups = new List<DuplicateGroup>();
        foreach (var members in Enumerable.Range(0, records.Count).GroupBy(Root).Where(group => group.Count() > 1))
        {
            var indexes = members.ToList();
            var maxDistance = 0;
            foreach (var left in indexes)
                foreach (var right in indexes)
                    maxDistance = Math.Max(maxDistance, HammingDistance(hashes[left], hashes[right]));
            var items = indexes.Select(index => records[index]).ToList();
            var keeper = PickKeeper(items);
            var ordered = items.OrderByDescending(item => item.Id == keeper.Id).ThenBy(item => item.ImportedAt).ToList();
            var exact = items.All(item => item.PngSha256.Length > 0 && item.PngSha256 == keeper.PngSha256);
            var reclaimable = ordered.Skip(1).Sum(item => fileLength(item.StoredPath) ?? 0);
            groups.Add(new(keeper.Id, ordered.Select(item => item.Id).ToList(), maxDistance, exact, reclaimable));
        }
        return groups.OrderByDescending(group => group.ReclaimableBytes).ThenByDescending(group => group.Ids.Count).ToList();
    }

    public static ImageRecord PickKeeper(IEnumerable<ImageRecord> records) => records
        .OrderBy(record => record.MarkedForDeletion)
        .ThenByDescending(record => record.Rating)
        .ThenByDescending(record => record.Favorite)
        .ThenByDescending(record => (long)record.Width * record.Height)
        .ThenByDescending(record => record.OriginalBytes)
        .ThenBy(record => record.ImportedAt)
        .First();

    public static HashSet<string> InitialKeepers(IReadOnlyCollection<ImageRecord> group)
    {
        var unmarked = group.Where(record => !record.MarkedForDeletion).Select(record => record.Id).ToHashSet();
        return unmarked.Count > 0 && unmarked.Count < group.Count ? unmarked : [PickKeeper(group).Id];
    }

    private static bool SimilarShape(ImageRecord left, ImageRecord right)
    {
        if (left.Width <= 0 || left.Height <= 0 || right.Width <= 0 || right.Height <= 0) return true;
        var a = (double)left.Width / left.Height;
        var b = (double)right.Width / right.Height;
        return Math.Abs(a - b) / Math.Max(a, b) <= 0.05;
    }
}
