using System.Security.Cryptography;
using Gallery.Core.Features.Ingestion.Metadata;
using Gallery.Core.Features.Media.Export;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Shared.Validation;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp;

namespace Gallery.Core.Features.Media;

internal sealed class GalleryMediaReader(GalleryLifetime galleryLifetime, VaultSession vaultSession) : IGalleryMedia
{
    private const string PngDataUrlPrefix = "data:image/png;base64,";

    public async Task<string> ThumbnailAsync(string id)
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await ReadThumbnailAsync(galleryLifetime.Current.GalleryStore, id, key.Bytes);
    }

    private static async Task<string> ReadThumbnailAsync(IGalleryStore store, string id, byte[] key)
    {
        using var thumbnail = await store.ReadThumbnailAsync(id, key);
        return PngDataUrlPrefix + Convert.ToBase64String(thumbnail.Content.Span);
    }

    public async Task<string> PreviewAsync(string id, ImageResolution resolution = ImageResolution.Fit)
    {
        OperationChoiceValidator.Validate(resolution);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        var record = await store.ReadRecordAsync(id, key.Bytes);
        return await ReadPreviewAsync(store, record, key.Bytes, resolution);
    }

    internal static async Task<string> ReadPreviewAsync(
        IGalleryStore store, ImageRecord record, byte[] key, ImageResolution resolution)
    {
        if (record.IsVideo) return await ReadThumbnailAsync(store, record.Id, key);
        return await CreatePreviewAsync(await StoredMediaProcessor.ReadAsync(record, key), resolution);
    }

    public async Task<string> ViewerImageAsync(string id, ImageResolution resolution)
    {
        OperationChoiceValidator.Validate(resolution);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        var record = await store.ReadRecordAsync(id, key.Bytes);
        if (record.IsVideo) return await ReadThumbnailAsync(store, id, key.Bytes);
        return await CreateViewerImageAsync(record,
            await StoredMediaProcessor.ReadAsync(record, key.Bytes), resolution);
    }

    public async Task<(Stream Content, string ContentType)> OpenMediaAsync(string id)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var record = await galleryLifetime.Current.GalleryStore.ReadRecordAsync(id, keyLease.Bytes);
        var plaintext = await StoredMediaProcessor.ReadAsync(record, keyLease.Bytes);
        return (new MemoryStream(plaintext, writable: false), record.ContentType);
    }

    public async Task<ExportFile> ExportAsync(string id)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var record = await galleryLifetime.Current.GalleryStore.ReadRecordAsync(id, keyLease.Bytes);
        return await ExportRecordAsync(record, keyLease.Bytes);
    }

    public async Task<ExportFile> ExportZipAsync(IEnumerable<string> ids)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        return await ExportComposer.ComposeZipAsync(ExportSelectionAsync(galleryLifetime.Current.GalleryStore, ids, keyLease.Bytes));
    }

    private static async IAsyncEnumerable<ExportFile> ExportSelectionAsync(IGalleryStore store, IEnumerable<string> ids, byte[] key)
    {
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            yield return await ExportRecordAsync(await store.ReadRecordAsync(id, key), key);
    }

    private static async Task<ExportFile> ExportRecordAsync(ImageRecord record, byte[] key)
    {
        var plaintext = await StoredMediaProcessor.ReadAsync(record, key);
        try { return ExportComposer.Export(record, plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task<ExportFile?> WorkflowAsync(string id)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var record = await galleryLifetime.Current.GalleryStore.ReadRecordAsync(id, keyLease.Bytes);
        var workflow = ComfyMetadata.WorkflowForExport(record.Metadata);
        return ExportComposer.ComposeWorkflow(record, workflow);
    }

    private static async Task<string> CreatePreviewAsync(
        byte[] plainImage, ImageResolution resolution)
    {
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

    private static async Task<string> CreateViewerImageAsync(
        ImageRecord record, byte[] plainImage, ImageResolution resolution)
    {
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
}
