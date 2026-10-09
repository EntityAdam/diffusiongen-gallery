using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    public const long MaxExportZipBytes = 1024L * 1024 * 1024;

    /// <summary>Decrypts the stored MP4 for playback in the browser.</summary>
    public async Task<(Stream Content, string ContentType)> OpenMediaAsync(string id)
    {
        using var key = session.Borrow();
        var record = await FindAsync(id, key.Bytes);
        var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key.Bytes, $"image:{id}");
        return (new MemoryStream(plain, writable: false), record.ContentType);
    }

    /// <summary>A decrypted copy for saving outside the vault: the MP4 as imported, or the image as PNG with its metadata.</summary>
    public async Task<ExportFile> ExportAsync(string id)
    {
        using var key = session.Borrow();
        return await ExportWithKeyAsync(await FindAsync(id, key.Bytes), key.Bytes);
    }

    public async Task<ExportFile> ExportZipAsync(IEnumerable<string> ids)
    {
        using var key = session.Borrow();
        using var zip = new MemoryStream();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                var file = await ExportWithKeyAsync(await FindAsync(id, key.Bytes), key.Bytes);
                try
                {
                    if (zip.Length + file.Content.Length > MaxExportZipBytes)
                        throw new InvalidOperationException("The selection is larger than the 1 GiB export limit. Export fewer items.");
                    var entryName = UniqueName(file.FileName, names);
                    // Media is already compressed; storing avoids a slow, pointless deflate pass.
                    var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    await using var output = entry.Open();
                    await output.WriteAsync(file.Content);
                }
                finally { CryptographicOperations.ZeroMemory(file.Content); }
            }
        }
        return new($"gallery-export-{DateTime.Now:yyyyMMdd-HHmmss}.zip", "application/zip", zip.ToArray());
    }

    /// <summary>The embedded ComfyUI workflow (or API prompt) as JSON, or null when none was found.</summary>
    public async Task<ExportFile?> WorkflowAsync(string id)
    {
        using var key = session.Borrow();
        var record = await FindAsync(id, key.Bytes);
        var json = ComfyMetadata.WorkflowForExport(record.Metadata);
        return json is null ? null
            : new(SafeFileName(record.Name) + ".workflow.json", "application/json", System.Text.Encoding.UTF8.GetBytes(json));
    }

    private static async Task<ExportFile> ExportWithKeyAsync(ImageRecord record, byte[] key)
    {
        var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key, $"image:{record.Id}");
        if (record.IsVideo) return new(SafeFileName(record.Name) + ".mp4", record.ContentType, plain);
        try
        {
            using var image = Image.Load(plain);
            using var output = new MemoryStream();
            // Uncompressed tEXt chunks keep the prompt/workflow readable by ComfyUI when the PNG is dropped back in.
            await image.SaveAsPngAsync(output, new PngEncoder { TextCompressionThreshold = int.MaxValue });
            var content = output.ToArray();
            CryptographicOperations.ZeroMemory(output.GetBuffer());
            return new(SafeFileName(record.Name) + ".png", "image/png", content);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.');
        return safe.Length == 0 ? "image" : safe.Length > 150 ? safe[..150] : safe;
    }

    private static string UniqueName(string fileName, HashSet<string> used)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;
        for (var index = 2; !used.Add(candidate); index++) candidate = $"{stem} ({index}){extension}";
        return candidate;
    }
}
