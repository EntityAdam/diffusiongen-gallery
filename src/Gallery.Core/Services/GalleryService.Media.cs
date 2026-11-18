using Gallery.Core.Models;
using Gallery.Core.Services.Composers;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public Task<string> ThumbnailAsync(string id) => mediaReader.ThumbnailAsync(id);
    public Task<string> PreviewAsync(string id, ImageResolution resolution = ImageResolution.Fit) =>
        mediaReader.PreviewAsync(id, resolution);
    public Task<string> ViewerImageAsync(string id, ImageResolution resolution) =>
        mediaReader.ViewerImageAsync(id, resolution);
    public Task<(Stream Content, string ContentType)> OpenMediaAsync(string id) => mediaReader.OpenMediaAsync(id);
    public Task<ExportFile> ExportAsync(string id) => mediaReader.ExportAsync(id);
    public Task<ExportFile> ExportZipAsync(IEnumerable<string> ids) => mediaReader.ExportZipAsync(ids);
    public Task<ExportFile?> WorkflowAsync(string id) => mediaReader.WorkflowAsync(id);

    public const long MaxExportZipBytes = ExportComposer.MaxZipBytes;
    public static byte[] CopyExportContent(ExportFile file) => file.CopyContentBytes();
    public static void ClearExportContent(ExportFile file) => ExportComposer.ClearOwnedContent(file);
    public static string SafeFileName(string name) => ExportComposer.SafeFileName(name);
}
