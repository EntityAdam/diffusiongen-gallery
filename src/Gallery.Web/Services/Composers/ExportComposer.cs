using Gallery.Web.Services.Processors;
using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Web.Exceptions;
using Gallery.Web.Models;

namespace Gallery.Web.Services.Composers;

internal static class ExportComposer
{
    internal const long MaxZipBytes = 1024L * 1024 * 1024;
    public static async Task<WebExportFile> ComposeZipAsync(IAsyncEnumerable<WebExportFile> files)
        => await BuildZipAsync(files);

    private static async Task<WebExportFile> BuildZipAsync(IAsyncEnumerable<WebExportFile> files)
    {
        using var zip = new MemoryStream();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long centralDirectoryBytes = 0;
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await foreach (var file in files)
            {
                var contentBytes = file.CopyContentBytes();
                try
                {
                    var entryName = UniqueName(file.FileName, names);
                    var centralEntryBytes = RequireAvailableArchiveCapacity(zip.Length, contentBytes.Length,
                        entryName, centralDirectoryBytes);
                    // Media is already compressed; storing avoids a slow, pointless deflate pass.
                    var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    await using var output = entry.Open();
                    await output.WriteAsync(contentBytes);
                    centralDirectoryBytes += centralEntryBytes;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(contentBytes);
                    ClearOwnedContent(file);
                }
            }
        }
        return new($"gallery-export-{DateTime.Now:yyyyMMdd-HHmmss}.zip", "application/zip", zip.ToArray());
    }

    public static void ClearOwnedContent(WebExportFile file)
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

    public static async Task<(Stream Content, string ContentType)> OpenMediaAsync(WebImageRecord record, byte[] key)
        => (new MemoryStream(await ReadMediaAsync(record, key), writable: false), record.ContentType);

    public static WebExportFile? ComposeWorkflow(WebImageRecord record, string? workflow)
        => workflow is null ? null : ComposeWorkflowDocument(record.Name, workflow);

    private static WebExportFile ComposeWorkflowDocument(string name, string workflow)
        => new(SafeFileName(name) + ".workflow.json", "application/json", System.Text.Encoding.UTF8.GetBytes(workflow));

    private static async Task<byte[]> ReadMediaAsync(WebImageRecord record, byte[] key)
        => VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key, $"image:{record.Id}");

    public static async Task<WebExportFile> ExportAsync(WebImageRecord record, byte[] key)
    {
        var plain = await ReadMediaAsync(record, key);
        try { return ComposeCleanedMedia(record, plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static WebExportFile ComposeCleanedMedia(WebImageRecord record, byte[] plain)
    {
        var content = ExportMediaCleaner.Clean(record.ContentType, plain);
        try
        {
            return new(SafeFileName(record.Name) + ImageContentFormats.FileExtension(record.ContentType),
                record.ContentType, content);
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    public static string SafeFileName(string name) => LimitFileName(SanitizeFileName(name));

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.');
        return safe;
    }

    private static string LimitFileName(string name) => name.Length == 0 ? "image" : name.Length > 150 ? name[..150] : name;

    private static string UniqueName(string fileName, HashSet<string> used)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;
        for (var index = 2; !used.Add(candidate); index++) candidate = $"{stem} ({index}){extension}";
        return candidate;
    }
}
