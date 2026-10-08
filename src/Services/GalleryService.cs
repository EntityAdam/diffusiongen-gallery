using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Services;

public sealed partial class GalleryService(GalleryStore store, VaultStore vault, VaultSession session)
{
    public const long MaxFileBytes = 50 * 1024 * 1024;
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    public async Task<List<ImageRecord>> ListAsync()
    {
        using var key = session.Borrow();
        await store.Gate.WaitAsync();
        try { await BackfillFingerprintsAsync(key.Bytes); }
        finally { store.Gate.Release(); }
        return await ListWithKeyAsync(key.Bytes);
    }

    private async Task<List<ImageRecord>> ListWithKeyAsync(byte[] key)
    {
        var result = new List<ImageRecord>();
        foreach (var (id, payload) in await store.ReadRecordsAsync())
        {
            result.Add(DecryptRecord(id, payload, key));
        }
        return result;
    }

    private static ImageRecord DecryptRecord(string id, byte[] payload, byte[] key)
    {
        var plain = VaultCrypto.Decrypt(payload, key, $"record:{id}");
        try
        {
            var record = JsonSerializer.Deserialize<ImageRecord>(plain)
                ?? throw new InvalidOperationException("Invalid image record.");
            if (record.Id != id) throw new CryptographicException("Image record identity mismatch.");
            return record;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private Task BackfillFingerprintsAsync(byte[] key) => store.BackfillFingerprintsAsync((id, payload) =>
        VaultCrypto.ContentFingerprint(key, DecryptRecord(id, payload, key).Sha256));

    public async Task<string> ThumbnailAsync(string id)
    {
        using var key = session.Borrow();
        var plain = VaultCrypto.Decrypt(await store.ReadThumbnailAsync(id), key.Bytes, $"thumbnail:{id}");
        try { return "data:image/png;base64," + Convert.ToBase64String(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<string> PreviewAsync(string id, bool fullResolution = false)
    {
        using var key = session.Borrow();
        var record = await FindAsync(id, key.Bytes);
        var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key.Bytes, $"image:{id}");
        try
        {
            using var image = Image.Load(plain);
            if (!fullResolution)
                image.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(1600, 1600), Mode = ResizeMode.Max }));
            using var preview = new MemoryStream();
            await image.SaveAsPngAsync(preview, new PngEncoder { SkipMetadata = true });
            var result = "data:image/png;base64," + Convert.ToBase64String(preview.GetBuffer(), 0, (int)preview.Length);
            CryptographicOperations.ZeroMemory(preview.GetBuffer());
            return result;
        }

        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<string> ViewerImageAsync(string id, bool fullResolution)
    {
        using var key = session.Borrow();
        var record = await FindAsync(id, key.Bytes);
        var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key.Bytes, $"image:{id}");
        try
        {
            if (fullResolution)
                return "data:image/png;base64," + Convert.ToBase64String(plain);
            using var image = Image.Load(plain);
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(1600, 1600), Mode = ResizeMode.Max
            }).BackgroundColor(Color.Black));
            using var preview = new MemoryStream();
            try
            {
                await image.SaveAsJpegAsync(preview, new JpegEncoder { Quality = 85, SkipMetadata = true });
                return "data:image/jpeg;base64," + Convert.ToBase64String(preview.GetBuffer(), 0, (int)preview.Length);
            }
            finally { CryptographicOperations.ZeroMemory(preview.GetBuffer()); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<ImageRecord> ImportAsync(Stream input, string name, string sourceFolder, string sourcePath = "")
    {
        using var key = session.Borrow();
        if (!Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Supported images: PNG, JPEG and WebP.");
        using var original = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            if (original.Length + read > MaxFileBytes) throw new InvalidOperationException("Image exceeds the 50 MiB limit.");
            await original.WriteAsync(buffer.AsMemory(0, read));
        }
        CryptographicOperations.ZeroMemory(buffer);
        var bytes = original.GetBuffer().AsMemory(0, (int)original.Length);
        try
        {
            var sourceHash = Convert.ToHexString(SHA256.HashData(bytes.Span));
            var fingerprint = VaultCrypto.ContentFingerprint(key.Bytes, sourceHash);
            await store.Gate.WaitAsync();
            try
            {
                await BackfillFingerprintsAsync(key.Bytes);
                if (await store.ContainsFingerprintAsync(fingerprint))
                    throw new InvalidOperationException($"'{name}' is already in the gallery.");
            }
            finally { store.Gate.Release(); }
            original.Position = 0;
            var info = await Image.IdentifyAsync(original);
            if ((long)info.Width * info.Height > 40_000_000 || info.Width > 16_384 || info.Height > 16_384)
                throw new InvalidOperationException("Image exceeds the 40 megapixel / 16384 pixel dimension limit.");
            original.Position = 0;
            using var image = await Image.LoadAsync(original);
            if (image.Frames.Count != 1) throw new InvalidOperationException("Animated images are not supported.");
            var metadata = JsonSerializer.Serialize(new
            {
                png = image.Metadata.GetPngMetadata().TextData.Select(chunk => new { chunk.Keyword, chunk.Value }),
                exif = image.Metadata.ExifProfile?.Values.Select(value => $"{value.Tag}: {value.GetValue()}")
            });
            using var png = new MemoryStream();
            await image.SaveAsPngAsync(png);
            using var thumbnail = image.Clone(context => context.Resize(new ResizeOptions
            {
                Size = new Size(384, 384), Mode = ResizeMode.Max
            }));
            thumbnail.Metadata.ExifProfile = null;
            thumbnail.Metadata.IccProfile = null;
            thumbnail.Metadata.XmpProfile = null;
            thumbnail.Metadata.GetPngMetadata().TextData.Clear();
            using var thumb = new MemoryStream();
            await thumbnail.SaveAsPngAsync(thumb, new PngEncoder { SkipMetadata = true });
            var record = new ImageRecord
            {
                Name = Path.GetFileNameWithoutExtension(name),
                OriginalName = Path.GetFileName(name),
                SourceFolder = sourceFolder,
                SourcePath = sourcePath,
                Width = image.Width, Height = image.Height,
                OriginalBytes = original.Length,
                Sha256 = sourceHash,
                PngSha256 = Convert.ToHexString(SHA256.HashData(png.GetBuffer().AsSpan(0, (int)png.Length))),
                Metadata = metadata
            };
            record.StoredPath = Path.Combine(vault.Root, "images", record.Id[..2], record.Id[2..4], record.Id + ".dpng");
            var encrypted = VaultCrypto.Encrypt(png.GetBuffer().AsSpan(0, (int)png.Length), key.Bytes, $"image:{record.Id}");
            var encryptedThumb = VaultCrypto.Encrypt(thumb.GetBuffer().AsSpan(0, (int)thumb.Length), key.Bytes, $"thumbnail:{record.Id}");
            CryptographicOperations.ZeroMemory(png.GetBuffer());
            CryptographicOperations.ZeroMemory(thumb.GetBuffer());
            await store.Gate.WaitAsync();
            try
            {
                if (await store.ContainsFingerprintAsync(fingerprint))
                    throw new InvalidOperationException($"'{name}' is already in the gallery.");
                Directory.CreateDirectory(Path.GetDirectoryName(record.StoredPath)!);
                await using (var output = new FileStream(record.StoredPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await output.WriteAsync(encrypted);
                    output.Flush(flushToDisk: true);
                }
                try { await store.InsertAsync(record.Id, EncryptRecord(record, key.Bytes), encryptedThumb, fingerprint); }
                catch
                {
                    File.Delete(record.StoredPath);
                    throw;
                }
                return record;
            }
            finally { store.Gate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes.Span); }
    }

    public async Task<List<string>> ImportFolderAsync(string folder, bool recursive, Action<string>? progress = null,
        bool deleteSources = false)
    {
        if (!session.IsUnlocked) throw new InvalidOperationException("Unlock the vault first.");
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing local folder.");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var failures = new List<string>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", options).Where(path =>
            Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
        {
            progress?.Invoke(Path.GetFileName(path));
            try
            {
                ImageRecord imported;
                await using (var input = File.OpenRead(path))
                {
                    imported = await ImportAsync(input, Path.GetFileName(path), Path.GetDirectoryName(path)!, path);
                    if (deleteSources)
                    {
                        await VerifyImportedAsync(imported.Id);
                        input.Position = 0;
                        var currentHash = Convert.ToHexString(await SHA256.HashDataAsync(input));
                        if (currentHash != imported.Sha256)
                            throw new InvalidOperationException("Source changed during import; it was not deleted.");
                    }
                }
                if (deleteSources)
                {
                    try { File.Delete(path); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException("Encrypted import succeeded, but source deletion failed. The source was retained.", exception);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or UnauthorizedAccessException or CryptographicException or UnknownImageFormatException or InvalidImageContentException)
            {
                failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
        return failures;
    }

    public async Task VerifyImportedAsync(string id)
    {
        using var key = session.Borrow();
        var record = await FindAsync(id, key.Bytes);
        var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key.Bytes, $"image:{id}");
        try
        {
            if (record.PngSha256.Length == 0 || Convert.ToHexString(SHA256.HashData(plain)) != record.PngSha256)
                throw new CryptographicException("Saved image did not match the imported PNG.");
            using var image = Image.Load(plain);
            if (image.Width != record.Width || image.Height != record.Height)
                throw new CryptographicException("Saved image dimensions did not match the catalog.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var thumbnail = VaultCrypto.Decrypt(await store.ReadThumbnailAsync(id), key.Bytes, $"thumbnail:{id}");
        try { using var image = Image.Load(thumbnail); }
        finally { CryptographicOperations.ZeroMemory(thumbnail); }
    }

    public async Task UpdateAsync(string id, Action<ImageRecord> update)
    {
        using var key = session.Borrow();
        await store.Gate.WaitAsync();
        try
        {
            var record = await FindAsync(id, key.Bytes);
            update(record);
            await store.UpdateAsync(id, EncryptRecord(record, key.Bytes));
        }
        finally { store.Gate.Release(); }
    }

    public async Task MoveAsync(string id, string folder, string name)
    {
        using var key = session.Borrow();
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' ') || name.Contains('/') || name.Contains('\\'))
            throw new InvalidOperationException("Enter a valid filename without a path or extension.");
        if (name.EndsWith(".dpng", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        if (name.Length == 0) throw new InvalidOperationException("A filename is required.");
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing destination folder.");
        await store.Gate.WaitAsync();
        try
        {
            var record = await FindAsync(id, key.Bytes);
            var target = Path.Combine(folder, name + ".dpng");
            if (string.Equals(record.StoredPath, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a different name or destination.");
            var source = record.StoredPath;
            // Copy first: a crash may leave a ciphertext duplicate, but cannot lose the sole image.
            File.Copy(source, target, overwrite: false);
            record.StoredPath = target;
            record.Name = name;
            try { await store.UpdateAsync(id, EncryptRecord(record, key.Bytes)); }
            catch
            {
                File.Delete(target);
                throw;
            }
            try { File.Delete(source); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"The image was moved to '{target}', but the old encrypted copy at '{source}' could not be removed. Remove that extra copy after checking the destination.", exception);
            }
        }
        finally { store.Gate.Release(); }
    }

    public async Task<VisionSettings> SettingsAsync()
    {
        using var key = session.Borrow();
        var encrypted = await store.ReadSettingsAsync();
        if (encrypted is null) return new();
        var plain = VaultCrypto.Decrypt(encrypted, key.Bytes, "vision-settings");
        try
        {
            return JsonSerializer.Deserialize<VisionSettings>(plain)
                ?? throw new InvalidOperationException("Invalid vision settings.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveSettingsAsync(VisionSettings settings)
    {
        VisionService.Validate(settings);
        using var key = session.Borrow();
        var plain = JsonSerializer.SerializeToUtf8Bytes(settings);
        try { await store.SaveSettingsAsync(VaultCrypto.Encrypt(plain, key.Bytes, "vision-settings")); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<ImageRecord> FindAsync(string id, byte[] key) =>
        DecryptRecord(id, await store.ReadRecordAsync(id), key);

    private static byte[] EncryptRecord(ImageRecord record, byte[] key)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(record);
        try { return VaultCrypto.Encrypt(plain, key, $"record:{record.Id}"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
