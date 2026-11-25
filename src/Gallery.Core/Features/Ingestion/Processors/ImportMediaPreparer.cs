using Gallery.Core.Features.Media.Processors;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Ingestion.Metadata;
using Gallery.Core.Features.Ingestion.Gateways;
using Gallery.Core.Features.Ingestion.Managers.Reporters;
using Gallery.Core.Features.Ingestion.Managers.Scanners;
using Gallery.Core.Features.Ingestion.Managers.Validators;
using Gallery.Core.Features.Media;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Features.Diagnostics.Models;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp;

namespace Gallery.Core.Features.Ingestion.Processors;

internal sealed class ImportMediaPreparer(VideoTools? videoTools)
{
    private const string ImagesStorageDirectoryName = "images";
    private const int ThumbnailMaxDimensionPixels = 384;

    public const long MaxFileBytes = 50 * 1024 * 1024;
    public const long MaxVideoBytes = 256 * 1024 * 1024;
    public bool VideoToolsAvailable => videoTools?.IsAvailable == true;

    public static async Task<PreparedImport> PrepareImageAsync(
        MemoryStream original, string name, string sourceFolder, string sourcePath, string sourceHash, byte[] key,
        IngestionTimings? timings, string mediaRoot)
    {
        var stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        original.Position = 0;
        using var image = await DecodeImportImageAsync(original);
        ImportMediaValidator.ValidateImageDimensions(image.Width, image.Height);
        var contentType = ImportMediaValidator.RequireSupportedContentType(image.Metadata.DecodedImageFormat);
        ImportMediaValidator.ValidateImageFrames(image.Frames.Count);
        var metadata = SerializeImageMetadata(image);
        timings?.Record(IngestionStage.Decode, stageStart);
        stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var thumbnailBytes = await CreateThumbnailAsync(image);
        timings?.Record(IngestionStage.Thumbnail, stageStart);
        var record = CreateImageRecord(
            image, original.Length, name, sourceFolder, sourcePath, sourceHash, contentType, metadata, mediaRoot);
        try
        {
            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var encrypted = StoredMediaProcessor.EncryptImport(record,
                original.GetBuffer().AsSpan(0, (int)original.Length), thumbnailBytes, key);
            timings?.Record(IngestionStage.Encrypt, stageStart);
            return new PreparedImport(record,
                ImmutableCollectionsMarshal.AsImmutableArray(encrypted.EncryptedMedia),
                ImmutableCollectionsMarshal.AsImmutableArray(encrypted.EncryptedThumbnail));
        }

        finally { CryptographicOperations.ZeroMemory(thumbnailBytes); }
    }

    private static string SerializeImageMetadata(Image image) =>
        JsonSerializer.Serialize(new
        {
            png = image.Metadata.GetPngMetadata().TextData.Select(chunk => new { chunk.Keyword, chunk.Value }),
            exif = image.Metadata.ExifProfile?.Values.Select(value => $"{value.Tag}: {value.GetValue()}")
        });

    private static ImageRecord CreateImageRecord(
        Image image, long originalBytes, string name, string sourceFolder, string sourcePath,
        string sourceHash, string contentType, string metadata, string mediaRoot)
    {
        var record = new ImageRecord
        {
            Name = Path.GetFileNameWithoutExtension(name),
            OriginalName = Path.GetFileName(name),
            SourceFolder = sourceFolder,
            SourcePath = sourcePath,
            Width = image.Width,
            Height = image.Height,
            OriginalBytes = originalBytes,
            Sha256 = sourceHash,
            ContentSha256 = sourceHash,
            ContentType = contentType,
            Metadata = metadata,
            Generation = ComfyMetadata.Extract(metadata),
            MetadataVersion = ComfyMetadata.Version
        };
        return record with
        {
            StoredPath = Path.Combine(mediaRoot, ImagesStorageDirectoryName, record.Id[..2],
                record.Id[2..4], record.Id + StoredMediaProcessor.EncryptedPngExtension)
        };
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
    /// ffprobe/ffmpeg need a file, so the plaintext MP4 is written briefly into a private temp directory
    /// that is deleted as soon as the metadata and first frame are read.
    /// </summary>
    public async Task<PreparedImport> PrepareVideoAsync(
        ReadOnlyMemory<byte> bytes, string name, string sourceFolder, string sourcePath, string sourceHash, byte[] key,
        IngestionTimings? timings, string mediaRoot)
    {
        var tools = RequiredVideoTools();
        var stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var temporaryDirectory = CreatePrivateTemporaryVideoDirectory();
        var temporaryVideoPath = Path.Combine(temporaryDirectory, "source" + IngestionSourceScanner.Mp4Extension);
        byte[]? videoFrameBytes = null;
        VideoTools.ProbeResult probe;
        try
        {
            await WriteTemporaryVideoAsync(temporaryVideoPath, bytes);
            probe = await tools.ProbeAsync(temporaryVideoPath);
            ImportMediaValidator.ValidateVideo(probe);
            videoFrameBytes = await tools.FirstFrameAsync(temporaryVideoPath, probe.StreamIndex);
        }
        finally
        {
            try
            {
                DeleteTemporaryVideo(temporaryVideoPath);
                DeleteTemporaryVideoDirectory(temporaryDirectory);
            }
            catch
            {
                if (videoFrameBytes is not null) CryptographicOperations.ZeroMemory(videoFrameBytes);
                throw;
            }
        }
        timings?.Record(IngestionStage.VideoProbe, stageStart);
        stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var thumbnailBytes = await CreateVideoThumbnailAsync(videoFrameBytes!);
        timings?.Record(IngestionStage.Thumbnail, stageStart);
        try
        {
            var record = CreateVideoRecord(probe, bytes.Length, name, sourceFolder, sourcePath, sourceHash, mediaRoot);
            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var encrypted = StoredMediaProcessor.EncryptImport(record, bytes.Span, thumbnailBytes, key);
            timings?.Record(IngestionStage.Encrypt, stageStart);
            return new PreparedImport(record,
                ImmutableCollectionsMarshal.AsImmutableArray(encrypted.EncryptedMedia),
                ImmutableCollectionsMarshal.AsImmutableArray(encrypted.EncryptedThumbnail));
        }
        finally { CryptographicOperations.ZeroMemory(thumbnailBytes); }
    }

    private VideoTools RequiredVideoTools() =>
        videoTools is not null && videoTools.IsAvailable ? videoTools
            : throw new VideoToolUnavailableException(VideoTools.MissingMessage, LogEventGallery.PrepareVideo,
                new ContextualData().Add("Video.RequiredTools", "ffmpeg, ffprobe"));

    internal static string CreatePrivateTemporaryVideoDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "gallery-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static FileStream OpenPrivateTemporaryVideoFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task WriteTemporaryVideoAsync(string path, ReadOnlyMemory<byte> bytes)
    {
        await using var fileStream = OpenPrivateTemporaryVideoFile(path);
        await fileStream.WriteAsync(bytes);
    }

    private static void DeleteTemporaryVideoDirectory(string path)
    {
        try { Directory.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The temporary decrypted video directory could not be deleted from '{path}'. Remove it manually.", exception);
        }
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

    private static ImageRecord CreateVideoRecord(VideoTools.ProbeResult probe, int contentLength,
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
            ContentSha256 = sourceHash,
            Metadata = metadata,
            MediaType = "video",
            ContentType = "video/mp4",
            DurationSeconds = probe.DurationSeconds,
            Generation = ComfyMetadata.Extract(metadata),
            MetadataVersion = ComfyMetadata.Version
        };
        record = record with
        {
            StoredPath = Path.Combine(mediaRoot, ImagesStorageDirectoryName, record.Id[..2], record.Id[2..4], record.Id + StoredMediaProcessor.EncryptedMp4Extension)
        };
        return record;
    }

    private static async Task<byte[]> CreateThumbnailAsync(Image image)
    {
        using var thumbnail = image.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new Size(ThumbnailMaxDimensionPixels, ThumbnailMaxDimensionPixels), Mode = ResizeMode.Max
        }));
        thumbnail.Metadata.ExifProfile = null;
        thumbnail.Metadata.IccProfile = null;
        thumbnail.Metadata.XmpProfile = null;
        thumbnail.Metadata.GetPngMetadata().TextData.Clear();
        using var memoryStream = new MemoryStream();
        await thumbnail.SaveAsPngAsync(memoryStream, new PngEncoder
        {
            SkipMetadata = true, CompressionLevel = PngCompressionLevel.Level1
        });
        var thumbnailBytes = memoryStream.ToArray();
        CryptographicOperations.ZeroMemory(memoryStream.GetBuffer());
        return thumbnailBytes;
    }
}
