using Gallery.Core.Services.Processors.Codecs;
using Gallery.Core.Services.Composers.Cleaners;
using Gallery.Core.Services.Composers.Formatters;
using Gallery.Core.Services.Processors;
using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Composers;

internal static class ExportComposer
{
    internal const long MaxZipBytes = 1024L * 1024 * 1024;
    public static async Task<ExportFile> ComposeZipAsync(IAsyncEnumerable<ExportFile> files)
        => await BuildZipAsync(files);

    private static async Task<ExportFile> BuildZipAsync(IAsyncEnumerable<ExportFile> files)
    {
        using var memoryStream = new MemoryStream();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long centralDirectoryBytes = 0;
        using (var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await foreach (var file in files)
            {
                var contentBytes = file.CopyContentBytes();
                try
                {
                    var entryName = UniqueName(file.FileName, names);
                    var centralEntryBytes = RequireAvailableArchiveCapacity(memoryStream.Length, contentBytes.Length,
                        entryName, centralDirectoryBytes);
                    // Media is already compressed; storing avoids a slow, pointless deflate pass.
                    var zipArchiveEntry = zipArchive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    await using var stream = zipArchiveEntry.Open();
                    await stream.WriteAsync(contentBytes);
                    centralDirectoryBytes += centralEntryBytes;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(contentBytes);
                    ClearOwnedContent(file);
                }
            }
        }
        return new($"gallery-export-{DateTime.Now:yyyyMMdd-HHmmss}.zip", "application/zip", memoryStream.ToArray());
    }

    public static void ClearOwnedContent(ExportFile file)
    {
        if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(file.OwnedContent, out var content))
            throw new InvalidOperationException("Export content must retain its owned array for secure cleanup.");
        CryptographicOperations.ZeroMemory(content.AsSpan());
    }

    private static long RequireAvailableArchiveCapacity(long archiveBytes, int contentBytes, string entryName,
        long centralDirectoryBytes)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetByteCount(entryName);
        var localHeaderBytes = 30L + nameBytes;
        var centralEntryBytes = 46L + nameBytes;
        if (archiveBytes + contentBytes + localHeaderBytes + centralDirectoryBytes + centralEntryBytes + 22 > MaxZipBytes)
            throw new InvalidOperationException("The selection is larger than the 1 GiB export limit. Export fewer items.");
        return centralEntryBytes;
    }

    public static ExportFile? ComposeWorkflow(ImageRecord record, string? workflow)
        => workflow is null ? null : ComposeWorkflowDocument(record.Name, workflow);

    private static ExportFile ComposeWorkflowDocument(string name, string workflow)
        => new(SafeFileName(name) + ".workflow.json", "application/json", System.Text.Encoding.UTF8.GetBytes(workflow));

    public static ExportFile Export(ImageRecord record, byte[] plain) => ComposeCleanedMedia(record, plain);

    private static ExportFile ComposeCleanedMedia(ImageRecord record, byte[] plain)
    {
        var content = ExportMediaCleaner.Clean(record.ContentType, plain);
        try
        {
            return new(ExportMediaNameFormatter.Format(record.Name, record.ContentType),
                record.ContentType, content);
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    public static string SafeFileName(string name) => ExportMediaNameFormatter.SafeFileName(name);

    private static string UniqueName(string fileName, HashSet<string> used)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;
        for (var index = 2; !used.Add(candidate); index++) candidate = $"{stem} ({index}){extension}";
        return candidate;
    }
}
