using Gallery.Core.Services.Processors.Codecs;
using System.Buffers.Binary;
using System.Text;
using Gallery.Core.Exceptions;
using Gallery.Core.Services.Queries;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.Processors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class OriginalStorageTests
{
    private static readonly byte[] TrailingSecret = "TRAILING-SECRET"u8.ToArray();

    // Every supported image format preserves its original bytes and media type.
    [Theory]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.webp", "image/webp")]
    [InlineData("photo.png", "image/png")]
    public async Task Import_WhenImageIsIngested_ThenStoresOriginalBytesUnchanged(string name, string contentType)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = MakeImage(contentType);

        // Act
        var record = await fixture.Service.ImportAsync(new MemoryStream(source), name, "folder");
        var stored = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), fixture.Session.Key, $"image:{record.Id}");
        var original = await fixture.Service.ViewerImageAsync(record.Id, ImageResolution.Original);

        // Assert
        Assert.Equal(source, stored);
        Assert.Equal(contentType, record.ContentType);
        Assert.Equal(record.Sha256, record.ContentSha256);
        Assert.StartsWith($"data:{contentType};base64,", original);
        Assert.Equal(source, Convert.FromBase64String(original[$"data:{contentType};base64,".Length..]));
        await fixture.Service.VerifyImportedAsync(record.Id);
    }

    [Fact]
    public async Task Import_WhenFormatIsNotPngJpegOrWebP_ThenRejectsIt()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        using var image = new Image<Rgba32>(8, 8, new Rgba32(10, 20, 30));
        using var bmp = new MemoryStream();
        image.SaveAsBmp(bmp);

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream(bmp.ToArray()), "disguised.png", "folder"));

        // Assert
        AsserterExceptionContract.AssertMessageContains(["BMP", "PNG", "JPEG", "WebP"], error.Message);
    }

    [Fact]
    public async Task Export_WhenPngHasPrivateChunkCompressedTextAndTrailingData_ThenCleansWithoutReencoding()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var clean = MakeCompressedTextPng();
        var source = AppendTrailing(InsertPngChunkBeforeIend(clean, "prVt", "private"u8.ToArray()));
        var record = await fixture.Service.ImportAsync(new MemoryStream(source), "comfy.png", "folder");

        // Act
        var export = await fixture.Service.ExportAsync(record.Id);
        var exported = export.CopyContentBytes();

        // Assert
        Assert.Equal(("comfy.png", "image/png"), (export.FileName, export.ContentType));
        Assert.DoesNotContain("prVt", PngChunkTypes(exported));
        Assert.DoesNotContain("zTXt", PngChunkTypes(exported));
        Assert.Equal("IEND", PngChunkTypes(exported)[^1]);
        Assert.Equal(-1, exported.AsSpan().IndexOf(TrailingSecret));
        Assert.Equal(PngIdat(clean), PngIdat(exported));
        using var image = Image.Load(exported);
        var actualPromptValues = image.Metadata.GetPngMetadata().TextData
            .Where(text => text.Keyword == "prompt").Select(text => text.Value).ToArray();
        Assert.Equal(["a lighthouse"], actualPromptValues);
    }

    [Fact]
    public async Task Export_WhenJpegHasPrivateSegmentAndTrailingData_ThenKeepsJpegAndCleansIt()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var clean = MakeImage("image/jpeg");
        byte[] privateSegment = [0xFF, 0xE5, 0x00, 0x09, .. "PRIVATE"u8];
        var source = AppendTrailing([.. clean[..2], .. privateSegment, .. clean[2..]]);
        var record = await fixture.Service.ImportAsync(new MemoryStream(source), "photo.jpg", "folder");

        // Act
        var export = await fixture.Service.ExportAsync(record.Id);
        var exported = export.CopyContentBytes();

        // Assert
        Assert.Equal(("photo.jpg", "image/jpeg"), (export.FileName, export.ContentType));
        Assert.Equal(clean, exported);
    }

    [Fact]
    public async Task Export_WhenWebPHasUnknownChunkAndTrailingData_ThenKeepsWebPAndCleansIt()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var clean = MakeImage("image/webp");
        var withChunk = new byte[clean.Length + 12];
        clean.CopyTo(withChunk, 0);
        "PRIV"u8.CopyTo(withChunk.AsSpan(clean.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(withChunk.AsSpan(clean.Length + 4), 4);
        "data"u8.CopyTo(withChunk.AsSpan(clean.Length + 8));
        BinaryPrimitives.WriteUInt32LittleEndian(withChunk.AsSpan(4), (uint)(withChunk.Length - 8));
        var record = await fixture.Service.ImportAsync(new MemoryStream(AppendTrailing(withChunk)), "photo.webp", "folder");

        // Act
        var export = await fixture.Service.ExportAsync(record.Id);
        var exported = export.CopyContentBytes();

        // Assert
        Assert.Equal(("photo.webp", "image/webp"), (export.FileName, export.ContentType));
        Assert.Equal(clean, exported);
    }

    [Fact]
    public async Task Export_WhenMp4HasPrivateBoxesPaddingAndTrailingData_ThenBlanksThemWithoutMovingMediaBoxes()
    {
        // Arrange
        var ftyp = Mp4Box("ftyp", "isom\0\0\0\0isom"u8.ToArray());
        var vendor = Mp4Box("uuid", "VENDOR-SECRET-16"u8.ToArray());
        var padding = Mp4Box("free", "LEFTOVER"u8.ToArray());
        var mdat = Mp4Box("mdat", "frames"u8.ToArray());
        var moov = Mp4Box("moov", Mp4Box("udta", "workflow"u8.ToArray()));
        var clean = (byte[])[.. ftyp, .. vendor, .. padding, .. mdat, .. moov];
        byte[] source = [.. clean, .. TrailingSecret];

        using var transport = new MediaProcessTransport(
            """{"streams":[{"codec_type":"video","width":32,"height":24,"duration":"1"}],"format":{"format_name":"mov,mp4","duration":"1"}}""");
        using var fixture = new TestVault(transport.VideoTools);
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(source), "clip.mp4", "folder");

        // Act
        var export = await fixture.Service.ExportAsync(record.Id);

        // Assert - gather the exported bytes
        var exported = export.CopyContentBytes();

        // Assert - every box keeps its size and position so sample offsets stay valid
        Assert.Equal(clean.Length, exported.Length);
        Assert.Equal([.. ftyp], exported[..ftyp.Length]);
        Assert.Equal([.. mdat, .. moov], exported[^(mdat.Length + moov.Length)..]);
        var blanked = exported[ftyp.Length..^(mdat.Length + moov.Length)];
        Assert.Equal([.. BlankFreeBox(vendor.Length), .. BlankFreeBox(padding.Length)], blanked);
    }

    private static byte[] Mp4Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] BlankFreeBox(int size) => Mp4Box("free", new byte[size - 8]);

    private static byte[] MakeImage(string contentType)
    {
        using var image = new Image<Rgba32>(40, 24, new Rgba32(200, 80, 40));
        using var output = new MemoryStream();
        if (contentType == "image/jpeg") image.SaveAsJpeg(output);
        else if (contentType == "image/webp") image.SaveAsWebp(output);
        else image.SaveAsPng(output);
        return output.ToArray();
    }

    private static byte[] MakeCompressedTextPng()
    {
        using var image = new Image<Rgba32>(40, 24, new Rgba32(200, 80, 40));
        image.Metadata.GetPngMetadata().TextData.Add(new PngTextData("prompt", "a lighthouse", "", ""));
        using var output = new MemoryStream();
        image.SaveAsPng(output, new PngEncoder { TextCompressionThreshold = 0 });
        var png = output.ToArray();
        Assert.Contains("zTXt", PngChunkTypes(png));
        return png;
    }

    private static byte[] AppendTrailing(byte[] content) => [.. content, .. TrailingSecret];

    private static byte[] InsertPngChunkBeforeIend(byte[] png, string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, chunk.AsSpan(4));
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        var iend = png.Length - 12;
        return [.. png[..iend], .. chunk, .. png[iend..]];
    }

    private static List<string> PngChunkTypes(byte[] png)
    {
        var types = new List<string>();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            types.Add(Encoding.ASCII.GetString(png, offset + 4, 4));
            offset += 12 + length;
        }
        return types;
    }

    private static byte[] PngIdat(byte[] png)
    {
        using var idat = new MemoryStream();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            if (Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT") idat.Write(png, offset + 8, length);
            offset += 12 + length;
        }
        return idat.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
