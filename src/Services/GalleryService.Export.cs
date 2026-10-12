using Gallery.Core.Extractors;
using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Core;
using Gallery.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace Gallery.Services;

using Gallery.Services.Composers;

public sealed partial class GalleryService
{
    public const long MaxExportZipBytes = ExportComposer.MaxZipBytes;

    public async Task<(Stream Content, string ContentType)> OpenMediaAsync(string id)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await ExportComposer.OpenMediaAsync(await FindAsync(id, key.Bytes), key.Bytes);
    }

    public async Task<ExportFile> ExportAsync(string id)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await ExportComposer.ExportAsync(await FindAsync(id, key.Bytes), key.Bytes);
    }

    public async Task<ExportFile> ExportZipAsync(IEnumerable<string> ids)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await ExportComposer.ComposeZipAsync(ExportSelectionAsync(ids, key.Bytes));
    }

    private async IAsyncEnumerable<ExportFile> ExportSelectionAsync(IEnumerable<string> ids, byte[] key)
    {
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            yield return await ExportComposer.ExportAsync(await FindAsync(id, key), key);
    }

    public async Task<ExportFile?> WorkflowAsync(string id)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var record = await FindAsync(id, key.Bytes);
        var workflow = ComfyMetadata.WorkflowForExport(record.Metadata);
        return ExportComposer.ComposeWorkflow(record, workflow);
    }

    public static void ClearExportContent(ExportFile file) => ExportComposer.ClearOwnedContent(file);

    public static string SafeFileName(string name) => ExportComposer.SafeFileName(name);
}
