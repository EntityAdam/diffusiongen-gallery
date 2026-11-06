using Gallery.Core.Models;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Validators;

namespace Gallery.Core.Services;

internal sealed class VisionManager(HttpClient httpClient)
{
    public Task<IReadOnlyList<string>> GetModelsAsync(VisionSettings settings) =>
        CreateGateway(settings).GetModelsAsync(settings.RequestTimeoutSeconds);

    public Task<(string Description, string Tags)> AnalyzeImageAsync(VisionSettings settings, string imageData, IReadOnlyList<string>? existingTags = null) =>
        CreateGateway(settings).AnalyzeAsync(settings.Model, settings.RequestTimeoutSeconds, imageData, existingTags);

    public Task<string> SuggestCollectionAsync(
        VisionSettings settings, string description, string tags, IEnumerable<string> collections) =>
        CreateGateway(settings).SuggestCollectionAsync(
            settings.Model, settings.RequestTimeoutSeconds, description, tags, collections);

    public Task<string> SuggestSetAsync(VisionSettings settings, string collection, IReadOnlyList<MediaSet> sets, IReadOnlyList<ImageRecord> images) =>
        CreateGateway(settings).SuggestSetAsync(collection, sets, images);

    public Task<GalleryFilter> InterpretSearchAsync(
        VisionSettings settings, string query, IEnumerable<string> collections)
    {
        SearchRequestValidator.Validate(query);
        return CreateGateway(settings).InterpretSearchAsync(
            settings.Model, settings.RequestTimeoutSeconds, query, collections);
    }

    public Task<(string Description, string Tags, string Collection, string Warning)> AnalyzeImageWithCollectionAsync(
        VisionSettings settings, string imageData, IEnumerable<string> collections, IReadOnlyList<string>? existingTags = null) =>
        CreateGateway(settings).AnalyzeImageWithCollectionAsync(
            settings.Model, settings.RequestTimeoutSeconds, imageData, collections, existingTags);

    private VisionService CreateGateway(VisionSettings settings) =>
        new(httpClient, settings);
}
