namespace Gallery.Web.Components.Features.Settings;

internal sealed record ExperimentalFeature(string Id, string Label, string Description, bool EnabledByDefault = false);

internal static class ExperimentalFeatures
{
    internal static readonly ExperimentalFeature InfiniteScroll = new("gallery-infinite-scroll", "Infinite scroll gallery",
        "Browse continuously and adjust thumbnail size with a slider. This applies the next time you open the gallery.");

    internal static IReadOnlyList<ExperimentalFeature> All { get; } = [InfiniteScroll];
}
