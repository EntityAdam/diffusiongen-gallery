using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Services;

public sealed partial class GalleryService(GalleryStore store, VaultStore vault, VaultSession session, VideoTools? video = null)
{
    public const long MaxFileBytes = 50 * 1024 * 1024;
    public const long MaxVideoBytes = 256 * 1024 * 1024;
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".mp4"];

    public static bool IsVideoName(string name) => string.Equals(Path.GetExtension(name), ".mp4", StringComparison.OrdinalIgnoreCase);

    public async Task<List<ImageRecord>> ListAsync()
    {
        using var key = session.Borrow();
        await store.Gate.WaitAsync();
        try { await BackfillFingerprintsAsync(key.Bytes); }
        finally { store.Gate.Release(); }
        var records = await ListWithKeyAsync(key.Bytes);
        await BackfillGenerationAsync(records, key.Bytes);
        return records;
    }

    /// <summary>Parses ComfyUI metadata once for records imported before (or with an older) parser.</summary>
    private async Task BackfillGenerationAsync(List<ImageRecord> records, byte[] key)
    {
        var stale = records.Select((record, index) => (record, index))
            .Where(item => item.record.MetadataVersion < ComfyMetadata.Version).ToList();
        if (stale.Count == 0) return;
        await store.Gate.WaitAsync();
        try
        {
            foreach (var (record, index) in stale)
            {
                try
                {
                    var fresh = await FindAsync(record.Id, key);
                    fresh.Generation = ComfyMetadata.Extract(fresh.Metadata);
                    fresh.MetadataVersion = ComfyMetadata.Version;
                    await store.UpdateAsync(fresh.Id, EncryptRecord(fresh, key));
                    records[index] = fresh;
                }
                catch (Exception exception) when (exception is InvalidOperationException or CryptographicException)
                {
                    // A record removed concurrently or unreadable stays as listed; it is retried next time.
                }
            }
        }
        finally { store.Gate.Release(); }
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
        return await ThumbnailWithKeyAsync(id, key.Bytes);
    }

    private async Task<string> ThumbnailWithKeyAsync(string id, byte[] key)
    {
        var plain = VaultCrypto.Decrypt(await store.ReadThumbnailAsync(id), key, $"thumbnail:{id}");
        try { return "data:image/png;base64," + Convert.ToBase64String(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<string> PreviewAsync(string id, bool fullResolution = false)
    {
        using var key = session.Borrow();
        var record = await FindAsync(id, key.Bytes);
        if (record.IsVideo) return await ThumbnailWithKeyAsync(id, key.Bytes);
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
        if (record.IsVideo) return await ThumbnailWithKeyAsync(id, key.Bytes);
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
        return await ImportWithKeyAsync(input, name, sourceFolder, sourcePath, key.Bytes, ensureFingerprints: true);
    }

    private async Task<ImageRecord> ImportWithKeyAsync(Stream input, string name, string sourceFolder,
        string sourcePath, byte[] key, bool ensureFingerprints)
    {
        if (!Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Supported files: PNG, JPEG, WebP and MP4.");
        var isVideo = IsVideoName(name);
        var limit = isVideo ? MaxVideoBytes : MaxFileBytes;
        using var original = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            if (original.Length + read > limit)
                throw new InvalidOperationException(isVideo ? "Video exceeds the 256 MiB limit." : "Image exceeds the 50 MiB limit.");
            await original.WriteAsync(buffer.AsMemory(0, read));
        }
        CryptographicOperations.ZeroMemory(buffer);
        var bytes = original.GetBuffer().AsMemory(0, (int)original.Length);
        try
        {
            var sourceHash = Convert.ToHexString(SHA256.HashData(bytes.Span));
            var fingerprint = VaultCrypto.ContentFingerprint(key, sourceHash);
            await store.Gate.WaitAsync();
            try
            {
                if (ensureFingerprints) await BackfillFingerprintsAsync(key);
                if (await store.ContainsFingerprintAsync(fingerprint))
                    throw new InvalidOperationException($"'{name}' is already in the gallery.");
            }
            finally { store.Gate.Release(); }
            var (record, encrypted, encryptedThumb) = isVideo
                ? await PrepareVideoAsync(bytes, name, sourceFolder, sourcePath, sourceHash, key)
                : await PrepareImageAsync(original, name, sourceFolder, sourcePath, sourceHash, key);
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
                try { await store.InsertAsync(record.Id, EncryptRecord(record, key), encryptedThumb, fingerprint); }
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

    private async Task<(ImageRecord Record, byte[] Encrypted, byte[] EncryptedThumb)> PrepareImageAsync(
        MemoryStream original, string name, string sourceFolder, string sourcePath, string sourceHash, byte[] key)
    {
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
        await image.SaveAsPngAsync(png, new PngEncoder { CompressionLevel = PngCompressionLevel.Level1 });
        var thumb = await CreateThumbnailAsync(image);
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
            Metadata = metadata,
            Generation = ComfyMetadata.Extract(metadata),
            MetadataVersion = ComfyMetadata.Version
        };
        record.StoredPath = Path.Combine(vault.Root, "images", record.Id[..2], record.Id[2..4], record.Id + ".dpng");
        try
        {
            return (record,
                VaultCrypto.Encrypt(png.GetBuffer().AsSpan(0, (int)png.Length), key, $"image:{record.Id}"),
                VaultCrypto.Encrypt(thumb, key, $"thumbnail:{record.Id}"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(png.GetBuffer());
            CryptographicOperations.ZeroMemory(thumb);
        }
    }

    /// <summary>
    /// ffprobe/ffmpeg need a file, so the plaintext MP4 is written briefly to a randomly named temp file
    /// that is deleted as soon as the metadata and first frame are read.
    /// </summary>
    private async Task<(ImageRecord Record, byte[] Encrypted, byte[] EncryptedThumb)> PrepareVideoAsync(
        ReadOnlyMemory<byte> bytes, string name, string sourceFolder, string sourcePath, string sourceHash, byte[] key)
    {
        if (video is null || !video.IsAvailable) throw new InvalidOperationException(VideoTools.MissingMessage);
        var temp = Path.Combine(Path.GetTempPath(), "gallery-" + Guid.NewGuid().ToString("N") + ".mp4");
        VideoTools.ProbeResult probe;
        byte[] frame;
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(bytes);
            probe = await video.ProbeAsync(temp);
            if (!probe.FormatName.Split(',').Any(format => format is "mp4" or "mov"))
                throw new InvalidOperationException("File is not a valid MP4 video.");
            if (probe.Width <= 0 || probe.Height <= 0) throw new InvalidOperationException("No video stream was found.");
            if (probe.Width > 16_384 || probe.Height > 16_384)
                throw new InvalidOperationException("Video exceeds the 16384 pixel dimension limit.");
            frame = await video.FirstFrameAsync(temp);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        byte[] thumb;
        try
        {
            using var image = Image.Load(frame);
            thumb = await CreateThumbnailAsync(image);
        }
        finally { CryptographicOperations.ZeroMemory(frame); }
        var metadata = JsonSerializer.Serialize(new
        {
            video = probe.Tags.Select(tag => new { Keyword = tag.Key, Value = tag.Value })
        });
        var record = new ImageRecord
        {
            Name = Path.GetFileNameWithoutExtension(name),
            OriginalName = Path.GetFileName(name),
            SourceFolder = sourceFolder,
            SourcePath = sourcePath,
            Width = probe.Width, Height = probe.Height,
            OriginalBytes = bytes.Length,
            Sha256 = sourceHash,
            PngSha256 = sourceHash,
            Metadata = metadata,
            MediaType = "video",
            ContentType = "video/mp4",
            DurationSeconds = probe.DurationSeconds,
            Generation = ComfyMetadata.Extract(metadata),
            MetadataVersion = ComfyMetadata.Version
        };
        record.StoredPath = Path.Combine(vault.Root, "images", record.Id[..2], record.Id[2..4], record.Id + ".dmp4");
        try
        {
            return (record, VaultCrypto.Encrypt(bytes.Span, key, $"image:{record.Id}"),
                VaultCrypto.Encrypt(thumb, key, $"thumbnail:{record.Id}"));
        }
        finally { CryptographicOperations.ZeroMemory(thumb); }
    }

    private static async Task<byte[]> CreateThumbnailAsync(Image image)
    {
        using var thumbnail = image.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new Size(384, 384), Mode = ResizeMode.Max
        }));
        thumbnail.Metadata.ExifProfile = null;
        thumbnail.Metadata.IccProfile = null;
        thumbnail.Metadata.XmpProfile = null;
        thumbnail.Metadata.GetPngMetadata().TextData.Clear();
        using var thumb = new MemoryStream();
        await thumbnail.SaveAsPngAsync(thumb, new PngEncoder
        {
            SkipMetadata = true, CompressionLevel = PngCompressionLevel.Level1
        });
        var result = thumb.ToArray();
        CryptographicOperations.ZeroMemory(thumb.GetBuffer());
        return result;
    }

    public async Task<List<string>> ImportFolderAsync(string folder, bool recursive, Action<string>? progress = null,
        bool deleteSources = false, Func<ImportProgress, Task>? reportProgress = null)
    {
        using var key = session.Borrow();
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing local folder.");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        if (reportProgress is not null)
            await reportProgress(new ImportProgress(null, 0, 0, 0, "Scanning selected folder..."));
        var paths = await Task.Run(() => Directory.EnumerateFiles(folder, "*", options).Where(path =>
            Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray());
        await store.Gate.WaitAsync();
        try { await BackfillFingerprintsAsync(key.Bytes); }
        finally { store.Gate.Release(); }
        var failures = new string?[paths.Length];
        var completed = 0;
        var importedCount = 0;
        var failedCount = 0;
        using var progressGate = new SemaphoreSlim(1);

        async Task ReportAsync(string status, bool finished = false, bool imported = false, bool failed = false)
        {
            await progressGate.WaitAsync();
            try
            {
                if (finished) completed++;
                if (imported) importedCount++;
                if (failed) failedCount++;
                if (reportProgress is not null)
                    await reportProgress(new ImportProgress(paths.Length, completed, importedCount, failedCount, status));
            }
            finally { progressGate.Release(); }
        }

        await ReportAsync("Importing images...");
        // Two workers overlap decode/encode and I/O without multiplying full-size image memory by every CPU.
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(2, Environment.ProcessorCount) }, async (index, _) =>
        {
            var path = paths[index];
            var name = Path.GetFileName(path);
            await progressGate.WaitAsync();
            try { progress?.Invoke(name); }
            finally { progressGate.Release(); }
            await ReportAsync($"Importing {name}...");
            var importedSuccessfully = false;
            try
            {
                ImageRecord imported;
                await using (var input = File.OpenRead(path))
                {
                    imported = await ImportWithKeyAsync(input, name, Path.GetDirectoryName(path)!, path,
                        key.Bytes, ensureFingerprints: false);
                    importedSuccessfully = true;
                    if (deleteSources)
                    {
                        await ReportAsync($"Verifying {name} before source deletion...");
                        await VerifyImportedWithKeyAsync(imported.Id, key.Bytes);
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
                failures[index] = $"{name}: {exception.Message}";
            }
            await ReportAsync($"Processed {name}", finished: true, imported: importedSuccessfully,
                failed: failures[index] is not null);
        });
        await ReportAsync(paths.Length == 0 ? "No supported images found." : "Ingestion complete.");
        return failures.OfType<string>().ToList();
    }

    public async Task VerifyImportedAsync(string id)
    {
        using var key = session.Borrow();
        await VerifyImportedWithKeyAsync(id, key.Bytes);
    }

    private async Task VerifyImportedWithKeyAsync(string id, byte[] key)
    {
        var record = await FindAsync(id, key);
        var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key, $"image:{id}");
        try
        {
            if (record.PngSha256.Length == 0 || Convert.ToHexString(SHA256.HashData(plain)) != record.PngSha256)
                throw new CryptographicException("Saved file did not match the imported content.");
            if (!record.IsVideo)
            {
                using var image = Image.Load(plain);
                if (image.Width != record.Width || image.Height != record.Height)
                    throw new CryptographicException("Saved image dimensions did not match the catalog.");
            }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var thumbnail = VaultCrypto.Decrypt(await store.ReadThumbnailAsync(id), key, $"thumbnail:{id}");
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

    public async Task<int> UpdateManyAsync(IEnumerable<string> ids, Action<ImageRecord> update)
    {
        using var key = session.Borrow();
        var failed = 0;
        await store.Gate.WaitAsync();
        try
        {
            foreach (var id in ids.Distinct())
            {
                try
                {
                    var record = await FindAsync(id, key.Bytes);
                    update(record);
                    await store.UpdateAsync(id, EncryptRecord(record, key.Bytes));
                }
                catch (Exception exception) when (exception is InvalidOperationException or CryptographicException or IOException)
                {
                    failed++;
                }
            }
        }
        finally { store.Gate.Release(); }
        return failed;
    }

    public async Task MoveAsync(string id, string folder, string name)
    {
        using var key = session.Borrow();
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' ') || name.Contains('/') || name.Contains('\\'))
            throw new InvalidOperationException("Enter a valid filename without a path or extension.");
        if (name.EndsWith(".dpng", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dmp4", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        if (name.Length == 0) throw new InvalidOperationException("A filename is required.");
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing destination folder.");
        await store.Gate.WaitAsync();
        try
        {
            var record = await FindAsync(id, key.Bytes);
            var target = Path.Combine(folder, name + (record.IsVideo ? ".dmp4" : ".dpng"));
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
