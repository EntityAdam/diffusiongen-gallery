using Gallery.Web.Models;
using Gallery.Web.Services;

namespace Gallery.Web.Components;

internal sealed class GalleryBrowsingProjection
{
    private IReadOnlyList<WebImageRecord>? source;
    private WebGalleryFilter? filter;
    private bool groupedByPrompt;

    public IReadOnlyList<WebImageRecord> Images { get; private set; } = [];
    public IReadOnlyDictionary<string, WebPromptGroup> PromptGroupsByImageId { get; private set; } =
        new Dictionary<string, WebPromptGroup>();

    public void Update(GalleryService gallery, IReadOnlyList<WebImageRecord> records,
        WebGalleryFilter currentFilter, bool groupByPrompt)
    {
        if (ReferenceEquals(source, records) && filter == currentFilter && groupedByPrompt == groupByPrompt)
            return;

        var filtered = gallery.Filter(records, currentFilter);
        var groups = groupByPrompt ? gallery.GroupByPrompt(filtered) : [];
        Images = groupByPrompt ? groups.SelectMany(group => group.Images).ToArray() : filtered;
        PromptGroupsByImageId = groups.SelectMany(group => group.Images.Select(image => (image.Id, Group: group)))
            .ToDictionary(item => item.Id, item => item.Group);
        source = records;
        filter = currentFilter;
        groupedByPrompt = groupByPrompt;
    }

    public void Invalidate() => source = null;

    public void Clear()
    {
        source = null;
        filter = null;
        Images = [];
        PromptGroupsByImageId = new Dictionary<string, WebPromptGroup>();
    }
}
