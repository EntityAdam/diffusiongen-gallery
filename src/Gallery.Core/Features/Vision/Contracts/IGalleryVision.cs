using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Configures vision analysis, manages checkpoints, and applies suggestions.</summary>
public interface IGalleryVision
{
    Task<MediaSet?> SuggestDestinationSetAsync(string collectionId, IEnumerable<string> mediaIds);
    Task<VisionSettings> LoadSettingsAsync();
    Task SaveSettingsAsync(VisionSettings settings);
    Task<VisionCheckpointSession> OpenVisionCheckpointAsync();
    Task<IReadOnlyList<string>> GetVisionModelsAsync(VisionSettings settings);
    Task<(string Description, string Tags)> AnalyzeImageAsync(
        VisionSettings settings, string imageData, CancellationToken cancellationToken = default);
    Task<VisionAnalysisResult> AnalyzeForDraftAsync(
        string id, VisionSettings settings, CancellationToken token = default);
    Task<ImageRecord> ApproveVisionAsync(string id, string description, string tags, string model,
        VisionAnalysis? analysis, bool tagsOnly = false, bool useExistingTags = true, bool suggestNewTags = true);
    Task<VisionAnalysis?> ReadVisionAnalysisAsync(string id);
    Task<ExportFile> ExportVisionAnalysisAsync(string id);
}
