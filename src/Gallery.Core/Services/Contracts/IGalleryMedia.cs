using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Reads encrypted media and exports decrypted content.</summary>
public interface IGalleryMedia
{
    Task<string> ThumbnailAsync(string id);
    Task<string> PreviewAsync(string id, ImageResolution resolution = ImageResolution.Fit);
    Task<string> ViewerImageAsync(string id, ImageResolution resolution);
    Task<(Stream Content, string ContentType)> OpenMediaAsync(string id);
    Task<ExportFile> ExportAsync(string id);
    Task<ExportFile> ExportZipAsync(IEnumerable<string> ids);
    Task<ExportFile?> WorkflowAsync(string id);
}
