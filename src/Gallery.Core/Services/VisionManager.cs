using Gallery.Core.Models;
using Gallery.Core.Services.Gateways;

namespace Gallery.Core.Services;

internal sealed class VisionManager(HttpClient httpClient)
{
    public Task<IReadOnlyList<string>> GetModelsAsync(VisionSettings settings) =>
        CreateGateway(settings).GetModelsAsync(settings.RequestTimeoutSeconds);

    public Task<(string Description, string Tags)> AnalyzeImageAsync(VisionSettings settings, string imageData) =>
        CreateGateway(settings).AnalyzeAsync(settings.Model, settings.RequestTimeoutSeconds, imageData);

    public Task<string> SuggestCollectionAsync(
        VisionSettings settings, string description, string tags, IEnumerable<string> collections) =>
        CreateGateway(settings).SuggestCollectionAsync(
            settings.Model, settings.RequestTimeoutSeconds, description, tags, collections);

    public Task<GalleryFilter> InterpretSearchAsync(
        VisionSettings settings, string query, IEnumerable<string> collections) =>
        CreateGateway(settings).InterpretSearchAsync(
            settings.Model, settings.RequestTimeoutSeconds, query, collections);

    public Task<(string Description, string Tags, string Collection, string Warning)> AnalyzeImageWithCollectionAsync(
        VisionSettings settings, string imageData, IEnumerable<string> collections) =>
        CreateGateway(settings).AnalyzeImageWithCollectionAsync(
            settings.Model, settings.RequestTimeoutSeconds, imageData, collections);

    private VisionService CreateGateway(VisionSettings settings) =>
        new(httpClient, settings.Endpoint, settings.ApiKey);
}
