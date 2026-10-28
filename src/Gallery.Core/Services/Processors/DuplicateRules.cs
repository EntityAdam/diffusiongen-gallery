using System.Numerics;
using System.Collections.Immutable;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class DuplicateRules
{

    public const int Threshold = 6;
    public static int HammingDistance(ulong firstHash, ulong secondHash) => BitOperations.PopCount(firstHash ^ secondHash);

    public static List<DuplicateGroup> GroupDuplicates(IReadOnlyList<ImageRecord> records,
        Func<ImageRecord, ulong> hashOf, Func<string, long?> fileLength)
    {
        var imageHashes = records.Select(hashOf).ToArray();
        var parentIndexes = Enumerable.Range(0, records.Count).ToArray();
        ConnectDuplicateRecords(records, imageHashes, parentIndexes);
        var duplicateGroups = Enumerable.Range(0, parentIndexes.Length)
            .GroupBy(index => Find(parentIndexes, index))
            .Where(component => component.Count() > 1)
            .Select(component => component.ToList())
            .Select(componentIndexes => CreateDuplicateGroup(componentIndexes, records, imageHashes, fileLength));
        return OrderDuplicateGroups(duplicateGroups);
    }

    private static void ConnectDuplicateRecords(IReadOnlyList<ImageRecord> records, ulong[] imageHashes,
        int[] parentIndexes)
    {
        for (var candidateIndex = 0; candidateIndex < records.Count; candidateIndex++)
            for (var comparisonIndex = candidateIndex + 1; comparisonIndex < records.Count; comparisonIndex++)
            {
                var candidateRecord = records[candidateIndex];
                var comparisonRecord = records[comparisonIndex];
                if (candidateRecord.MediaType != comparisonRecord.MediaType) continue;
                if (AreDuplicates(candidateRecord, comparisonRecord, imageHashes[candidateIndex],
                    imageHashes[comparisonIndex]))
                    Union(parentIndexes, candidateIndex, comparisonIndex);
            }
    }

    private static DuplicateGroup CreateDuplicateGroup(List<int> componentIndexes, IReadOnlyList<ImageRecord> records,
        ulong[] imageHashes, Func<string, long?> fileLength)
    {
        var maximumDistance = MaximumGroupDistance(componentIndexes, imageHashes);
        var duplicateRecords = componentIndexes.Select(index => records[index]).ToList();
        var imageRecord = PickKeeper(duplicateRecords);
        var orderedDuplicateRecords = OrderDuplicateRecords(duplicateRecords, imageRecord);
        var groupIsExact = AllRecordsMatchExactly(duplicateRecords, imageRecord);
        var reclaimableBytes = CalculateReclaimableBytes(orderedDuplicateRecords, fileLength);
        return new(imageRecord.Id, orderedDuplicateRecords.Select(duplicateImageRecord => duplicateImageRecord.Id).ToImmutableArray(),
            maximumDistance, groupIsExact, reclaimableBytes);
    }

    private static int MaximumGroupDistance(IReadOnlyList<int> componentIndexes, ulong[] imageHashes)
    {
        var maximumDistance = 0;
        foreach (var candidateIndex in componentIndexes)
            foreach (var comparisonIndex in componentIndexes)
                maximumDistance = Math.Max(maximumDistance, HammingDistance(imageHashes[candidateIndex], imageHashes[comparisonIndex]));
        return maximumDistance;
    }

    private static List<ImageRecord> OrderDuplicateRecords(
        List<ImageRecord> duplicateRecords, ImageRecord imageRecord) =>
        duplicateRecords
            .OrderByDescending(duplicateImageRecord => duplicateImageRecord.Id == imageRecord.Id)
            .ThenBy(duplicateImageRecord => duplicateImageRecord.ImportedAt).ToList();

    private static bool AllRecordsMatchExactly(List<ImageRecord> duplicateRecords, ImageRecord imageRecord) =>
        duplicateRecords.All(duplicateImageRecord => duplicateImageRecord.PngSha256.Length > 0
            && duplicateImageRecord.PngSha256 == imageRecord.PngSha256);

    private static long CalculateReclaimableBytes(
        List<ImageRecord> orderedDuplicateRecords, Func<string, long?> fileLength) =>
        orderedDuplicateRecords.Skip(1)
            .Sum(duplicateImageRecord => fileLength(duplicateImageRecord.StoredPath) ?? 0);

    private static List<DuplicateGroup> OrderDuplicateGroups(IEnumerable<DuplicateGroup> duplicateGroups) =>
        duplicateGroups.OrderByDescending(duplicateGroup => duplicateGroup.ReclaimableBytes)
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
        var orderedCandidates = imageRecords
            .OrderBy(imageRecord => imageRecord.IsMarkedForDeletion)
            .ThenByDescending(imageRecord => imageRecord.Rating)
            .ThenByDescending(imageRecord => imageRecord.IsFavorite)
            .ThenByDescending(imageRecord => (long)imageRecord.Width * imageRecord.Height)
            .ThenByDescending(imageRecord => imageRecord.OriginalBytes)
            .ThenBy(imageRecord => imageRecord.ImportedAt);
        using var candidateEnumerator = orderedCandidates.GetEnumerator();
        if (!candidateEnumerator.MoveNext())
            throw new InvalidOperationException("Cannot choose a duplicate keeper because no image records were supplied.");
        return candidateEnumerator.Current;
    }

    public static HashSet<string> InitialKeepers(IReadOnlyCollection<ImageRecord> groupRecords)
    {
        var unmarkedImageIds = CollectUnmarkedIds(groupRecords);
        return HasPartialDeletionDecision(unmarkedImageIds, groupRecords.Count)
            ? unmarkedImageIds : [PickKeeper(groupRecords).Id];
    }

    private static HashSet<string> CollectUnmarkedIds(IEnumerable<ImageRecord> records) =>
        records.Where(imageRecord => !imageRecord.IsMarkedForDeletion).Select(imageRecord => imageRecord.Id).ToHashSet();

    private static bool HasPartialDeletionDecision(IReadOnlyCollection<string> unmarkedIds, int recordCount) =>
        unmarkedIds.Count > 0 && unmarkedIds.Count < recordCount;

    private static bool SimilarShape(ImageRecord candidateRecord, ImageRecord comparisonRecord)
    {
        if (!HasValidDimensions(candidateRecord) || !HasValidDimensions(comparisonRecord)) return true;
        var candidateAspectRatio = (double)candidateRecord.Width / candidateRecord.Height;
        var comparisonAspectRatio = (double)comparisonRecord.Width / comparisonRecord.Height;
        return Math.Abs(candidateAspectRatio - comparisonAspectRatio)
            / Math.Max(candidateAspectRatio, comparisonAspectRatio) <= 0.05;
    }

    private static bool HasValidDimensions(ImageRecord imageRecord) =>
        imageRecord.Width > 0 && imageRecord.Height > 0;

    private static int Find(int[] parentIndexes, int index)
    {
        while (parentIndexes[index] != index)
            index = parentIndexes[index] = parentIndexes[parentIndexes[index]];
        return index;
    }

    private static void Union(int[] parentIndexes, int firstIndex, int secondIndex) =>
        parentIndexes[Find(parentIndexes, secondIndex)] = Find(parentIndexes, firstIndex);
}
