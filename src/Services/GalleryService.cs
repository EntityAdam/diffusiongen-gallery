using System.Collections.Immutable;
using Gallery.Services.Validators;
using Gallery.Services.Processors;
using Gallery.Services.DataManagers.Models;
using Gallery.Core.Extractors;
using Gallery.Core.Processors;
using Gallery.Managers.Gateways;
using Gallery.Services.DataManagers;
using Gallery.Services.Sessions;
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
                    return PngDataUrlPrefix + Convert.ToBase64String(plainImage);

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
        await galleryStore.Gate.WaitAsync();
        try
        {
            await RecoverInterruptedRemovalsAsync(key.Bytes);
            await BackfillFingerprintsAsync(key.Bytes);
        }
        finally { galleryStore.Gate.Release(); }
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

        var imageRoot = Path.Combine(vaultStore.Root, "images");
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
        await galleryStore.Gate.WaitAsync();
        try
        {
            foreach (var staleRecordIndex in staleRecordIndices)
            {
                ImageRecord record;
                int index;
                (record, index) = staleRecordIndex;
                try
                {
                    var original = await FindAsync(record.Id, key);
                    var edit = new ImageRecordEdit(original)
                    {
                        Generation = ComfyMetadata.Extract(original.Metadata),
                        MetadataVersion = ComfyMetadata.Version
                    };
                    var refreshedImageRecord = edit.ToRecord(original);
                    await galleryStore.UpdateAsync(refreshedImageRecord, key);
                    records[index] = refreshedImageRecord;
                }
                catch (Exception exception) when (exception is InvalidOperationException or CryptographicException or StoredMetadataFormatException)
                {
                    throw new InvalidOperationException($"Could not backfill generation metadata for image '{record.Id}'.", exception);
                }
            }
        }
        finally { galleryStore.Gate.Release(); }
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
        return await ImportWithKeyAsync(input, name, sourceFolder, sourcePath, key.Bytes);
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
        string sourcePath, byte[] key)
    {
        var isVideo = IsVideoName(name);
        using var original = await ImportContentReader.ReadAsync(input,
            isVideo ? MaxVideoBytes : MaxFileBytes,
            isVideo ? "Video exceeds the 256 MiB limit." : "Image exceeds the 50 MiB limit.");
        var bytes = original.GetBuffer().AsMemory(0, (int)original.Length);
        try
        {
            var sourceHash = Convert.ToHexString(SHA256.HashData(bytes.Span));
            await RejectDuplicateImportAsync(sourceHash, name, key);
            var prepared = isVideo
                ? await PrepareVideoAsync(bytes, name, sourceFolder, sourcePath, sourceHash, key)
                : await PrepareImageAsync(original, name, sourceFolder, sourcePath, sourceHash, key);
            return await PersistImportAsync(prepared.Record, prepared.Encrypted, prepared.EncryptedThumb, sourceHash, name, key);
        }
        finally { CryptographicOperations.ZeroMemory(original.GetBuffer()); }
    }

    private async Task RejectDuplicateImportAsync(string sourceHash, string name, byte[] key)
    {
        await galleryStore.Gate.WaitAsync();
        try { await RejectKnownFingerprintAsync(sourceHash, name, key); }
        finally { galleryStore.Gate.Release(); }
    }

    private async Task RejectKnownFingerprintAsync(string sourceHash, string name, byte[] key)
    {
        if (await galleryStore.ContainsFingerprintAsync(sourceHash, key))
            throw new InvalidOperationException($"'{name}' is already in the gallery.");
    }

    private async Task<ImageRecord> PersistImportAsync(
        ImageRecord record, byte[] encrypted, byte[] encryptedThumbnail, string sourceHash, string name, byte[] key)
    {
        await galleryStore.Gate.WaitAsync();
        try
        {
            await RejectKnownFingerprintAsync(sourceHash, name, key);
            await WriteImportedCiphertextAsync(record.StoredPath, encrypted);
            await InsertImportedCatalogAsync(record, encryptedThumbnail, key);
            return record;
        }
        finally { galleryStore.Gate.Release(); }
    }

    private static async Task WriteImportedCiphertextAsync(string storedPath, byte[] encrypted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
        await using var output = new FileStream(storedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(encrypted);
        output.Flush(flushToDisk: true);
    }

    private async Task InsertImportedCatalogAsync(ImageRecord record, byte[] encryptedThumbnail, byte[] key)
    {
        try
        {
            var thumbnailBytes = VaultCrypto.Decrypt(encryptedThumbnail, key, $"thumbnail:{record.Id}");
            try { await galleryStore.InsertAsync(record, new ImageThumbnail(thumbnailBytes), key); }
            finally { CryptographicOperations.ZeroMemory(thumbnailBytes); }
        }
        catch
        {
            File.Delete(record.StoredPath);
            throw;
        }
    }

    private async Task<(ImageRecord Record, byte[] Encrypted, byte[] EncryptedThumb)> PrepareImageAsync(
        MemoryStream original, string name, string sourceFolder, string sourcePath, string sourceHash, byte[] key)
    {
        original.Position = 0;
        var info = await Image.IdentifyAsync(original);
        ImportMediaValidator.ValidateImageDimensions(info.Width, info.Height);
        original.Position = 0;
        using var image = await Image.LoadAsync(original);
        ImportMediaValidator.ValidateImageFrames(image.Frames.Count);
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
            StoredPath = Path.Combine(vaultStore.Root, "images", record.Id[..2], record.Id[2..4], record.Id + EncryptedPngExtension)
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
        var tools = RequiredVideoTools();
        var video = await ReadTemporaryVideoAsync(bytes, tools);
        var thumbnailBytes = await CreateVideoThumbnailAsync(video.FrameBytes);
        try
        {
            var record = CreateVideoRecord(video.Probe, bytes.Length, name, sourceFolder, sourcePath, sourceHash);
            return EncryptPreparedVideo(record, bytes, thumbnailBytes, key);
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
        string name, string sourceFolder, string sourcePath, string sourceHash)
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
            StoredPath = Path.Combine(vaultStore.Root, "images", record.Id[..2], record.Id[2..4], record.Id + EncryptedMp4Extension)
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
            Action<string>? progress, Func<ImportProgress, Task>? reportProgress)
        {
            var progressGate = new SemaphoreSlim(1);
            int completedCount = 0;
            int importedCount = 0;
            int failedCount = 0;
            var failuresByPathIndex = new Exception?[imagePaths.Length];
            try
            {
                await ReportAsync(new ImportProgress(imagePaths.Length, 0, 0, 0, "Importing images..."));
                await ExecuteImportsAsync();
                await ReportCompletionAsync();
                ThrowImportFailures(folder, failuresByPathIndex);
            }
            finally { progressGate.Dispose(); }

            Task ExecuteImportsAsync() => Parallel.ForEachAsync(Enumerable.Range(0, imagePaths.Length),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(2, Environment.ProcessorCount) },
                async (pathIndex, _) => await ImportOneAsync(pathIndex));

            Task ReportCompletionAsync() => ReportAsync(new ImportProgress(imagePaths.Length,
                completedCount, importedCount, failedCount,
                imagePaths.Length == 0 ? "No supported images found." : "Ingestion complete."));

            async Task ImportOneAsync(int pathIndex)
            {
                var path = imagePaths[pathIndex];
                var name = Path.GetFileName(path);
                await AnnounceImportAsync(name);

                var imageWasImportedSuccessfully = false;
                await ExecuteFileImportAsync();
                await AccountImportAsync(pathIndex, name, imageWasImportedSuccessfully);

                async Task ExecuteFileImportAsync()
                {
                try
                {
                    ImageRecord imported;
                    ValidateImportName(name);
                    await using (var input = File.OpenRead(path))
                    {
                        imported = await galleryService.ImportWithKeyAsync(input, name, Path.GetDirectoryName(path)!, path,
                            key);
                        imageWasImportedSuccessfully = true;
                        if (sourceRetention == SourceRetention.DeleteAfterVerification)
                        {
                            await ReportAsync(new ImportProgress(imagePaths.Length, completedCount, importedCount, failedCount,
                                $"Verifying {name} before source deletion..."));
                            await galleryService.VerifyImportedWithKeyAsync(imported.Id, key);
                        }
                    }
                    if (sourceRetention == SourceRetention.DeleteAfterVerification)
                        await DeleteVerifiedSourceAsync(path, imported.Sha256);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException
                    or UnauthorizedAccessException or GalleryException or CryptographicException or UnknownImageFormatException
                    or InvalidImageContentException)
                {
                    failuresByPathIndex[pathIndex] = new IOException($"Failed to import '{path}'.", exception);
                }

                }
            }

            async Task AnnounceImportAsync(string name)
            {
                await progressGate.WaitAsync();
                try { progress?.Invoke(name); }
                finally { progressGate.Release(); }
                await ReportAsync(new ImportProgress(imagePaths.Length, completedCount, importedCount, failedCount,
                    $"Importing {name}..."));

            }

            async Task AccountImportAsync(int pathIndex, string name, bool imageWasImportedSuccessfully)
            {
                await progressGate.WaitAsync();
                try
                {
                    completedCount++;
                    if (imageWasImportedSuccessfully) importedCount++;
                    if (failuresByPathIndex[pathIndex] is not null) failedCount++;
                    if (reportProgress is not null)
                        await reportProgress(new ImportProgress(imagePaths.Length, completedCount, importedCount, failedCount,
                            $"Processed {name}"));
                }
                finally { progressGate.Release(); }
            }

            async Task ReportAsync(ImportProgress update)
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
    }

    public async Task ImportFolderAsync(string folder, FolderSearchScope searchScope, Action<string>? progress = null,
        SourceRetention sourceRetention = SourceRetention.Retain, Func<ImportProgress, Task>? reportProgress = null)
    {
        ValidateOperationChoice(searchScope);
        ValidateOperationChoice(sourceRetention);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing local folder.");
        if (reportProgress is not null)
            await reportProgress(new ImportProgress(null, 0, 0, 0, "Scanning selected folder..."));
        var imagePaths = await Task.Run(() => FolderImportProcessor.DiscoverImagePaths(folder, searchScope));
        await galleryStore.Gate.WaitAsync();
        try { await BackfillFingerprintsAsync(key.Bytes); }
        finally { galleryStore.Gate.Release(); }
        await new FolderImportProcessor(this).ImportAsync(
            folder, imagePaths, key.Bytes, sourceRetention, progress, reportProgress);
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
        var thumbnail = (await galleryStore.ReadThumbnailAsync(id, key)).PngBytes;
        try { using var image = Image.Load(thumbnail); }
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
        var destination = StoredImageMover.NormalizeDestination(folder, name);
        StoredImageMoveValidator.ValidateDestination(destination);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await MoveUnderGateAsync(id, destination, key.Bytes);
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

    private async Task MoveStoredImageAsync(ImageRecord record, string target, string newName, byte[] key)
    {
        var movedRecord = StoredImageMover.CopyToDestination(record, target, newName);
        try { await galleryStore.UpdateAsync(movedRecord, key); }
        catch
        {
            StoredImageMover.RollBackCopiedDestination(target);
            throw;
        }
        StoredImageMover.RemoveOldCopy(record.StoredPath, target);
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
