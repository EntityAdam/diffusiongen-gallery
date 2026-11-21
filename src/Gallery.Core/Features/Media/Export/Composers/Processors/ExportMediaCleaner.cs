using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Gallery.Core.Features.Media.Export.Composers.Processors;

/// <summary>
/// Losslessly cleans exported media without decoding or re-encoding pixels or video: standard and metadata
/// chunks/segments/boxes are copied, while unknown or private ones and any bytes after the media end are dropped
/// (MP4 boxes are blanked in place instead, to keep sample offsets valid).
/// Compressed PNG text is written as uncompressed text so ComfyUI can read the prompt and workflow.
/// </summary>
internal static class ExportMediaCleaner
{
    private const int MaxDecompressedTextBytes = 64 * 1024 * 1024;
    private const string PngFormatName = "PNG";
    private const string JpegFormatName = "JPEG";
    private const string WebPFormatName = "WebP";
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly HashSet<string> KeptPngChunks =
    [
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "cHRM", "gAMA", "iCCP", "sBIT", "sRGB", "cICP", "mDCV", "cLLI",
        "bKGD", "hIST", "pHYs", "sPLT", "eXIf", "tIME", "tEXt"
    ];

    private static readonly HashSet<string> KeptWebPChunks =
        ["VP8 ", "VP8L", "VP8X", "ALPH", "ANIM", "ANMF", "ICCP", "EXIF", "XMP "];

    private static readonly HashSet<string> KeptMp4Boxes =
        ["ftyp", "styp", "moov", "mdat", "moof", "mfra", "sidx", "ssix", "prft", "emsg", "meta", "pdin"];

    public static byte[] Clean(string contentType, ReadOnlySpan<byte> content)
    {
        using var memoryStream = new MemoryStream(content.Length);
        try
        {
            switch (contentType)
            {
                case "image/png": CleanPng(content, memoryStream); break;
                case "image/jpeg": CleanJpeg(content, memoryStream); break;
                case "image/webp": CleanWebP(content, memoryStream); break;
                case "video/mp4": CleanMp4(content, memoryStream); break;
                default: throw new InvalidOperationException($"Export cleaning does not support '{contentType}'.");
            }
            return memoryStream.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(memoryStream.GetBuffer()); }
    }

    private static void CleanPng(ReadOnlySpan<byte> content, MemoryStream memoryStream)
    {
        if (!content.StartsWith(PngSignature)) throw Malformed(PngFormatName);
        memoryStream.Write(PngSignature);
        for (var offset = PngSignature.Length; ;)
        {
            var pngChunk = ReadPngChunk(content, offset);
            var chunkData = content.Slice(offset + 8, pngChunk.DataLength);
            if (KeptPngChunks.Contains(pngChunk.Type))
                memoryStream.Write(content.Slice(offset, pngChunk.TotalLength));
            else if (pngChunk.Type == "zTXt") WritePngTextFromZText(chunkData, memoryStream);
            else if (pngChunk.Type == "iTXt") WriteUncompressedInternationalText(chunkData, memoryStream);
            if (pngChunk.Type == "IEND") return;
            offset += pngChunk.TotalLength;
        }
    }

    private static PngChunk ReadPngChunk(ReadOnlySpan<byte> content, int offset)
    {
        if (offset + 12 > content.Length) throw Malformed(PngFormatName);
        var length = BinaryPrimitives.ReadUInt32BigEndian(content[offset..]);
        if (length > int.MaxValue || offset + 12L + length > content.Length) throw Malformed(PngFormatName);
        return new(Encoding.ASCII.GetString(content.Slice(offset + 4, 4)), 12 + (int)length, (int)length);
    }

    private static void WritePngTextFromZText(ReadOnlySpan<byte> zTextChunkBytes, MemoryStream memoryStream)
    {
        var keywordEnd = GetValidatedZTextKeywordEnd(zTextChunkBytes);
        var text = Inflate(zTextChunkBytes[(keywordEnd + 2)..]);
        try
        {
            WriteUncompressedTextChunk(memoryStream, zTextChunkBytes[..(keywordEnd + 1)], text);
        }
        finally { CryptographicOperations.ZeroMemory(text); }
    }

    private static int GetValidatedZTextKeywordEnd(ReadOnlySpan<byte> zTextChunkBytes)
    {
        var keywordEnd = zTextChunkBytes.IndexOf((byte)0);
        if (!HasValidZTextHeader(zTextChunkBytes, keywordEnd)) throw Malformed(PngFormatName);
        return keywordEnd;
    }

    private static bool HasValidZTextHeader(ReadOnlySpan<byte> zTextChunkBytes, int keywordEnd) =>
        keywordEnd > 0 && keywordEnd + 2 <= zTextChunkBytes.Length && zTextChunkBytes[keywordEnd + 1] == 0;

    private static void WriteUncompressedInternationalText(ReadOnlySpan<byte> internationalTextChunkBytes, MemoryStream memoryStream)
    {
        var header = ParseInternationalTextHeader(internationalTextChunkBytes);
        var text = header.IsCompressed
            ? Inflate(internationalTextChunkBytes[header.TextStart..])
            : internationalTextChunkBytes[header.TextStart..].ToArray();
        try
        {
            WriteUncompressedInternationalTextChunk(
                memoryStream, internationalTextChunkBytes, header.TextStart, header.KeywordEnd, text);
        }
        finally { CryptographicOperations.ZeroMemory(text); }
    }

    private static InternationalTextHeader ParseInternationalTextHeader(ReadOnlySpan<byte> internationalTextChunkBytes)
    {
        var keywordEnd = internationalTextChunkBytes.IndexOf((byte)0);
        if (keywordEnd <= 0 || keywordEnd + 3 > internationalTextChunkBytes.Length) throw Malformed(PngFormatName);
        var isCompressed = internationalTextChunkBytes[keywordEnd + 1] == 1;
        var languageEnd = internationalTextChunkBytes[(keywordEnd + 3)..].IndexOf((byte)0);
        if (languageEnd < 0) throw Malformed(PngFormatName);
        var translatedStart = keywordEnd + 3 + languageEnd + 1;
        var translatedEnd = internationalTextChunkBytes[translatedStart..].IndexOf((byte)0);
        if (translatedEnd < 0) throw Malformed(PngFormatName);
        return new(translatedStart + translatedEnd + 1, isCompressed, keywordEnd);
    }

    private static void WriteUncompressedInternationalTextChunk(
        MemoryStream memoryStream, ReadOnlySpan<byte> internationalTextChunkBytes, int textStart, int keywordEnd,
        ReadOnlySpan<byte> text)
    {
        var chunk = new byte[textStart + text.Length];
        try
        {
            internationalTextChunkBytes[..textStart].CopyTo(chunk);
            chunk[keywordEnd + 1] = 0;
            chunk[keywordEnd + 2] = 0;
            text.CopyTo(chunk.AsSpan(textStart));
            WritePngChunk(memoryStream, "iTXt", chunk);
        }
        finally { CryptographicOperations.ZeroMemory(chunk); }
    }

    private static void WriteUncompressedTextChunk(
        MemoryStream memoryStream, ReadOnlySpan<byte> keyword, ReadOnlySpan<byte> text)
    {
        var chunk = new byte[keyword.Length + text.Length];
        try
        {
            keyword.CopyTo(chunk);
            text.CopyTo(chunk.AsSpan(keyword.Length));
            WritePngChunk(memoryStream, "tEXt", chunk);
        }
        finally { CryptographicOperations.ZeroMemory(chunk); }
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        var compressedCopy = compressed.ToArray();
        using var input = new MemoryStream(compressedCopy);
        using var zLibStream = new ZLibStream(input, CompressionMode.Decompress);
        using var text = new MemoryStream();
        try
        {
            var decompressedTextBytes = new byte[81920];
            for (int read; (read = zLibStream.Read(decompressedTextBytes)) > 0;)
            {
                if (text.Length + read > MaxDecompressedTextBytes)
                    throw new InvalidOperationException("Embedded PNG text is too large to export.");
                text.Write(decompressedTextBytes, 0, read);
            }
            return text.ToArray();
        }
        catch (InvalidDataException invalidDataException) { throw new InvalidOperationException("Embedded PNG text is corrupt.", invalidDataException); }
        finally
        {
            CryptographicOperations.ZeroMemory(compressedCopy);
            CryptographicOperations.ZeroMemory(text.GetBuffer());
        }
    }

    private static void WritePngChunk(MemoryStream memoryStream, string type, ReadOnlySpan<byte> chunkBytes)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)chunkBytes.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        memoryStream.Write(header);
        memoryStream.Write(chunkBytes);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, header[4..]), chunkBytes) ^ 0xFFFFFFFFu;
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
        memoryStream.Write(trailer);
    }

    private static void CleanJpeg(ReadOnlySpan<byte> content, MemoryStream memoryStream)
    {
        if (!HasJpegStartMarker(content)) throw Malformed(JpegFormatName);
        memoryStream.Write(content[..2]);
        var offset = 2;
        while (true)
        {
            var marker = ReadJpegMarker(content, ref offset);
            if (marker == 0xD9)
            {
                memoryStream.Write([0xFF, 0xD9]);
                return;
            }
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                memoryStream.Write([0xFF, marker]);
                continue;
            }
            var segmentOffset = offset;
            var length = ReadJpegSegmentLength(content, ref offset);
            if (IsKeptJpegSegment(marker))
            {
                memoryStream.Write([0xFF, marker]);
                memoryStream.Write(content.Slice(segmentOffset, length));
            }
            if (marker == 0xDA)
            {
                var scanEnd = EntropyCodedDataEnd(content, offset);
                memoryStream.Write(content[offset..scanEnd]);
                offset = scanEnd;
            }
        }
    }

    private static bool HasJpegStartMarker(ReadOnlySpan<byte> content) =>
        content.Length >= 4 && content[0] == 0xFF && content[1] == 0xD8;

    private static byte ReadJpegMarker(ReadOnlySpan<byte> content, ref int offset)
    {
        if (offset >= content.Length || content[offset] != 0xFF) throw Malformed(JpegFormatName);
        while (offset < content.Length && content[offset] == 0xFF) offset++;
        if (offset >= content.Length) throw Malformed(JpegFormatName);
        return content[offset++];
    }

    private static int ReadJpegSegmentLength(ReadOnlySpan<byte> content, ref int offset)
    {
        if (offset + 2 > content.Length) throw Malformed(JpegFormatName);
        var length = BinaryPrimitives.ReadUInt16BigEndian(content[offset..]);
        if (length < 2 || offset + length > content.Length) throw Malformed(JpegFormatName);
        offset += length;
        return length;
    }

    // APP0 (JFIF), APP1 (EXIF/XMP), APP2 (ICC) and APP14 (Adobe color transform) are standard; other APPn are private.
    private static bool IsKeptJpegSegment(byte marker) =>
        marker is not (>= 0xE0 and <= 0xEF) || marker is 0xE0 or 0xE1 or 0xE2 or 0xEE;

    private static int EntropyCodedDataEnd(ReadOnlySpan<byte> content, int offset)
    {
        for (var index = offset; index + 1 < content.Length; index++)
        {
            if (!DoesNotTerminateEntropyCodedScan(content[index], index + 1 < content.Length ? content[index + 1] : (byte)0))
                return index;
        }
        throw Malformed(JpegFormatName);
    }

    private static bool DoesNotTerminateEntropyCodedScan(byte current, byte next) =>
        current != 0xFF || next == 0x00 || next is >= 0xD0 and <= 0xD7 || next == 0xFF;

    private static void CleanWebP(ReadOnlySpan<byte> content, MemoryStream memoryStream)
    {
        if (!HasWebPRiffHeader(content)) throw Malformed(WebPFormatName);
        var riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(content[4..]);
        if (riffEnd > content.Length) throw Malformed(WebPFormatName);
        memoryStream.Write(content[..12]);
        for (var offset = 12; offset < riffEnd;)
        {
            if (offset + 8 > riffEnd) throw Malformed(WebPFormatName);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(content[(offset + 4)..]);
            var paddedEnd = offset + 8L + size + (size & 1);
            if (offset + 8L + size > riffEnd) throw Malformed(WebPFormatName);
            var chunkEnd = (int)Math.Min(paddedEnd, riffEnd);
            if (KeptWebPChunks.Contains(Encoding.ASCII.GetString(content.Slice(offset, 4))))
            {
                memoryStream.Write(content[offset..chunkEnd]);
                if (chunkEnd < paddedEnd) memoryStream.WriteByte(0);
            }
            offset = (int)paddedEnd;
        }
        var riffSize = memoryStream.Length - 8;
        if (riffSize > uint.MaxValue) throw Malformed(WebPFormatName);
        BinaryPrimitives.WriteUInt32LittleEndian(memoryStream.GetBuffer().AsSpan(4, 4), (uint)riffSize);
    }

    private static bool HasWebPRiffHeader(ReadOnlySpan<byte> content) =>
        content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WEBP"u8);

    // Sample tables hold absolute file offsets, so boxes are never removed or moved: unknown top-level boxes become
    // zero-filled 'free' boxes of the same size, free/skip padding is zeroed, and bytes after the last box are dropped.
    private static void CleanMp4(ReadOnlySpan<byte> content, MemoryStream memoryStream)
    {
        var offset = 0;
        while (offset + 8 <= content.Length)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(content[offset..]);
            var headerSize = 8;
            if (size == 1)
            {
                if (offset + 16 > content.Length) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(content[(offset + 8)..]);
                headerSize = 16;
            }
            else if (size == 0) size = content.Length - offset;
            if (size < headerSize || size > content.Length - offset) break;
            var box = content.Slice(offset, (int)size);
            var type = Encoding.ASCII.GetString(box.Slice(4, 4));
            if (KeptMp4Boxes.Contains(type)) memoryStream.Write(box);
            else
            {
                var start = (int)memoryStream.Length;
                memoryStream.SetLength(start + size);
                memoryStream.Position = memoryStream.Length;
                var blanked = memoryStream.GetBuffer().AsSpan(start, (int)size);
                blanked.Clear();
                box[..headerSize].CopyTo(blanked);
                "free"u8.CopyTo(blanked[4..]);
            }
            offset += (int)size;
        }
        if (memoryStream.Length == 0) throw Malformed("MP4");
    }

    private static InvalidOperationException Malformed(string format) =>
        new($"The stored {format} file is malformed and could not be cleaned for export.");

    private readonly record struct PngChunk(string Type, int TotalLength, int DataLength);
    private readonly record struct InternationalTextHeader(int TextStart, bool IsCompressed, int KeywordEnd);

    private static class Crc32
    {
        private static readonly uint[] crcValues = CreateTable();

        private static uint[] CreateTable()
        {
            var crcValues = new uint[256];
            for (uint index = 0; index < crcValues.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                crcValues[index] = value;
            }
            return crcValues;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes) crc = crcValues[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
