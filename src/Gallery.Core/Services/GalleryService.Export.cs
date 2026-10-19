using Gallery.Core.Services.Processors;
using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace Gallery.Core.Services;

using Gallery.Core.Services.Composers;

public sealed partial class GalleryService
{
    public static byte[] CopyExportContent(ExportFile file) => file.CopyContentBytes();

    public const long MaxExportZipBytes = ExportComposer.MaxZipBytes;

    public async Task<(Stream Content, string ContentType)> OpenMediaAsync(string id)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var record = await FindAsync(id, keyLease.Bytes);
        var plaintext = await StoredMediaProcessor.ReadAsync(record, keyLease.Bytes);
        return (new MemoryStream(plaintext, writable: false), record.ContentType);
    }

    public async Task<ExportFile> ExportAsync(string id)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        return await ExportRecordAsync(await FindAsync(id, keyLease.Bytes), keyLease.Bytes);
    }

    public async Task<ExportFile> ExportZipAsync(IEnumerable<string> ids)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        return await ExportComposer.ComposeZipAsync(ExportSelectionAsync(ids, keyLease.Bytes));
    }

    private async IAsyncEnumerable<ExportFile> ExportSelectionAsync(IEnumerable<string> ids, byte[] key)
    {
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            yield return await ExportRecordAsync(await FindAsync(id, key), key);
    }

    private static async Task<ExportFile> ExportRecordAsync(ImageRecord record, byte[] key)
    {
        var plaintext = await StoredMediaProcessor.ReadAsync(record, key);
        try { return ExportComposer.Export(record, plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task<ExportFile?> WorkflowAsync(string id)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var record = await FindAsync(id, keyLease.Bytes);
        var workflow = ComfyMetadata.WorkflowForExport(record.Metadata);
        return ExportComposer.ComposeWorkflow(record, workflow);
    }

    public static void ClearExportContent(ExportFile file) => ExportComposer.ClearOwnedContent(file);

    public static string SafeFileName(string name) => ExportComposer.SafeFileName(name);
}
