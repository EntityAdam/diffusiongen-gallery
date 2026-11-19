namespace Gallery.Web.Components.Features.Browsing;

internal static class MediaRangeSelection
{
    public static string? Select(HashSet<string> selection, List<string> orderedIds, string? anchor, string target, bool range)
    {
        var targetIndex = orderedIds.IndexOf(target);
        if (targetIndex < 0) return anchor;

        var anchorIndex = range && anchor is not null ? orderedIds.IndexOf(anchor) : -1;
        if (anchorIndex >= 0)
        {
            var first = Math.Min(anchorIndex, targetIndex);
            var last = Math.Max(anchorIndex, targetIndex);
            selection.UnionWith(orderedIds.Skip(first).Take(last - first + 1));
        }
        else if (!selection.Remove(target))
        {
            selection.Add(target);
        }

        return target;
    }
}
