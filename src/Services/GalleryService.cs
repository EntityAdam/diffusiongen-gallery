using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core;
using Gallery.Core.Queries;
using Gallery.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    private const string RatingRangeMessageFormat = "Rating {0} must be between 0 and 5.";
    private const string EmptyDuplicateCandidatesMessage =
        "Cannot choose a duplicate keeper by deletion status, rating, favorite status, dimensions, original size, and import time: candidate count was 0.";

    private readonly GalleryStore store;
    private readonly VaultStore vault;
    private readonly VaultSession session;
    private readonly VideoTools? video;
    private readonly VisionService? vision;
    private readonly LibraryService? library;

    private static class GalleryImageRenderer
    {
        public static async Task<string> CreatePreviewAsync(
            ImageRecord record, byte[] key, bool shouldUseFullResolution)
        {
            var plainImage = await DecryptStoredImageAsync(record, key);
            try
            {
                using var image = Image.Load(plainImage);
                if (!shouldUseFullResolution)
                    image.Mutate(context => context.Resize(new ResizeOptions
                    {
                        Size = new Size(1600, 1600), Mode = ResizeMode.Max
                    }));
                using var preview = new MemoryStream();
                await image.SaveAsPngAsync(preview, new PngEncoder { SkipMetadata = true });
                var dataUrl = "data:image/png;base64," +
                    Convert.ToBase64String(preview.GetBuffer(), 0, (int)preview.Length);
                CryptographicOperations.ZeroMemory(preview.GetBuffer());
                return dataUrl;
            }
            finally { CryptographicOperations.ZeroMemory(plainImage); }
        }

        public static async Task<string> CreateViewerImageAsync(
            ImageRecord record, byte[] key, bool shouldUseFullResolution)
        {
            var plainImage = await DecryptStoredImageAsync(record, key);
            try
            {
                if (shouldUseFullResolution)
                    return "data:image/png;base64," + Convert.ToBase64String(plainImage);

                using var image = Image.Load(plainImage);
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(1600, 1600), Mode = ResizeMode.Max
                }).BackgroundColor(Color.Black));
                using var preview = new MemoryStream();
                try
                {
                    await image.SaveAsJpegAsync(preview, new JpegEncoder { Quality = 85, SkipMetadata = true });
                    return "data:image/jpeg;base64," +
                        Convert.ToBase64String(preview.GetBuffer(), 0, (int)preview.Length);
                }
                finally { CryptographicOperations.ZeroMemory(preview.GetBuffer()); }
            }
            finally { CryptographicOperations.ZeroMemory(plainImage); }
        }

        private static async Task<byte[]> DecryptStoredImageAsync(ImageRecord record, byte[] key) =>
            VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key, $"image:{record.Id}");
    }

    internal GalleryService(GalleryStore store, VaultStore vault, VaultSession session, VideoTools? video = null,
        VisionService? vision = null, LibraryService? library = null)
    {
        this.store = store;
        this.vault = vault;
        this.session = session;
        this.video = video;
        this.vision = vision;
        this.library = library;
    }

    public const long MaxFileBytes = 50 * 1024 * 1024;
    public const long MaxVideoBytes = 256 * 1024 * 1024;
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".mp4"];

    public static bool IsVideoName(string name) => string.Equals(Path.GetExtension(name), ".mp4", StringComparison.OrdinalIgnoreCase);

    public List<ImageRecord> Filter(IEnumerable<ImageRecord> records, GalleryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(filter);
        return GalleryFilterProcessor.Apply(filter, records).ToList();
    }

    public List<PromptGroup> GroupByPrompt(IEnumerable<ImageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return ComfyMetadata.GroupByPrompt(records);
    }

    public ImageRecord PickDuplicateKeeper(IEnumerable<ImageRecord> records)
    {
        var candidates = RequireDuplicateCandidates(records);
        return DuplicateRules.PickKeeper(candidates);
    }

    public HashSet<string> InitialDuplicateKeepers(IReadOnlyCollection<ImageRecord> group)
    {
        var candidates = RequireDuplicateCandidates(group);
        return DuplicateRules.InitialKeepers(candidates);
    }

    private static List<ImageRecord> RequireDuplicateCandidates(IEnumerable<ImageRecord> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var candidateRecords = candidates.ToList();
        if (candidateRecords.Count == 0)
            throw new InvalidOperationException(EmptyDuplicateCandidatesMessage);
        return candidateRecords;
    }

    public void ApplyReviewDecision(ImageRecordEdit image, ReviewAction action, int rating = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!Enum.IsDefined(action))
            throw new InvalidOperationException(
                $"Review action value {(int)action} is invalid; choose Keep, Favorite, MarkDeletion, or Rate.");
        if (action == ReviewAction.Rate && rating is < 1 or > 5)
            throw new InvalidOperationException($"Rating {rating} must be between 1 and 5.");
        ReviewDecision.Apply(image, action, rating);
    }

    public ReviewState CaptureReviewState(ImageRecord image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ReviewState.Capture(image);
    }

    public void RestoreReviewState(ImageRecordEdit image, ReviewState state)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(state);
        image.Reviewed = state.Reviewed;
        image.Favorite = state.Favorite;
        image.MarkedForDeletion = state.MarkedForDeletion;
        image.Rating = state.Rating;
    }

    public void SetCuration(ImageRecordEdit image, int? rating, bool? reviewed)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (rating is < 0 or > 5)
            throw new InvalidOperationException(string.Format(
                System.Globalization.CultureInfo.InvariantCulture, RatingRangeMessageFormat, rating));
        LibraryRules.SetCuration(image, rating, reviewed);
    }

    public void SetOrganization(ImageRecordEdit image, int rating, bool reviewed, string? collection)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (rating is < 0 or > 5)
            throw new InvalidOperationException(string.Format(
                System.Globalization.CultureInfo.InvariantCulture, RatingRangeMessageFormat, rating));
        if (IsInvalidCollectionName(collection))
            throw new InvalidOperationException("Collection names must have 1 to 80 characters.");
        LibraryRules.SetOrganization(image, rating, reviewed, collection);
    }

    private static bool IsInvalidCollectionName(string? collection) =>
        collection is not null && (string.IsNullOrWhiteSpace(collection) || collection.Trim().Length > 80);

    public string MergeTags(string existing, string additions) => LibraryRules.MergeTags(existing, additions);

    internal Task<List<SavedView>> LoadViewsAsync() => RequiredLibrary().LoadViewsAsync();
    internal Task SaveViewAsync(string name, GalleryFilter filter) => RequiredLibrary().SaveViewAsync(name, filter);
    internal Task DeleteViewAsync(string name) => RequiredLibrary().DeleteViewAsync(name);
    public Task<IReadOnlyList<string>> GetVisionModelsAsync(VisionSettings settings) =>
        RequiredVision().GetModelsAsync(settings);
    public Task<(string Description, string Tags)> AnalyzeImageAsync(VisionSettings settings, string imageData) =>
        RequiredVision().AnalyzeAsync(settings, imageData);
    public Task<GalleryFilter> InterpretSearchAsync(VisionSettings settings, string query, IEnumerable<string> collections) =>
        RequiredVision().InterpretSearchAsync(settings, query, collections);
    public Task<(string Description, string Tags, string Collection, string Warning)> AnalyzeImageWithCollectionAsync(
        VisionSettings settings, string imageData, IEnumerable<string> collections) =>
        RequiredVision().AnalyzeImageWithCollectionAsync(settings, imageData, collections);

    private LibraryService RequiredLibrary() => library
        ?? throw new InvalidOperationException("The library manager is not configured.");

    private VisionService RequiredVision() => vision
        ?? throw new InvalidOperationException("The vision manager is not configured.");

    public static string FormatDuration(ImageRecord image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var duration = TimeSpan.FromSeconds(Math.Max(0, Math.Round(image.DurationSeconds)));
        return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes:N0} B")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }

    public async Task<List<ImageRecord>> ListAsync()
    {
        using var key = session.Borrow();
        await store.Gate.WaitAsync();
        try
        {
            await RecoverInterruptedRemovalsAsync(key.Bytes);
            await BackfillFingerprintsAsync(key.Bytes);
        }
        finally { store.Gate.Release(); }
        var records = await ListWithKeyAsync(key.Bytes);
        await BackfillGenerationAsync(records, key.Bytes);
        return records;
    }

    private async Task RecoverInterruptedRemovalsAsync(byte[] key)
    {
        var records = await ListWithKeyAsync(key);
        var stagedPathsByStoredPath = records.Select(record => Path.GetDirectoryName(record.StoredPath)!)
            .Distinct(StringComparer.Ordinal).Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory))
            .Where(IsRemovalStagingFile)
            .ToLookup(RemovalOriginalPath, StringComparer.Ordinal);
        foreach (var record in records)
            await RecoverStagedRemovalAsync(record, stagedPathsByStoredPath[record.StoredPath].ToArray());

        var imageRoot = Path.Combine(vault.Root, "images");
        IEnumerable<string> stagedRemovals = Directory.Exists(imageRoot)
            ? Directory.EnumerateFiles(imageRoot, "*.removing-*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        foreach (var path in stagedRemovals)
        {
            if (IsRemovalStagingFile(path) && !records.Any(record =>
                string.Equals(record.StoredPath, RemovalOriginalPath(path), StringComparison.OrdinalIgnoreCase)))
                File.Delete(path);
        }
    }

    private static Task RecoverRecordRemovalAsync(ImageRecord record)
    {
        var directory = Path.GetDirectoryName(record.StoredPath)!;
        if (!Directory.Exists(directory)) return Task.CompletedTask;
        var stagingPrefix = record.StoredPath + ".removing-";
        var stagedPaths = Directory.EnumerateFiles(directory)
            .Where(path => path.StartsWith(stagingPrefix, StringComparison.Ordinal) && IsRemovalStagingFile(path))
            .ToArray();
        return RecoverStagedRemovalAsync(record, stagedPaths);
    }

    private static async Task RecoverStagedRemovalAsync(ImageRecord record, string[] stagedPaths)
    {
        if (stagedPaths.Length == 0) return;

        var storedImageExists = SourceExists(record.StoredPath);
        var comparisonPath = storedImageExists ? record.StoredPath : stagedPaths[0];
        foreach (var stagedPath in stagedPaths)
        {
            if (!SourceExists(stagedPath) || !await FilesMatchAsync(stagedPath, comparisonPath))
                throw new IOException($"Interrupted removal left conflicting encrypted copies for image '{record.Id}'; all copies were preserved.");
        }

        if (!storedImageExists) File.Move(stagedPaths[0], record.StoredPath);
        foreach (var stagedPath in stagedPaths)
        {
            if (storedImageExists || stagedPath != stagedPaths[0]) File.Delete(stagedPath);
        }
    }

    private static bool IsRemovalStagingFile(string path)
    {
        var markerIndex = path.LastIndexOf(".removing-", StringComparison.Ordinal);
        return markerIndex >= 0 && Guid.TryParseExact(path[(markerIndex + ".removing-".Length)..], "N", out _);
    }

    private static string RemovalOriginalPath(string path) =>
        path[..path.LastIndexOf(".removing-", StringComparison.Ordinal)];

    private static string RemovalStagingPath(ImageRecord record) =>
        record.StoredPath + ".removing-" + record.Id;

    private static async Task<bool> FilesMatchAsync(string leftPath, string rightPath)
    {
        await using var left = File.OpenRead(leftPath);
        await using var right = File.OpenRead(rightPath);
        if (left.Length != right.Length) return false;
        var leftHash = await SHA256.HashDataAsync(left);
        var rightHash = await SHA256.HashDataAsync(right);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    /// <summary>Parses ComfyUI metadata once for records imported before (or with an older) parser.</summary>
    private async Task BackfillGenerationAsync(List<ImageRecord> records, byte[] key)
    {
        var staleRecordIndices = records.Select((record, index) => (record, index))
            .Where(item => item.record.MetadataVersion < ComfyMetadata.Version).ToList();
        if (staleRecordIndices.Count == 0) return;
        await store.Gate.WaitAsync();
        try
        {
            foreach (var (record, index) in staleRecordIndices)
            {
                try
                {
                    var original = await FindAsync(record.Id, key);
                    var edit = new ImageRecordEdit(original)
                    {
                        Generation = ComfyMetadata.Extract(original.Metadata),
                        MetadataVersion = ComfyMetadata.Version
                    };
                    var refreshedImageRecord = edit.ToRecord(original);
                    await store.UpdateAsync(refreshedImageRecord.Id, EncryptRecord(refreshedImageRecord, key));
                    records[index] = refreshedImageRecord;
                }
                catch (Exception exception) when (exception is InvalidOperationException or CryptographicException)
                {
                    throw new InvalidOperationException($"Could not backfill generation metadata for image '{record.Id}'.", exception);
                }
            }
        }
        finally { store.Gate.Release(); }
    }

    private async Task<List<ImageRecord>> ListWithKeyAsync(byte[] key)
    {
        var imageRecords = new List<ImageRecord>();
        foreach (var (id, payload) in await store.ReadRecordsAsync())
        {
            imageRecords.Add(DecryptRecord(id, payload, key));
        }
        return imageRecords;
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
        return await GalleryImageRenderer.CreatePreviewAsync(record, key.Bytes, fullResolution);
    }

    public async Task<string> ViewerImageAsync(string id, bool fullResolution)
    {
        using var key = session.Borrow();
        var record = await FindAsync(id, key.Bytes);
        if (record.IsVideo) return await ThumbnailWithKeyAsync(id, key.Bytes);
        return await GalleryImageRenderer.CreateViewerImageAsync(record, key.Bytes, fullResolution);
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
        var readBufferBytes = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(readBufferBytes)) > 0)
        {
            if (original.Length + read > limit)
                throw new InvalidOperationException(isVideo ? "Video exceeds the 256 MiB limit." : "Image exceeds the 50 MiB limit.");
            await original.WriteAsync(readBufferBytes.AsMemory(0, read));
        }
        CryptographicOperations.ZeroMemory(readBufferBytes);
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
        var thumbnailBytes = await CreateThumbnailAsync(image);
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
        record = record with
        {
            StoredPath = Path.Combine(vault.Root, "images", record.Id[..2], record.Id[2..4], record.Id + ".dpng")
        };
        try
        {
            return (record,
                VaultCrypto.Encrypt(png.GetBuffer().AsSpan(0, (int)png.Length), key, $"image:{record.Id}"),
                VaultCrypto.Encrypt(thumbnailBytes, key, $"thumbnail:{record.Id}"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(png.GetBuffer());
            CryptographicOperations.ZeroMemory(thumbnailBytes);
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
        byte[] videoFrameBytes;
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
            videoFrameBytes = await video.FirstFrameAsync(temp);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"The temporary decrypted video could not be deleted from '{temp}'. Remove it manually.", exception);
            }
        }
        byte[] thumbnailBytes;
        try
        {
            using var image = Image.Load(videoFrameBytes);
            thumbnailBytes = await CreateThumbnailAsync(image);
        }
        finally { CryptographicOperations.ZeroMemory(videoFrameBytes); }
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
        record = record with
        {
            StoredPath = Path.Combine(vault.Root, "images", record.Id[..2], record.Id[2..4], record.Id + ".dmp4")
        };
        try
        {
            return (record, VaultCrypto.Encrypt(bytes.Span, key, $"image:{record.Id}"),
                VaultCrypto.Encrypt(thumbnailBytes, key, $"thumbnail:{record.Id}"));
        }
        finally { CryptographicOperations.ZeroMemory(thumbnailBytes); }
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
        var thumbnailBytes = thumb.ToArray();
        CryptographicOperations.ZeroMemory(thumb.GetBuffer());
        return thumbnailBytes;
    }

    private sealed class FolderImportProcessor(GalleryService gallery)
    {
        private readonly SemaphoreSlim progressGate = new(1);
        private int completedCount;
        private int importedCount;
        private int failedCount;

        public static string[] DiscoverImagePaths(string folder, bool recursive)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            return Directory.EnumerateFiles(folder, "*", options)
                .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        public async Task ImportAsync(string folder, string[] imagePaths, byte[] key, bool deleteSources,
            Action<string>? progress, Func<ImportProgress, Task>? reportProgress)
        {
            var failuresByPathIndex = new Exception?[imagePaths.Length];
            try
            {
                await ReportAsync(new ImportProgress(imagePaths.Length, 0, 0, 0, "Importing images..."), reportProgress);
                await Parallel.ForEachAsync(Enumerable.Range(0, imagePaths.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Min(2, Environment.ProcessorCount) },
                    async (pathIndex, _) =>
                        await ImportOneAsync(folder, imagePaths, pathIndex, key, deleteSources, progress,
                            reportProgress, failuresByPathIndex));

                var completionStatus = imagePaths.Length == 0 ? "No supported images found." : "Ingestion complete.";
                await ReportAsync(new ImportProgress(imagePaths.Length, completedCount, importedCount, failedCount, completionStatus),
                    reportProgress);
                var terminalFailures = failuresByPathIndex.OfType<Exception>().ToList();
                if (terminalFailures.Count > 0)
                    throw new ImportFolderException(folder, new AggregateException(terminalFailures));
            }
            finally { progressGate.Dispose(); }
        }

        private async Task ImportOneAsync(string folder, string[] imagePaths, int pathIndex, byte[] key, bool deleteSources,
            Action<string>? progress, Func<ImportProgress, Task>? reportProgress, Exception?[] failuresByPathIndex)
        {
            var path = imagePaths[pathIndex];
            var name = Path.GetFileName(path);
            await progressGate.WaitAsync();
            try { progress?.Invoke(name); }
            finally { progressGate.Release(); }
            await ReportAsync(new ImportProgress(imagePaths.Length, completedCount, importedCount, failedCount,
                $"Importing {name}..."), reportProgress);

            var importedSuccessfully = false;
            try
            {
                ImageRecord imported;
                await using (var input = File.OpenRead(path))
                {
                    imported = await gallery.ImportWithKeyAsync(input, name, Path.GetDirectoryName(path)!, path,
                        key, ensureFingerprints: false);
                    importedSuccessfully = true;
                    if (deleteSources)
                    {
                        await ReportAsync(new ImportProgress(imagePaths.Length, completedCount, importedCount, failedCount,
                            $"Verifying {name} before source deletion..."), reportProgress);
                        await gallery.VerifyImportedWithKeyAsync(imported.Id, key);
                    }
                }
                if (deleteSources)
                    await DeleteVerifiedSourceAsync(path, imported.Sha256);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or UnauthorizedAccessException or CryptographicException or UnknownImageFormatException
                or InvalidImageContentException)
            {
                failuresByPathIndex[pathIndex] = new IOException($"Failed to import '{path}'.", exception);
            }

            await progressGate.WaitAsync();
            try
            {
                completedCount++;
                if (importedSuccessfully) importedCount++;
                if (failuresByPathIndex[pathIndex] is not null) failedCount++;
                if (reportProgress is not null)
                    await reportProgress(new ImportProgress(imagePaths.Length, completedCount, importedCount, failedCount,
                        $"Processed {name}"));
            }
            finally { progressGate.Release(); }
        }

        private async Task ReportAsync(ImportProgress update, Func<ImportProgress, Task>? reportProgress)
        {
            await progressGate.WaitAsync();
            try
            {
                if (reportProgress is not null)
                    await reportProgress(update with
                    {
                        Completed = completedCount,
                        Imported = importedCount,
                        Failed = failedCount
                    });
            }
            finally { progressGate.Release(); }
        }
    }

    public async Task ImportFolderAsync(string folder, bool recursive, Action<string>? progress = null,
        bool deleteSources = false, Func<ImportProgress, Task>? reportProgress = null)
    {
        using var key = session.Borrow();
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing local folder.");
        if (reportProgress is not null)
            await reportProgress(new ImportProgress(null, 0, 0, 0, "Scanning selected folder..."));
        var imagePaths = await Task.Run(() => FolderImportProcessor.DiscoverImagePaths(folder, recursive));
        await store.Gate.WaitAsync();
        try { await BackfillFingerprintsAsync(key.Bytes); }
        finally { store.Gate.Release(); }
        await new FolderImportProcessor(this).ImportAsync(
            folder, imagePaths, key.Bytes, deleteSources, progress, reportProgress);
    }

    private static async Task DeleteVerifiedSourceAsync(string sourcePath, string expectedSha256)
    {
        var claimedPath = sourcePath + ".gallery-delete-" + Guid.NewGuid().ToString("N");
        File.Move(sourcePath, claimedPath);
        string actualSha256;
        try
        {
            await using var claimed = new FileStream(claimedPath, FileMode.Open, FileAccess.Read, FileShare.None);
            actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(claimed));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RestoreClaimedSource(claimedPath, sourcePath, exception);
            throw;
        }

        if (!actualSha256.Equals(expectedSha256, StringComparison.Ordinal))
        {
            RestoreClaimedSource(claimedPath, sourcePath);
            throw new InvalidOperationException("Source changed since import; it was not deleted.");
        }

        try { File.Delete(claimedPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RestoreClaimedSource(claimedPath, sourcePath, exception);
            throw new InvalidOperationException("Encrypted import succeeded, but source deletion failed.", exception);
        }
    }

    private static void RestoreClaimedSource(string claimedPath, string sourcePath, Exception? cause = null)
    {
        try
        {
            File.Move(claimedPath, sourcePath);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"The source could not be restored to '{sourcePath}' and is preserved at '{claimedPath}'.",
                cause is null ? exception : new AggregateException(cause, exception));
        }
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

    public async Task UpdateAsync(string id, Action<ImageRecordEdit> update)
    {
        using var key = session.Borrow();
        await store.Gate.WaitAsync();
        try
        {
            var record = await FindAsync(id, key.Bytes);
            var edit = new ImageRecordEdit(record);
            update(edit);
            var updatedRecord = edit.ToRecord(record);
            await store.UpdateAsync(id, EncryptRecord(updatedRecord, key.Bytes));
        }
        finally { store.Gate.Release(); }
    }

    public async Task UpdateManyAsync(IEnumerable<string> ids, Action<ImageRecordEdit> update)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(update);
        var uniqueImageIds = ids.Distinct(StringComparer.Ordinal).ToList();
        using var key = session.Borrow();
        var updateFailures = new List<Exception>();
        await store.Gate.WaitAsync();
        try
        {
            foreach (var imageId in uniqueImageIds)
            {
                try
                {
                    await UpdateRecordAsync(imageId, update, key.Bytes);
                }
                catch (Exception exception) when (exception is InvalidOperationException or CryptographicException or IOException)
                {
                    updateFailures.Add(new InvalidOperationException(
                        $"Could not update gallery record '{imageId}'.", exception));
                }
            }
        }
        finally { store.Gate.Release(); }
        if (updateFailures.Count > 0)
        {
            var failureSummary = string.Join(", ", updateFailures.Select(exception => exception.Message));
            throw new AggregateException(
                $"Failed to update {updateFailures.Count} gallery record(s): {failureSummary}", updateFailures);
        }
    }

    private async Task UpdateRecordAsync(string imageId, Action<ImageRecordEdit> update, byte[] key)
    {
        var record = await FindAsync(imageId, key);
        var edit = new ImageRecordEdit(record);
        update(edit);
        var updatedImageRecord = edit.ToRecord(record);
        await store.UpdateAsync(imageId, EncryptRecord(updatedImageRecord, key));
    }

    public async Task MoveAsync(string id, string folder, string name)
    {
        using var key = session.Borrow();
        name = NormalizeGalleryFileName(name);
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing destination folder.");
        await store.Gate.WaitAsync();
        try
        {
            var record = await FindAsync(id, key.Bytes);
            var target = Path.Combine(folder, name + (record.IsVideo ? ".dmp4" : ".dpng"));
            if (string.Equals(record.StoredPath, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a different name or destination.");
            await MoveStoredImageAsync(record, target, name, key.Bytes);
        }
        finally { store.Gate.Release(); }
    }

    private static string NormalizeGalleryFileName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' ') || name.Contains('/') || name.Contains('\\'))
            throw new InvalidOperationException("Enter a valid filename without a path or extension.");
        if (name.EndsWith(".dpng", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".dmp4", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        if (name.Length == 0) throw new InvalidOperationException("A filename is required.");
        return name;
    }

    private async Task MoveStoredImageAsync(ImageRecord record, string target, string newName, byte[] key)
    {
        var source = record.StoredPath;
        // Copy first: a crash may leave a ciphertext duplicate, but cannot lose the sole image.
        File.Copy(source, target, overwrite: false);
        var edit = new ImageRecordEdit(record) { StoredPath = target, Name = newName };
        var movedImageRecord = edit.ToRecord(record);
        try { await store.UpdateAsync(record.Id, EncryptRecord(movedImageRecord, key)); }
        catch
        {
            File.Delete(target);
            throw;
        }
        try { File.Delete(source); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The image was moved to '{target}', but the old encrypted copy at '{source}' could not be removed. Remove that extra copy after checking the destination.",
                exception);
        }
    }

    public async Task<VisionSettings> SettingsAsync() =>
        await ReadEncryptedSettingAsync<VisionSettings>("vision-settings", "vision-settings") ?? new();

    public async Task SaveSettingsAsync(VisionSettings settings)
    {
        VisionService.Validate(settings);
        await WriteEncryptedSettingAsync(settings, "vision-settings", "vision-settings");
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
