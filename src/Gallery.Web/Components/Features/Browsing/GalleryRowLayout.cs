namespace Gallery.Web.Components.Features.Browsing;

internal sealed class GalleryRowLayout(GalleryBrowsingProjection projection, int columns, double cardWidth)
{
    internal const double Gap = 10;
    internal readonly record struct Row(int Start, int Count);

    private readonly Row[] rows = BuildRows(projection, columns);
    private double[]? heights;
    private double[]? offsets;

    internal int Count => rows.Length;
    internal Row this[int index] => rows[index];
    internal double Height => Math.Max(0, Offset(Count) - Gap);

    private static Row[] BuildRows(GalleryBrowsingProjection projection, int columns)
    {
        var rows = new List<Row>();
        var images = projection.Images;
        for (var start = 0; start < images.Count;)
        {
            var id = images[start].Id;
            var count = images.Count - start;
            if (projection.PromptGroupsByImageId.TryGetValue(id, out var prompt)) count = prompt.Images.Length;
            else if (projection.DiffusionModelGroupsByImageId.TryGetValue(id, out var model)) count = model.Images.Length;
            if (projection.PromptGroupsByImageId.Count != 0 || projection.DiffusionModelGroupsByImageId.Count != 0)
                rows.Add(new(start, 0));
            var end = start + count;
            while (start < end)
            {
                var take = Math.Min(columns, end - start);
                rows.Add(new(start, take));
                start += take;
            }
        }
        return rows.ToArray();
    }

    // A Fenwick tree keeps both prefix offsets and height corrections logarithmic.
    private void InitializeOffsets()
    {
        if (offsets is not null) return;
        heights = new double[Count];
        offsets = new double[Count + 1];
        for (var index = 1; index <= Count; index++)
        {
            var row = rows[index - 1];
            var estimate = row.Count > 0 ? cardWidth + (cardWidth < 220 ? 82 : 46) : HeaderHeight(row.Start);
            heights[index - 1] = estimate + Gap;
            offsets[index] += heights[index - 1];
            var parent = index + (index & -index);
            if (parent <= Count) offsets[parent] += offsets[index];
        }
    }

    private double HeaderHeight(int start) =>
        projection.PromptGroupsByImageId.TryGetValue(projection.Images[start].Id, out var group)
        && group.Kind == Gallery.Core.Models.PromptGroup.Shared ? 100 : 50;

    internal double Offset(int index)
    {
        InitializeOffsets();
        var offset = 0d;
        for (var position = index; position > 0; position -= position & -position)
            offset += offsets![position];
        return offset;
    }

    internal bool Measure(int row, double height)
    {
        if (row < 0 || row >= Count || !double.IsFinite(height) || height is < 10 or > 4096) return false;
        InitializeOffsets();
        var difference = height + Gap - heights![row];
        if (Math.Abs(difference) < 0.5) return false;
        heights[row] += difference;
        for (var index = row + 1; index <= Count; index += index & -index)
            offsets![index] += difference;
        return true;
    }

    internal int RowAt(double offset)
    {
        InitializeOffsets();
        var index = 0;
        var step = 1;
        while (step <= Count / 2) step *= 2;
        for (; step > 0; step /= 2)
        {
            var next = index + step;
            if (next > Count || offsets![next] > offset) continue;
            offset -= offsets[next];
            index = next;
        }
        return Math.Clamp(index, 0, Math.Max(0, Count - 1));
    }

    internal int RowOfImage(int image)
    {
        var start = 0;
        var end = Count;
        while (start < end)
        {
            var middle = (start + end) / 2;
            if (rows[middle].Start <= image) start = middle + 1;
            else end = middle;
        }
        return start - 1;
    }
}
