using System.Collections.Immutable;
using Gallery.Web.Services.Validators;
using Gallery.Web.Services.Processors;
using Gallery.Web.Services.DataManagers.Models;
using Gallery.Core.Extractors;
using Gallery.Core.Processors;
using Gallery.Web.Managers.Gateways;
using Gallery.Web.Services.DataManagers;
using Gallery.Web.Services.Sessions;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core;
using Gallery.Core.Queries;
using Gallery.Web.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Web.Services;

public sealed partial class GalleryService
{
    private const string PngDataUrlPrefix = "data:image/png;base64,";
    private const string Mp4Extension = ".mp4";
    private const string EncryptedMp4Extension = ".dmp4";
    private const string EncryptedPngExtension = ".dpng";
    private const string VisionSettingsId = "vision-settings";

    private const string RatingRangeMessageFormat = "Rating {0} must be between 0 and 5.";
    private const string EmptyDuplicateCandidatesMessage =
        "Cannot choose a duplicate keeper by deletion status, rating, favorite status, dimensions, original size, and import time: candidate count was 0.";

    private GalleryStore galleryStore;
    private VaultStore vaultStore;
    private readonly VaultSession vaultSession;
    private readonly VideoTools? videoTools;
    private readonly VisionService? visionService;

    private static class GalleryImageRenderer
    {
        public static async Task<string> CreatePreviewAsync(
            ImageRecord record, byte[] key, ImageResolution resolution)
        {
            var plainImage = await DecryptStoredImageAsync(record, key);
            try
            {
                using var image = Image.Load(plainImage);
                if (resolution == ImageResolution.Fit)
                    image.Mutate(context => context.Resize(new ResizeOptions
                    {
                        Size = new Size(1600, 1600), Mode = ResizeMode.Max
                    }));
                using var preview = new MemoryStream();
                await image.SaveAsPngAsync(preview, new PngEncoder { SkipMetadata = true });
                var dataUrl = PngDataUrlPrefix +
                    Convert.ToBase64String(preview.GetBuffer(), 0, (int)preview.Length);
                CryptographicOperations.ZeroMemory(preview.GetBuffer());
                return dataUrl;
            }
            finally { CryptographicOperations.ZeroMemory(plainImage); }
        }

        public static async Task<string> CreateViewerImageAsync(
            ImageRecord record, byte[] key, ImageResolution resolution)
        {
            var plainImage = await DecryptStoredImageAsync(record, key);
            try
            {
                if (resolution == ImageResolution.Original)
                    return ImageContentFormats.DataUrlPrefix(record.ContentType) + Convert.ToBase64String(plainImage);

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

    internal GalleryService(GalleryStore galleryStore, VaultStore vaultStore, VaultSession vaultSession, VideoTools? videoTools = null,
        VisionService? visionService = null)
    {
        this.galleryStore = galleryStore;
        this.vaultStore = vaultStore;
        this.vaultSession = vaultSession;
        this.videoTools = videoTools;
        this.visionService = visionService;
        this.vaultSession.AutoLocked += () => OnAutoLocked?.Invoke();
        drivePlayer = CreateDrivePlayer();
    }

    public const long MaxFileBytes = 50 * 1024 * 1024;
    public const long MaxVideoBytes = 256 * 1024 * 1024;
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", Mp4Extension];

    public static bool IsVideoName(string name) => string.Equals(Path.GetExtension(name), Mp4Extension, StringComparison.OrdinalIgnoreCase);

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

    public HashSet<string> InitialDuplicateKeepers(IReadOnlyCollection<ImageRecord> duplicateImageRecords)
    {
        var candidates = RequireDuplicateCandidates(duplicateImageRecords);
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

    public ImageRecordEdit ApplyReviewDecision(ImageRecordEdit image, ReviewAction action, int rating = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!Enum.IsDefined(action))
            throw new InvalidOperationException(
                $"Review action value {(int)action} is invalid; choose Keep, Favorite, MarkDeletion, or Rate.");
        if (action == ReviewAction.Rate && rating is < 1 or > 5)
            throw new InvalidOperationException($"Rating {rating} must be between 1 and 5.");
        return ReviewDecision.Apply(image, action, rating);
    }

    public ReviewState CaptureReviewState(ImageRecord image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ReviewState.Capture(image);
    }

    public ImageRecordEdit RestoreReviewState(ImageRecordEdit image, ReviewState state)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(state);
        return ReviewDecision.Restore(image, state);
    }

    public ImageRecordEdit SetCuration(ImageRecordEdit image, int? rating, bool? reviewed)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateCurationRating(rating);
        return LibraryRules.SetCuration(image, rating, reviewed);
    }

    public ImageRecordEdit SetDeletionMark(ImageRecordEdit image, bool markedForDeletion)
    {
        ArgumentNullException.ThrowIfNull(image);
        return LibraryRules.SetDeletionMark(image, markedForDeletion);
    }

    public ImageRecordEdit SetOrganization(ImageRecordEdit image, int rating, bool reviewed, string? collection)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateCurationRating(rating);
        if (IsInvalidCollectionName(collection))
            throw new InvalidOperationException("Collection names must have 1 to 80 characters.");
        return LibraryRules.SetOrganization(image, rating, reviewed, collection);
    }

    private static void ValidateCurationRating(int? rating)
    {
        if (rating is < 0 or > 5)
            throw new InvalidOperationException(string.Format(
                System.Globalization.CultureInfo.InvariantCulture, RatingRangeMessageFormat, rating));
    }

    private static void ValidateOperationChoice<TChoice>(TChoice choice) where TChoice : struct, Enum
    {
        if (!Enum.IsDefined(choice))
            throw new InvalidOperationException(
                $"{typeof(TChoice).Name} value {Convert.ToInt32(choice)} is invalid. Choose {string.Join(", ", Enum.GetNames<TChoice>())}.");
    }

    private static bool IsInvalidCollectionName(string? collection) =>
        collection is not null && (string.IsNullOrWhiteSpace(collection) || collection.Trim().Length > 80);

    public string MergeTags(string existing, string additions) => LibraryRules.MergeTags(existing, additions);

    internal async Task<List<SavedView>> LoadViewsAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryStore.LoadViewsAsync(key.Bytes);
    }

    internal async Task SaveViewAsync(string name, GalleryFilter filter)
    {
        var normalizedName = name.Trim();
        SavedViewNameValidator.Validate(normalizedName);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.SaveViewAsync(normalizedName, filter, key.Bytes);
    }

    internal async Task DeleteViewAsync(string name)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.DeleteViewAsync(name, key.Bytes);
    }
    public Task<IReadOnlyList<string>> GetVisionModelsAsync(VisionSettings settings) =>
        RequiredVision().GetModelsAsync(settings);
    public Task<(string Description, string Tags)> AnalyzeImageAsync(VisionSettings settings, string imageData) =>
        RequiredVision().AnalyzeAsync(settings, imageData);
    public Task<string> SuggestCollectionAsync(VisionSettings settings, string description, string tags,
        IEnumerable<string> collections) => RequiredVision().SuggestCollectionAsync(settings, description, tags, collections);
    public Task<GalleryFilter> InterpretSearchAsync(VisionSettings settings, string query, IEnumerable<string> collections) =>
        RequiredVision().InterpretSearchAsync(settings, query, collections);
    public Task<(string Description, string Tags, string Collection, string Warning)> AnalyzeImageWithCollectionAsync(
        VisionSettings settings, string imageData, IEnumerable<string> collections) =>
        RequiredVision().AnalyzeImageWithCollectionAsync(settings, imageData, collections);

    private VisionService RequiredVision() => visionService
        ?? throw new InvalidOperationException("The vision manager is not configured.");

    public static string FormatDuration(ImageRecord image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return GalleryDisplayFormatter.FormatDuration(image);
    }

    public static string FormatBytes(long bytes) => GalleryDisplayFormatter.FormatBytes(bytes);

    public async Task<List<ImageRecord>> ListAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await ListWithKeyAsync(key.Bytes);
    }

    public async Task<List<ImageRecord>> LoadAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await LoadGalleryWithKeyAsync(key.Bytes);
    }

    private async Task<List<ImageRecord>> LoadGalleryWithKeyAsync(byte[] key)
    {
        List<ImageRecord> records;
        await galleryStore.Gate.WaitAsync();
        try
        {
            records = await ListWithKeyAsync(key);
            await BackfillFingerprintsAsync(key);
            await BackfillGenerationAsync(records, key);
        }
        finally { galleryStore.Gate.Release(); }
        await ScheduleMediaIndexAsync(records, key);
        drivePlayer.Start();
        return records;
    }
    /// <summary>Parses ComfyUI metadata once for records imported before (or with an older) parser.</summary>
    private async Task BackfillGenerationAsync(List<ImageRecord> records, byte[] key)
    {
        var staleRecordIndices = records.Select((record, index) => (record, index))
            .Where(item => item.record.MetadataVersion < ComfyMetadata.Version).ToList();
        foreach (var staleRecordIndex in staleRecordIndices)
        {
            ImageRecord record;
            int index;
            (record, index) = staleRecordIndex;
            try
            {
                var edit = new ImageRecordEdit(record)
                {
                    Generation = ComfyMetadata.Extract(record.Metadata),
                    MetadataVersion = ComfyMetadata.Version
                };
                var refreshedImageRecord = edit.ToRecord(record);
                await galleryStore.UpdateAsync(refreshedImageRecord, key);
                records[index] = refreshedImageRecord;
            }
            catch (Exception exception) when (exception is InvalidOperationException or CryptographicException or StoredMetadataFormatException)
            {
                throw new InvalidOperationException($"Could not backfill generation metadata for image '{record.Id}'.", exception);
            }
        }
    }

    private Task<List<ImageRecord>> ListWithKeyAsync(byte[] key) => galleryStore.ReadRecordsAsync(key);

    private Task BackfillFingerprintsAsync(byte[] key) => galleryStore.BackfillFingerprintsAsync(key);

    public async Task<string> ThumbnailAsync(string id)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await ThumbnailWithKeyAsync(id, key.Bytes);
    }

    private async Task<string> ThumbnailWithKeyAsync(string id, byte[] key)
    {
        var plain = (await galleryStore.ReadThumbnailAsync(id, key)).PngBytes;
        try { return PngDataUrlPrefix + Convert.ToBase64String(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<string> PreviewAsync(string id, ImageResolution resolution = ImageResolution.Fit)
    {
        ValidateOperationChoice(resolution);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var record = await FindAsync(id, key.Bytes);
        if (record.IsVideo) return await ThumbnailWithKeyAsync(id, key.Bytes);
        return await GalleryImageRenderer.CreatePreviewAsync(record, key.Bytes, resolution);
    }

    public async Task<string> ViewerImageAsync(string id, ImageResolution resolution)
    {
        ValidateOperationChoice(resolution);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var record = await FindAsync(id, key.Bytes);
        if (record.IsVideo) return await ThumbnailWithKeyAsync(id, key.Bytes);
        return await GalleryImageRenderer.CreateViewerImageAsync(record, key.Bytes, resolution);
    }

    public async Task<ImageRecord> ImportAsync(Stream input, string name, string sourceFolder, string sourcePath = "")
    {
        ValidateImportName(name);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await PrepareFingerprintIndexAsync(key.Bytes);
        await using var catalog = await galleryStore.OpenImportCatalogAsync();
        return await ImportWithKeyAsync(input, name, sourceFolder, sourcePath, key.Bytes, catalog, null);
    }

    private static void ValidateImportName(string name) =>
        ImportRequestValidator.ValidateName(name, Extensions);

    private async Task PrepareFingerprintIndexAsync(byte[] key)
    {
        await galleryStore.Gate.WaitAsync();
        try { await BackfillFingerprintsAsync(key); }
        finally { galleryStore.Gate.Release(); }
    }

    private async Task<ImageRecord> ImportWithKeyAsync(Stream input, string name, string sourceFolder,
        string sourcePath, byte[] key, GalleryStore.ImportCatalog catalog, IngestionTimings? timings)
    {
        var mediaRoot = await MediaDirectoryWithKeyAsync(key);
        var prepared = await PrepareImportAsync(input, name, sourceFolder, sourcePath, key, catalog, timings, mediaRoot);
        return await PersistImportAsync(prepared.Record, prepared.Encrypted, prepared.EncryptedThumb,
            prepared.Record.Sha256, name, key, catalog, timings);
    }

    private async Task<(ImageRecord Record, byte[] Encrypted, byte[] EncryptedThumb)> PrepareImportAsync(
        Stream input, string name, string sourceFolder, string sourcePath, byte[] key,
        GalleryStore.ImportCatalog catalog, IngestionTimings? timings, string mediaRoot)
    {
        var isVideo = IsVideoName(name);
        var stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using var original = await ImportContentReader.ReadAsync(input,
            isVideo ? MaxVideoBytes : MaxFileBytes,
            isVideo ? "Video exceeds the 256 MiB limit." : "Image exceeds the 50 MiB limit.");
        timings?.Record(IngestionStage.ReadSource, stageStart);
        timings?.AddSourceBytes(original.Length);
        var bytes = original.GetBuffer().AsMemory(0, (int)original.Length);
        try
        {
            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var sourceHash = Convert.ToHexString(SHA256.HashData(bytes.Span));
            timings?.Record(IngestionStage.HashSource, stageStart);
            await RejectDuplicateImportAsync(sourceHash, name, key, catalog, timings);
            return isVideo
                ? await PrepareVideoAsync(bytes, name, sourceFolder, sourcePath, sourceHash, key, timings, mediaRoot)
                : await PrepareImageAsync(original, name, sourceFolder, sourcePath, sourceHash, key, timings, mediaRoot);
        }
        finally { CryptographicOperations.ZeroMemory(original.GetBuffer()); }
    }

    private async Task RejectDuplicateImportAsync(string sourceHash, string name, byte[] key,
        GalleryStore.ImportCatalog catalog, IngestionTimings? timings)
    {
        var stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        await galleryStore.Gate.WaitAsync();
        timings?.Record(IngestionStage.DuplicateWait, stageStart);
        stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try { await RejectKnownFingerprintAsync(sourceHash, name, key, catalog); }
        finally
        {
            timings?.Record(IngestionStage.DuplicateCheck, stageStart);
            galleryStore.Gate.Release();
        }
    }

    private static async Task RejectKnownFingerprintAsync(string sourceHash, string name, byte[] key, GalleryStore.ImportCatalog catalog)
    {
        if (await catalog.ContainsFingerprintAsync(sourceHash, key))
            throw new InvalidOperationException($"'{name}' is already in the gallery.");
    }

    private async Task<ImageRecord> PersistImportAsync(
        ImageRecord record, byte[] encrypted, byte[] encryptedThumbnail, string sourceHash, string name, byte[] key,
        GalleryStore.ImportCatalog catalog, IngestionTimings? timings)
    {
        var stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        await WriteImportedCiphertextAsync(record.StoredPath, encrypted);
        timings?.Record(IngestionStage.WriteMedia, stageStart);
        try
        {
            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            await galleryStore.Gate.WaitAsync();
            timings?.Record(IngestionStage.PersistWait, stageStart);
            try
            {
                await RejectKnownFingerprintAsync(sourceHash, name, key, catalog);
                stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
                await catalog.InsertEncryptedAsync(record, encryptedThumbnail, key);
                timings?.Record(IngestionStage.CatalogInsert, stageStart);
            }
            finally { galleryStore.Gate.Release(); }
            return record;
        }
        catch
        {
            File.Delete(record.StoredPath);
            throw;
        }
    }

    private static async Task WriteImportedCiphertextAsync(string storedPath, byte[] encrypted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
        await using var output = new FileStream(storedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await output.WriteAsync(encrypted);
            output.Flush(flushToDisk: true);
        }
        catch
        {
            output.Close();
            File.Delete(storedPath);
            throw;
        }
    }

    private async Task<(ImageRecord Record, byte[] Encrypted, byte[] EncryptedThumb)> PrepareImageAsync(
        MemoryStream original, string name, string sourceFolder, string sourcePath, string sourceHash, byte[] key,
        IngestionTimings? timings, string mediaRoot)
    {
        var stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        original.Position = 0;
        using var image = await DecodeImportImageAsync(original);
        ImportMediaValidator.ValidateImageDimensions(image.Width, image.Height);
        var contentType = ImageContentFormats.RequireSupportedContentType(image.Metadata.DecodedImageFormat);
        ImportMediaValidator.ValidateImageFrames(image.Frames.Count);
        var metadata = JsonSerializer.Serialize(new
        {
            png = image.Metadata.GetPngMetadata().TextData.Select(chunk => new { chunk.Keyword, chunk.Value }),
            exif = image.Metadata.ExifProfile?.Values.Select(value => $"{value.Tag}: {value.GetValue()}")
        });
        timings?.Record(IngestionStage.Decode, stageStart);
        stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var thumbnailBytes = await CreateThumbnailAsync(image);
        timings?.Record(IngestionStage.Thumbnail, stageStart);
        var record = new ImageRecord
        {
            Name = Path.GetFileNameWithoutExtension(name),
            OriginalName = Path.GetFileName(name),
            SourceFolder = sourceFolder,
            SourcePath = sourcePath,
            Width = image.Width, Height = image.Height,
            OriginalBytes = original.Length,
            Sha256 = sourceHash,
            PngSha256 = sourceHash,
            ContentType = contentType,
            Metadata = metadata,
            Generation = ComfyMetadata.Extract(metadata),
            MetadataVersion = ComfyMetadata.Version
        };
        record = record with
        {
            StoredPath = Path.Combine(mediaRoot, "images", record.Id[..2], record.Id[2..4], record.Id + EncryptedPngExtension)
        };
        try
        {
            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var encrypted = (record,
                VaultCrypto.Encrypt(original.GetBuffer().AsSpan(0, (int)original.Length), key, $"image:{record.Id}"),
                VaultCrypto.Encrypt(thumbnailBytes, key, $"thumbnail:{record.Id}"));
            timings?.Record(IngestionStage.Encrypt, stageStart);
            return encrypted;
        }

        finally { CryptographicOperations.ZeroMemory(thumbnailBytes); }
    }

    private static async Task<Image<Rgba32>> DecodeImportImageAsync(Stream original)
    {
        var configuration = Configuration.Default.Clone();
        configuration.MaxDegreeOfParallelism = 1;
        configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            AllocationLimitMegabytes = 256,
            AccumulativeAllocationLimitMegabytes = 512,
            MaximumPoolSizeMegabytes = 0
        });
        try
        {
            return await Image.LoadAsync<Rgba32>(new DecoderOptions
            {
                Configuration = configuration,
                MaxFrames = 2
            }, original);
        }
        catch (InvalidMemoryOperationException exception)
        {
            throw new InvalidOperationException("Image exceeds the bounded decode memory limit.", exception);
        }
    }

    /// <summary>
    /// ffprobe/ffmpeg need a file, so the plaintext MP4 is written briefly to a randomly named temp file
    /// that is deleted as soon as the metadata and first frame are read.
    /// </summary>
    private async Task<(ImageRecord Record, byte[] Encrypted, byte[] EncryptedThumb)> PrepareVideoAsync(
        ReadOnlyMemory<byte> bytes, string name, string sourceFolder, string sourcePath, string sourceHash, byte[] key,
        IngestionTimings? timings, string mediaRoot)
    {
        var tools = RequiredVideoTools();
        var stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var video = await ReadTemporaryVideoAsync(bytes, tools);
        timings?.Record(IngestionStage.VideoProbe, stageStart);
        stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var thumbnailBytes = await CreateVideoThumbnailAsync(video.FrameBytes);
        timings?.Record(IngestionStage.Thumbnail, stageStart);
        try
        {
            var record = CreateVideoRecord(video.Probe, bytes.Length, name, sourceFolder, sourcePath, sourceHash, mediaRoot);
            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var encrypted = EncryptPreparedVideo(record, bytes, thumbnailBytes, key);
            timings?.Record(IngestionStage.Encrypt, stageStart);
            return encrypted;
        }
        finally { CryptographicOperations.ZeroMemory(thumbnailBytes); }
    }

    private VideoTools RequiredVideoTools() =>
        videoTools is not null && videoTools.IsAvailable ? videoTools
            : throw new VideoToolUnavailableException(VideoTools.MissingMessage, GalleryLogEvent.PrepareVideo,
                new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Video.RequiredTools", "ffmpeg, ffprobe")));

    private static async Task<(VideoTools.ProbeResult Probe, byte[] FrameBytes)> ReadTemporaryVideoAsync(
        ReadOnlyMemory<byte> bytes, VideoTools tools)
    {
        var temporaryVideoPath = Path.Combine(Path.GetTempPath(), "gallery-" + Guid.NewGuid().ToString("N") + Mp4Extension);
        byte[]? videoFrameBytes = null;
        try
        {
            await WriteTemporaryVideoAsync(temporaryVideoPath, bytes);
            var probe = await tools.ProbeAsync(temporaryVideoPath);
            ImportMediaValidator.ValidateVideo(probe);
            videoFrameBytes = await tools.FirstFrameAsync(temporaryVideoPath);
            return (probe, videoFrameBytes);
        }
        finally
        {
            try { DeleteTemporaryVideo(temporaryVideoPath); }
            catch
            {
                if (videoFrameBytes is not null) CryptographicOperations.ZeroMemory(videoFrameBytes);
                throw;
            }
        }
    }

    private static async Task WriteTemporaryVideoAsync(string path, ReadOnlyMemory<byte> bytes)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes);
    }

    private static void DeleteTemporaryVideo(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The temporary decrypted video could not be deleted from '{path}'. Remove it manually.", exception);
        }
    }

    private static async Task<byte[]> CreateVideoThumbnailAsync(byte[] frameBytes)
    {
        try
        {
            using var image = Image.Load(frameBytes);
            return await CreateThumbnailAsync(image);
        }
        finally { CryptographicOperations.ZeroMemory(frameBytes); }
    }

    private ImageRecord CreateVideoRecord(VideoTools.ProbeResult probe, int contentLength,
        string name, string sourceFolder, string sourcePath, string sourceHash, string mediaRoot)
    {
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
            OriginalBytes = contentLength,
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
            StoredPath = Path.Combine(mediaRoot, "images", record.Id[..2], record.Id[2..4], record.Id + EncryptedMp4Extension)
        };
        return record;
    }

    private static (ImageRecord Record, byte[] Encrypted, byte[] EncryptedThumb) EncryptPreparedVideo(
        ImageRecord record, ReadOnlyMemory<byte> bytes, byte[] thumbnailBytes, byte[] key)
    {
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

    private sealed class FolderImportProcessor(GalleryService galleryService)
    {
        public static string[] DiscoverImagePaths(string folder, FolderSearchScope searchScope)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = searchScope == FolderSearchScope.Descendants,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            return Directory.EnumerateFiles(folder, "*", options)
                .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        private static void ThrowImportFailures(string folder, Exception?[] failuresByPathIndex)
        {
            var failures = failuresByPathIndex.OfType<Exception>().ToList();
            if (failures.Count == 0) return;
            throw new ImportFolderException($"Importing folder '{folder}' completed with one or more file failures.",
                new FolderImportFailureLogEvent(), new ContextualData(ImmutableDictionary<string, object>.Empty
                    .Add("Folder", folder).Add("FailureCount", failures.Count)), new AggregateException(failures));
        }

        public async Task ImportAsync(string folder, string[] imagePaths, byte[] key, SourceRetention sourceRetention,
            Action<string>? progress, Func<ImportProgress, Task>? reportProgress, GalleryStore.ImportCatalog catalog,
            IngestionTimings? timings, IngestionOptions options, string mediaRoot)
        {
            using var progressGate = new SemaphoreSlim(1);
            int completedCount = 0;
            int importedCount = 0;
            int failedCount = 0;
            var failuresByPathIndex = new Exception?[imagePaths.Length];
            var workers = options.Workers;
            var lastReport = System.Diagnostics.Stopwatch.StartNew();
            timings?.SetFiles(imagePaths.Length, workers);
            await ReportAsync("Importing images...", force: true);
            foreach (var indices in Enumerable.Range(0, imagePaths.Length).Chunk(options.CatalogBatchSize))
            {
                var pending = indices.Select(index => new PendingImport(index, imagePaths[index])).ToArray();
                Exception? interruption = null;
                try
                {
                    await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = workers },
                        async (item, _) =>
                        {
                            try
                            {
                                await ReportAsync($"Importing {item.Name}...", name: item.Name);
                                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                                item.Source = new ImportSource(item.Path, sourceRetention == SourceRetention.DeleteAfterVerification);
                                timings?.Record(IngestionStage.OpenSource, start);
                                ValidateImportName(item.Name);
                                var prepared = await galleryService.PrepareImportAsync(item.Source.Stream, item.Name,
                                    Path.GetDirectoryName(item.Path)!, item.Path, key, catalog, timings, mediaRoot);
                                start = System.Diagnostics.Stopwatch.GetTimestamp();
                                await WriteImportedCiphertextAsync(prepared.Record.StoredPath, prepared.Encrypted);
                                timings?.Record(IngestionStage.WriteMedia, start);
                                item.Record = prepared.Record;
                                item.EncryptedThumbnail = prepared.EncryptedThumb;
                            }
                            catch (Exception exception) when (IsImportFailure(exception)) { item.Failure = exception; }
                        });

                    await CommitBatchAsync(pending);
                    await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = workers },
                        async (item, _) =>
                        {
                            try
                            {
                                if (item.Committed && sourceRetention == SourceRetention.DeleteAfterVerification)
                                {
                                    var start = System.Diagnostics.Stopwatch.GetTimestamp();
                                    await galleryService.VerifyImportedWithKeyAsync(item.Record!.Id, key, catalog);
                                    timings?.Record(IngestionStage.Verify, start);
                                    start = System.Diagnostics.Stopwatch.GetTimestamp();
                                    if (item.Source!.CanDeleteByHandle) item.Source.DeleteVerified();
                                    else
                                    {
                                        await CloseSourceAsync(item);
                                        await DeleteVerifiedSourceAsync(item.Path, item.Record.Sha256);
                                    }
                                    timings?.Record(IngestionStage.DeleteSource, start);
                                }
                            }
                            catch (Exception exception) when (IsImportFailure(exception)) { item.Failure = exception; }
                            finally
                            {
                                try { await CloseSourceAsync(item); }
                                catch (IOException exception) { item.Failure = exception; }
                            }
                            if (!item.Committed && item.Record is not null)
                            {
                                try { File.Delete(item.Record.StoredPath); }
                                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                                {
                                    item.Failure = new AggregateException("Import failed and its uncommitted ciphertext could not be removed.",
                                        item.Failure ?? new IOException("Catalog commit failed."), exception);
                                }
                                item.MediaCleanupAttempted = true;
                            }
                            await progressGate.WaitAsync();
                            try
                            {
                                completedCount++;
                                if (item.Committed) importedCount++;
                                if (item.Failure is not null)
                                {
                                    failedCount++;
                                    failuresByPathIndex[item.Index] = new IOException($"Failed to import '{item.Path}'.", item.Failure);
                                }
                            }
                            finally { progressGate.Release(); }
                            await ReportAsync($"Processed {item.Name}", force: item.Failure is not null);
                            timings?.Record(IngestionStage.File, item.Started);
                        });
                }
                catch (Exception exception)
                {
                    interruption = exception;
                    throw;
                }
                finally
                {
                    var cleanupFailures = new List<Exception>();
                    foreach (var item in pending)
                    {
                        try { await CloseSourceAsync(item); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            cleanupFailures.Add(exception);
                        }
                        if (!item.Committed && item.Record is not null && !item.MediaCleanupAttempted)
                        {
                            try { File.Delete(item.Record.StoredPath); }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                            {
                                cleanupFailures.Add(exception);
                            }
                        }
                    }
                    if (cleanupFailures.Count > 0)
                    {
                        if (interruption is not null) cleanupFailures.Insert(0, interruption);
                        throw new AggregateException("Import cleanup failed; inspect any remaining uncommitted ciphertext.", cleanupFailures);
                    }
                }
            }
            timings?.SetOutcome(importedCount, failedCount);
            await ReportAsync(imagePaths.Length == 0 ? "No supported images found." : "Ingestion complete.", force: true);
            ThrowImportFailures(folder, failuresByPathIndex);

            async Task CommitBatchAsync(PendingImport[] pending)
            {
                var ready = pending.Where(item => item.Record is not null && item.Failure is null).ToArray();
                if (ready.Length == 0) return;
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                await galleryService.galleryStore.Gate.WaitAsync();
                timings?.Record(IngestionStage.PersistWait, start);
                try
                {
                    using var transaction = catalog.BeginBatch();
                    foreach (var item in ready)
                    {
                        if (await catalog.ContainsFingerprintAsync(item.Record!.Sha256, key, transaction))
                        {
                            item.Failure = new InvalidOperationException($"'{item.Name}' is already in the gallery.");
                            continue;
                        }
                        start = System.Diagnostics.Stopwatch.GetTimestamp();
                        await catalog.InsertEncryptedAsync(item.Record, item.EncryptedThumbnail!, key, transaction);
                        timings?.Record(IngestionStage.CatalogInsert, start);
                    }
                    start = System.Diagnostics.Stopwatch.GetTimestamp();
                    transaction.Commit();
                    timings?.Record(IngestionStage.CatalogCommit, start);
                    foreach (var item in ready.Where(item => item.Failure is null)) item.Committed = true;
                }
                catch (Exception exception) when (IsImportFailure(exception))
                {
                    foreach (var item in ready) item.Failure ??= exception;
                }
                finally { galleryService.galleryStore.Gate.Release(); }
            }

            async Task CloseSourceAsync(PendingImport item)
            {
                if (item.Source is null) return;
                var source = item.Source;
                item.Source = null;
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                try { await source.DisposeAsync(); }
                finally { timings?.Record(IngestionStage.CloseSource, start); }
            }

            async Task ReportAsync(string status, bool force = false, string? name = null)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                await progressGate.WaitAsync();
                try
                {
                    if (name is not null) progress?.Invoke(name);
                    if (reportProgress is not null && (force || lastReport.ElapsedMilliseconds >= 100))
                    {
                        await reportProgress(new ImportProgress(imagePaths.Length, completedCount, importedCount, failedCount, status));
                        lastReport.Restart();
                    }
                }
                finally
                {
                    progressGate.Release();
                    timings?.Record(IngestionStage.ReportProgress, start);
                }
            }
        }

        private static bool IsImportFailure(Exception exception) => exception is IOException or InvalidOperationException
            or UnauthorizedAccessException or GalleryException or CryptographicException or UnknownImageFormatException
            or InvalidImageContentException or Microsoft.Data.Sqlite.SqliteException;

        private sealed class PendingImport(int index, string path)
        {
            public int Index { get; } = index;
            public string Path { get; } = path;
            public string Name { get; } = System.IO.Path.GetFileName(path);
            public long Started { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
            public ImportSource? Source { get; set; }
            public ImageRecord? Record { get; set; }
            public byte[]? EncryptedThumbnail { get; set; }
            public Exception? Failure { get; set; }
            public bool Committed { get; set; }
            public bool MediaCleanupAttempted { get; set; }
        }
    }

    public async Task ImportFolderAsync(string folder, FolderSearchScope searchScope, Action<string>? progress = null,
        SourceRetention sourceRetention = SourceRetention.Retain, Func<ImportProgress, Task>? reportProgress = null,
        IngestionTimings? timings = null, IngestionOptions? options = null)
    {
        ValidateOperationChoice(searchScope);
        ValidateOperationChoice(sourceRetention);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        options ??= await galleryStore.ReadSettingsAsync<IngestionOptions>("ingestion-options", "ingestion-options:v1", key.Bytes)
            ?? new IngestionOptions();
        options.Validate();
        var mediaRoot = await MediaDirectoryWithKeyAsync(key.Bytes);
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing local folder.");
        timings?.Begin(folder, mediaRoot, VaultRoot);
        try
        {
            if (reportProgress is not null)
            {
                var reportStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try { await reportProgress(new ImportProgress(null, 0, 0, 0, "Scanning selected folder...")); }
                finally { timings?.Record(IngestionStage.ReportProgress, reportStart); }
            }
            var stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var imagePaths = await Task.Run(() => FolderImportProcessor.DiscoverImagePaths(folder, searchScope));
            timings?.Record(IngestionStage.Discover, stageStart);
            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            await galleryStore.Gate.WaitAsync();
            try { await BackfillFingerprintsAsync(key.Bytes); }
            finally { galleryStore.Gate.Release(); }
            timings?.Record(IngestionStage.Backfill, stageStart);
            await using var catalog = await galleryStore.OpenImportCatalogAsync();
            await new FolderImportProcessor(this).ImportAsync(
                folder, imagePaths, key.Bytes, sourceRetention, progress, reportProgress, catalog, timings, options, mediaRoot);
        }
        finally { timings?.Complete(); }
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
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await VerifyImportedWithKeyAsync(id, key.Bytes);
    }

    private async Task VerifyImportedWithKeyAsync(string id, byte[] key, GalleryStore.ImportCatalog? catalog = null)
    {
        if (catalog is null)
        {
            await using var verificationCatalog = await galleryStore.OpenImportCatalogAsync();
            await VerifyImportedWithKeyAsync(id, key, verificationCatalog);
            return;
        }
        ImageRecord record;
        byte[] thumbnail;
        await galleryStore.Gate.WaitAsync();
        try { (record, thumbnail) = await catalog.ReadVerificationAsync(id, key); }
        finally { galleryStore.Gate.Release(); }
        try
        {
            var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key, $"image:{id}");
            try
            {
                if (record.PngSha256.Length == 0 || Convert.ToHexString(SHA256.HashData(plain)) != record.PngSha256)
                    throw new CryptographicException("Saved file did not match the imported content.");
                if (!record.IsVideo)
                {
                    var image = Image.Identify(plain);
                    if (image.Width != record.Width || image.Height != record.Height)
                        throw new CryptographicException("Saved image dimensions did not match the catalog.");
                }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Image.Identify(thumbnail);
        }
        finally { CryptographicOperations.ZeroMemory(thumbnail); }
    }

    public async Task UpdateAsync(string id, Func<ImageRecordEdit, ImageRecordEdit> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.Gate.WaitAsync();
        try { await UpdateRecordAsync(id, update, key.Bytes); }
        finally { galleryStore.Gate.Release(); }
    }

    public async Task UpdateManyAsync(IEnumerable<string> ids, Func<ImageRecordEdit, ImageRecordEdit> update)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(update);
        // Callbacks can mutate the caller's ID collection; capture the batch before invoking them.
        var uniqueImageIds = ids.Distinct(StringComparer.Ordinal).ToList();
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.Gate.WaitAsync();
        try { await GalleryRecordUpdater.UpdateBatchAsync(uniqueImageIds, id => UpdateRecordAsync(id, update, key.Bytes)); }
        finally { galleryStore.Gate.Release(); }
    }

    private async Task UpdateRecordAsync(string imageId, Func<ImageRecordEdit, ImageRecordEdit> update, byte[] key)
    {
        var record = await FindAsync(imageId, key);
        await galleryStore.UpdateAsync(GalleryRecordUpdater.Apply(record, update), key);
    }

    public async Task MoveAsync(string id, string folder, string name)
    {
        StoredImageMoveValidator.ValidateName(name);
        var destination = StoredImageMover.NormalizeDestination(folder, name);
        StoredImageMoveValidator.ValidateDestination(destination);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await MoveUnderGateAsync(id, destination, key.Bytes);
        drivePlayer.Start();
    }

    private async Task MoveUnderGateAsync(string id, StoredImageMoveDestination destination, byte[] key)
    {
        await galleryStore.Gate.WaitAsync();
        try
        {
            var record = await FindAsync(id, key);
            var target = StoredImageMover.TargetPath(record, destination);
            StoredImageMoveValidator.ValidateDifferentPath(record.StoredPath, target);
            await MoveStoredImageAsync(record, target, destination.Name, key);
        }
        finally { galleryStore.Gate.Release(); }
    }

    // The old copy is deleted by the drive queue after the catalog commits. Moving an image back onto a path
    // still queued for deletion cancels that delete and keeps the file instead of copying it again.
    private async Task MoveStoredImageAsync(ImageRecord record, string target, string newName, byte[] key)
    {
        var deleteOldCopy = CreateDriveOperation(DriveOperationKind.DeleteFile, record.StoredPath, record.Id, key);
        var queuedDelete = await galleryStore.FindPendingDriveOperationAsync(
            DriveOperationKind.DeleteFile, VaultCrypto.PathFingerprint(key, target));
        if (queuedDelete is not null)
        {
            if (queuedDelete.ImageId == record.Id && File.Exists(target))
            {
                await galleryStore.UpdateImageAndEnqueueAsync(record with { StoredPath = target, Name = newName }, key,
                    deleteOldCopy, queuedDelete.Sequence);
                return;
            }
            if (await DeleteQueuedFileAsync(galleryStore, queuedDelete.ImageId, target, key))
                await galleryStore.RemoveDriveOperationAsync(queuedDelete.Sequence);
        }

        var movedRecord = StoredImageMover.CopyToDestination(record, target, newName);
        try { await galleryStore.UpdateImageAndEnqueueAsync(movedRecord, key, deleteOldCopy); }
        catch
        {
            StoredImageMover.RollBackCopiedDestination(target);
            throw;
        }
    }

    public async Task<VisionSettings> SettingsAsync() =>
        await ReadEncryptedSettingAsync<VisionSettings>(VisionSettingsId, VisionSettingsId) ?? new();

    public async Task SaveSettingsAsync(VisionSettings settings)
    {
        VisionService.Validate(settings);
        await WriteEncryptedSettingAsync(settings, VisionSettingsId, VisionSettingsId);
    }

    private Task<ImageRecord> FindAsync(string id, byte[] key) => galleryStore.ReadRecordAsync(id, key);
}
