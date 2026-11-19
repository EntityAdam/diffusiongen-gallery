using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Ingestion.Metadata;
using Gallery.Core.Features.Ingestion.Video;
using Gallery.Core.Features.Media;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Shared.Diagnostics;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp;

namespace Gallery.Core.Features.Ingestion;

internal sealed class ImportMediaPreparer(VideoTools? videoTools)
{
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
            ContentSha256 = sourceHash,
            ContentType = contentType,
            Metadata = metadata,
            Generation = ComfyMetadata.Extract(metadata),
            MetadataVersion = ComfyMetadata.Version
        };
        record = record with
        {
            StoredPath = Path.Combine(mediaRoot, "images", record.Id[..2], record.Id[2..4], record.Id + StoredMediaProcessor.EncryptedPngExtension)
        };
        try
        {
            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var encrypted = StoredMediaProcessor.EncryptImport(record,
                original.GetBuffer().AsSpan(0, (int)original.Length), thumbnailBytes, key);
            timings?.Record(IngestionStage.Encrypt, stageStart);
            return new PreparedImport(record, encrypted.EncryptedMedia, encrypted.EncryptedThumbnail);
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
    public async Task<PreparedImport> PrepareVideoAsync(
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
            var encrypted = StoredMediaProcessor.EncryptImport(record, bytes.Span, thumbnailBytes, key);
            timings?.Record(IngestionStage.Encrypt, stageStart);
            return new PreparedImport(record, encrypted.EncryptedMedia, encrypted.EncryptedThumbnail);
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
        var temporaryVideoPath = Path.Combine(Path.GetTempPath(), "gallery-" + Guid.NewGuid().ToString("N") + IngestionSourceScanner.Mp4Extension);
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
            StoredPath = Path.Combine(mediaRoot, "images", record.Id[..2], record.Id[2..4], record.Id + StoredMediaProcessor.EncryptedMp4Extension)
        };
        return record;
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
}
