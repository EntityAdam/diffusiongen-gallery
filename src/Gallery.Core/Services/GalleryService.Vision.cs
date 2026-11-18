using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<VisionSettings> SettingsAsync() => visionWorkflow.LoadSettingsAsync();
    public Task SaveSettingsAsync(VisionSettings settings) => visionWorkflow.SaveSettingsAsync(settings);
    public Task<VisionCheckpointSession> OpenVisionCheckpointAsync() => visionWorkflow.OpenVisionCheckpointAsync();

    public Task<IReadOnlyList<string>> GetVisionModelsAsync(VisionSettings settings) =>
        visionWorkflow.GetVisionModelsAsync(settings);

    public Task<(string Description, string Tags)> AnalyzeImageAsync(
        VisionSettings settings, string imageData, CancellationToken cancellationToken = default) =>
        visionWorkflow.AnalyzeImageAsync(settings, imageData, cancellationToken);

    public Task<VisionAnalysisResult> AnalyzeForDraftAsync(
        string id, VisionSettings settings, CancellationToken token = default) =>
        visionWorkflow.AnalyzeForDraftAsync(id, settings, token);

    public Task<ImageRecord> ApproveVisionAsync(string id, string description, string tags, string model,
        VisionAnalysis? analysis, bool tagsOnly = false, bool useExistingTags = true, bool suggestNewTags = true) =>
        visionWorkflow.ApproveVisionAsync(id, description, tags, model, analysis, tagsOnly, useExistingTags, suggestNewTags);

    public Task<VisionAnalysis?> ReadVisionAnalysisAsync(string id) => visionWorkflow.ReadVisionAnalysisAsync(id);
    public Task<ExportFile> ExportVisionAnalysisAsync(string id) => visionWorkflow.ExportVisionAnalysisAsync(id);
}
