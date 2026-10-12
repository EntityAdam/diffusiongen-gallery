using System.Numerics;
using System.Collections.Immutable;
using Gallery.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Core.Processors;

internal static class DuplicateRules
{
    private const int DifferenceHashDimension = 8;

    public const int Threshold = 6;
    public const string EmptyKeeperCandidatesMessage =
        "Cannot choose a duplicate keeper by deletion status, rating, favorite status, dimensions, original size, and import time: candidate count was 0.";

    public static ulong DifferenceHash(byte[] imageBytes)
    {
        using var image = LoadNormalizedImage(imageBytes);
        return ComputeDifferenceHash(image);
    }

    private static Image<L8> LoadNormalizedImage(byte[] imageBytes)
    {
        var image = Image.Load<L8>(imageBytes);
        try
        {
            image.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(DifferenceHashDimension + 1, DifferenceHashDimension), Mode = ResizeMode.Stretch }));
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

    public static int HammingDistance(ulong firstHash, ulong secondHash) => BitOperations.PopCount(firstHash ^ secondHash);

    public static List<DuplicateGroup> GroupDuplicates(IReadOnlyList<ImageRecord> records,
        Func<ImageRecord, ulong> hashOf, Func<string, long?> fileLength)
    {
        var imageHashes = records.Select(hashOf).ToArray();
        var disjointSet = new DisjointSet(records.Count);
        ConnectDuplicateRecords(records, imageHashes, disjointSet);
        return BuildDuplicateGroups(records, imageHashes, disjointSet, fileLength);
    }

    private static void ConnectDuplicateRecords(IReadOnlyList<ImageRecord> records, ulong[] imageHashes,
        DisjointSet disjointSet)
    {
        for (var candidateIndex = 0; candidateIndex < records.Count; candidateIndex++)
            for (var comparisonIndex = candidateIndex + 1; comparisonIndex < records.Count; comparisonIndex++)
            {
                var candidateRecord = records[candidateIndex];
                var comparisonRecord = records[comparisonIndex];
                if (candidateRecord.MediaType != comparisonRecord.MediaType) continue;
                if (AreDuplicates(candidateRecord, comparisonRecord, imageHashes[candidateIndex],
                    imageHashes[comparisonIndex]))
                    disjointSet.Union(candidateIndex, comparisonIndex);
            }
    }

    private static List<DuplicateGroup> BuildDuplicateGroups(IReadOnlyList<ImageRecord> records, ulong[] imageHashes,
        DisjointSet disjointSet, Func<string, long?> fileLength)
    {
        var duplicateGroups = BuildDuplicateComponents(records.Count, disjointSet)
            .Select(componentIndexes => CreateDuplicateGroup(componentIndexes, records, imageHashes, fileLength));
        return OrderDuplicateGroups(duplicateGroups);
    }

    private static IEnumerable<List<int>> BuildDuplicateComponents(int recordCount, DisjointSet disjointSet) =>
        Enumerable.Range(0, recordCount).GroupBy(disjointSet.Find)
            .Where(component => component.Count() > 1).Select(component => component.ToList());

    private static DuplicateGroup CreateDuplicateGroup(List<int> componentIndexes, IReadOnlyList<ImageRecord> records,
        ulong[] imageHashes, Func<string, long?> fileLength)
    {
        var maxDistance = 0;
        foreach (var candidateIndex in componentIndexes)
            foreach (var comparisonIndex in componentIndexes)
                maxDistance = Math.Max(maxDistance, HammingDistance(imageHashes[candidateIndex], imageHashes[comparisonIndex]));
        var duplicateRecords = componentIndexes.Select(index => records[index]).ToList();
        var imageRecord = PickKeeper(duplicateRecords);
        var orderedDuplicateRecords = duplicateRecords
            .OrderByDescending(duplicateImageRecord => duplicateImageRecord.Id == imageRecord.Id)
            .ThenBy(duplicateImageRecord => duplicateImageRecord.ImportedAt).ToList();
        var groupIsExact = duplicateRecords.All(duplicateImageRecord => duplicateImageRecord.PngSha256.Length > 0
            && duplicateImageRecord.PngSha256 == imageRecord.PngSha256);
        var reclaimableBytes = orderedDuplicateRecords.Skip(1)
            .Sum(duplicateImageRecord => fileLength(duplicateImageRecord.StoredPath) ?? 0);
        return new(imageRecord.Id, orderedDuplicateRecords.Select(duplicateImageRecord => duplicateImageRecord.Id).ToImmutableArray(),
            maxDistance, groupIsExact, reclaimableBytes);
    }

    private static List<DuplicateGroup> OrderDuplicateGroups(IEnumerable<DuplicateGroup> duplicateGroups) =>
        duplicateGroups.OrderByDescending(group => group.ReclaimableBytes)
            .ThenByDescending(group => group.ImageIds.Length).ToList();

    private static bool AreDuplicates(ImageRecord candidateRecord, ImageRecord comparisonRecord, ulong candidateHash,
        ulong comparisonHash)
    {
        var recordsMatchExactly = candidateRecord.PngSha256.Length > 0
            && candidateRecord.PngSha256 == comparisonRecord.PngSha256;
        return recordsMatchExactly || (HammingDistance(candidateHash, comparisonHash) <= Threshold
            && SimilarShape(candidateRecord, comparisonRecord));
    }

    public static ImageRecord PickKeeper(IEnumerable<ImageRecord> imageRecords)
    {
        var candidates = imageRecords.ToList();
        RequireKeeperCandidates(candidates);
        return OrderKeeperCandidates(candidates).First();
    }

    private static void RequireKeeperCandidates(IReadOnlyCollection<ImageRecord> candidates)
    {
        if (candidates.Count == 0) throw new InvalidOperationException(EmptyKeeperCandidatesMessage);
    }

    private static IOrderedEnumerable<ImageRecord> OrderKeeperCandidates(IEnumerable<ImageRecord> candidates) => candidates
            .OrderBy(imageRecord => imageRecord.MarkedForDeletion)
            .ThenByDescending(imageRecord => imageRecord.Rating)
            .ThenByDescending(imageRecord => imageRecord.Favorite)
            .ThenByDescending(imageRecord => (long)imageRecord.Width * imageRecord.Height)
            .ThenByDescending(imageRecord => imageRecord.OriginalBytes)
            .ThenBy(imageRecord => imageRecord.ImportedAt);

    public static HashSet<string> InitialKeepers(IReadOnlyCollection<ImageRecord> groupRecords)
    {
        var unmarkedImageIds = CollectUnmarkedIds(groupRecords);
        return HasPartialDeletionDecision(unmarkedImageIds, groupRecords.Count)
            ? unmarkedImageIds : [PickKeeper(groupRecords).Id];
    }

    private static HashSet<string> CollectUnmarkedIds(IEnumerable<ImageRecord> records) =>
        records.Where(record => !record.MarkedForDeletion).Select(record => record.Id).ToHashSet();

    private static bool HasPartialDeletionDecision(IReadOnlyCollection<string> unmarkedIds, int recordCount) =>
        unmarkedIds.Count > 0 && unmarkedIds.Count < recordCount;

    private static bool SimilarShape(ImageRecord candidateRecord, ImageRecord comparisonRecord)
    {
        if (candidateRecord.Width <= 0 || candidateRecord.Height <= 0
            || comparisonRecord.Width <= 0 || comparisonRecord.Height <= 0) return true;
        var candidateAspectRatio = (double)candidateRecord.Width / candidateRecord.Height;
        var comparisonAspectRatio = (double)comparisonRecord.Width / comparisonRecord.Height;
        return Math.Abs(candidateAspectRatio - comparisonAspectRatio)
            / Math.Max(candidateAspectRatio, comparisonAspectRatio) <= 0.05;
    }

    private sealed class DisjointSet
    {
        private readonly int[] parentIndexes;

        public DisjointSet(int count) => parentIndexes = Enumerable.Range(0, count).ToArray();

        public int Find(int index)
        {
            while (parentIndexes[index] != index)
                index = parentIndexes[index] = parentIndexes[parentIndexes[index]];
            return index;
        }

        public void Union(int firstIndex, int secondIndex) =>
            parentIndexes[Find(secondIndex)] = Find(firstIndex);
    }
}
