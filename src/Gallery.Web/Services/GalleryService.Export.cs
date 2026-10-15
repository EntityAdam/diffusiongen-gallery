using Gallery.Web.Services.Processors;
using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Web.Exceptions;
using Gallery.Web.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace Gallery.Web.Services;

using Gallery.Web.Services.Composers;

public sealed partial class GalleryService
{
    public const long MaxExportZipBytes = ExportComposer.MaxZipBytes;

    public async Task<(Stream Content, string ContentType)> OpenMediaAsync(string id)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await ExportComposer.OpenMediaAsync(await FindAsync(id, key.Bytes), key.Bytes);
    }

    public async Task<WebExportFile> ExportAsync(string id)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await ExportComposer.ExportAsync(await FindAsync(id, key.Bytes), key.Bytes);
    }

    public async Task<WebExportFile> ExportZipAsync(IEnumerable<string> ids)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await ExportComposer.ComposeZipAsync(ExportSelectionAsync(ids, key.Bytes));
    }

    private async IAsyncEnumerable<WebExportFile> ExportSelectionAsync(IEnumerable<string> ids, byte[] key)
    {
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            yield return await ExportComposer.ExportAsync(await FindAsync(id, key), key);
    }

    public async Task<WebExportFile?> WorkflowAsync(string id)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var record = await FindAsync(id, key.Bytes);
        var workflow = ComfyMetadata.WorkflowForExport(record.Metadata);
        return ExportComposer.ComposeWorkflow(record, workflow);
    }

    public static void ClearExportContent(WebExportFile file) => ExportComposer.ClearOwnedContent(file);

    public static string SafeFileName(string name) => ExportComposer.SafeFileName(name);
}
