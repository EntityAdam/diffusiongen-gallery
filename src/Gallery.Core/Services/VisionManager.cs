using Gallery.Core.Models;
using Gallery.Core.Services.Gateways;

namespace Gallery.Core.Services;

internal sealed class VisionManager(HttpClient httpClient)
{
    public Task<IReadOnlyList<string>> GetModelsAsync(VisionSettings settings) =>
        CreateGateway(settings).GetModelsAsync(settings.RequestTimeoutSeconds);

    public Task<(string Description, string Tags)> AnalyzeImageAsync(VisionSettings settings, string imageData, IReadOnlyList<string>? existingTags = null, CancellationToken cancellationToken = default) =>
        CreateGateway(settings).AnalyzeAsync(settings.Model, settings.RequestTimeoutSeconds, imageData, existingTags, cancellationToken);

    public Task<string> SuggestSetAsync(VisionSettings settings, string collection, IReadOnlyList<MediaSet> sets, IReadOnlyList<ImageRecord> images) =>
        CreateGateway(settings).SuggestSetAsync(collection, sets, images);

    public Task<VisionAnalysisResult> AnalyzeResultAsync(VisionSettings settings, string imageData,
        IReadOnlyList<string> existingTags, CancellationToken token) =>
        CreateGateway(settings).AnalyzeResultAsync(settings.Model, settings.RequestTimeoutSeconds, imageData,
            settings.UseExistingTags ? existingTags : [], token);

    private VisionService CreateGateway(VisionSettings settings) =>
        new(httpClient, settings);
}
