using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Gallery.Web.Services.Processors;

/// <summary>
/// Losslessly cleans exported media without decoding or re-encoding pixels or video: standard and metadata
/// chunks/segments/boxes are copied, while unknown or private ones and any bytes after the media end are dropped
/// (MP4 boxes are blanked in place instead, to keep sample offsets valid).
/// Compressed PNG text is written as uncompressed text so ComfyUI can read the prompt and workflow.
/// </summary>
internal static class ExportMediaCleaner
{
    private const int MaxDecompressedTextBytes = 64 * 1024 * 1024;
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
        using var output = new MemoryStream(content.Length);
        try
        {
            switch (contentType)
            {
                case ImageContentFormats.Png: CleanPng(content, output); break;
                case ImageContentFormats.Jpeg: CleanJpeg(content, output); break;
                case ImageContentFormats.WebP: CleanWebP(content, output); break;
                case ImageContentFormats.Mp4: CleanMp4(content, output); break;
                default: throw new InvalidOperationException($"Export cleaning does not support '{contentType}'.");
            }
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(output.GetBuffer()); }
    }

    private static void CleanPng(ReadOnlySpan<byte> content, MemoryStream output)
    {
        if (!content.StartsWith(PngSignature)) throw Malformed("PNG");
        output.Write(PngSignature);
        for (var offset = PngSignature.Length; ;)
        {
            if (offset + 12 > content.Length) throw Malformed("PNG");
            var length = BinaryPrimitives.ReadUInt32BigEndian(content[offset..]);
            if (length > int.MaxValue || offset + 12L + length > content.Length) throw Malformed("PNG");
            var type = Encoding.ASCII.GetString(content.Slice(offset + 4, 4));
            var data = content.Slice(offset + 8, (int)length);
            if (KeptPngChunks.Contains(type)) output.Write(content.Slice(offset, 12 + (int)length));
            else if (type == "zTXt") WritePngTextFromZText(data, output);
            else if (type == "iTXt") WriteUncompressedInternationalText(data, output);
            if (type == "IEND") return;
            offset += 12 + (int)length;
        }
    }

    private static void WritePngTextFromZText(ReadOnlySpan<byte> data, MemoryStream output)
    {
        var keywordEnd = data.IndexOf((byte)0);
        if (keywordEnd <= 0 || keywordEnd + 2 > data.Length || data[keywordEnd + 1] != 0) throw Malformed("PNG");
        var text = Inflate(data[(keywordEnd + 2)..]);
        try
        {
            var chunk = new byte[keywordEnd + 1 + text.Length];
            data[..(keywordEnd + 1)].CopyTo(chunk);
            text.CopyTo(chunk, keywordEnd + 1);
            WritePngChunk(output, "tEXt", chunk);
            CryptographicOperations.ZeroMemory(chunk);
        }
        finally { CryptographicOperations.ZeroMemory(text); }
    }

    private static void WriteUncompressedInternationalText(ReadOnlySpan<byte> data, MemoryStream output)
    {
        var keywordEnd = data.IndexOf((byte)0);
        if (keywordEnd <= 0 || keywordEnd + 3 > data.Length) throw Malformed("PNG");
        var compressed = data[keywordEnd + 1] == 1;
        var languageEnd = data[(keywordEnd + 3)..].IndexOf((byte)0);
        if (languageEnd < 0) throw Malformed("PNG");
        var translatedStart = keywordEnd + 3 + languageEnd + 1;
        var translatedEnd = data[translatedStart..].IndexOf((byte)0);
        if (translatedEnd < 0) throw Malformed("PNG");
        var textStart = translatedStart + translatedEnd + 1;
        var text = compressed ? Inflate(data[textStart..]) : data[textStart..].ToArray();
        try
        {
            var chunk = new byte[textStart + text.Length];
            data[..textStart].CopyTo(chunk);
            chunk[keywordEnd + 1] = 0;
            chunk[keywordEnd + 2] = 0;
            text.CopyTo(chunk, textStart);
            WritePngChunk(output, "iTXt", chunk);
            CryptographicOperations.ZeroMemory(chunk);
        }
        finally { CryptographicOperations.ZeroMemory(text); }
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        var compressedCopy = compressed.ToArray();
        using var input = new MemoryStream(compressedCopy);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress);
        using var text = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            for (int read; (read = inflater.Read(buffer)) > 0;)
            {
                if (text.Length + read > MaxDecompressedTextBytes)
                    throw new InvalidOperationException("Embedded PNG text is too large to export.");
                text.Write(buffer, 0, read);
            }
            return text.ToArray();
        }
        catch (InvalidDataException exception) { throw new InvalidOperationException("Embedded PNG text is corrupt.", exception); }
        finally
        {
            CryptographicOperations.ZeroMemory(compressedCopy);
            CryptographicOperations.ZeroMemory(text.GetBuffer());
        }
    }

    private static void WritePngChunk(MemoryStream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        output.Write(header);
        output.Write(data);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, header[4..]), data) ^ 0xFFFFFFFFu;
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
        output.Write(trailer);
    }

    private static void CleanJpeg(ReadOnlySpan<byte> content, MemoryStream output)
    {
        if (content.Length < 4 || content[0] != 0xFF || content[1] != 0xD8) throw Malformed("JPEG");
        output.Write(content[..2]);
        var offset = 2;
        while (true)
        {
            if (offset >= content.Length || content[offset] != 0xFF) throw Malformed("JPEG");
            while (offset < content.Length && content[offset] == 0xFF) offset++;
            if (offset >= content.Length) throw Malformed("JPEG");
            var marker = content[offset++];
            if (marker == 0xD9)
            {
                output.Write([0xFF, 0xD9]);
                return;
            }
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                output.Write([0xFF, marker]);
                continue;
            }
            if (offset + 2 > content.Length) throw Malformed("JPEG");
            var length = BinaryPrimitives.ReadUInt16BigEndian(content[offset..]);
            if (length < 2 || offset + length > content.Length) throw Malformed("JPEG");
            if (IsKeptJpegSegment(marker))
            {
                output.Write([0xFF, marker]);
                output.Write(content.Slice(offset, length));
            }
            offset += length;
            if (marker == 0xDA)
            {
                var scanEnd = EntropyCodedDataEnd(content, offset);
                output.Write(content[offset..scanEnd]);
                offset = scanEnd;
            }
        }
    }

    // APP0 (JFIF), APP1 (EXIF/XMP), APP2 (ICC) and APP14 (Adobe color transform) are standard; other APPn are private.
    private static bool IsKeptJpegSegment(byte marker) =>
        marker is not (>= 0xE0 and <= 0xEF) || marker is 0xE0 or 0xE1 or 0xE2 or 0xEE;

    private static int EntropyCodedDataEnd(ReadOnlySpan<byte> content, int offset)
    {
        for (var index = offset; index + 1 < content.Length; index++)
        {
            if (content[index] != 0xFF) continue;
            var next = content[index + 1];
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7 || next == 0xFF) continue;
            return index;
        }
        throw Malformed("JPEG");
    }

    private static void CleanWebP(ReadOnlySpan<byte> content, MemoryStream output)
    {
        if (content.Length < 12 || !content[..4].SequenceEqual("RIFF"u8) || !content.Slice(8, 4).SequenceEqual("WEBP"u8))
            throw Malformed("WebP");
        var riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(content[4..]);
        if (riffEnd > content.Length) throw Malformed("WebP");
        output.Write(content[..12]);
        for (var offset = 12; offset < riffEnd;)
        {
            if (offset + 8 > riffEnd) throw Malformed("WebP");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(content[(offset + 4)..]);
            var paddedEnd = offset + 8L + size + (size & 1);
            if (offset + 8L + size > riffEnd) throw Malformed("WebP");
            var chunkEnd = (int)Math.Min(paddedEnd, riffEnd);
            if (KeptWebPChunks.Contains(Encoding.ASCII.GetString(content.Slice(offset, 4))))
            {
                output.Write(content[offset..chunkEnd]);
                if (chunkEnd < paddedEnd) output.WriteByte(0);
            }
            offset = (int)paddedEnd;
        }
        var riffSize = output.Length - 8;
        if (riffSize > uint.MaxValue) throw Malformed("WebP");
        BinaryPrimitives.WriteUInt32LittleEndian(output.GetBuffer().AsSpan(4, 4), (uint)riffSize);
    }

    // Sample tables hold absolute file offsets, so boxes are never removed or moved: unknown top-level boxes become
    // zero-filled 'free' boxes of the same size, free/skip padding is zeroed, and bytes after the last box are dropped.
    private static void CleanMp4(ReadOnlySpan<byte> content, MemoryStream output)
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
            if (KeptMp4Boxes.Contains(type)) output.Write(box);
            else
            {
                var start = (int)output.Length;
                output.SetLength(start + size);
                output.Position = output.Length;
                var blanked = output.GetBuffer().AsSpan(start, (int)size);
                blanked.Clear();
                box[..headerSize].CopyTo(blanked);
                "free"u8.CopyTo(blanked[4..]);
            }
            offset += (int)size;
        }
        if (output.Length == 0) throw Malformed("MP4");
    }

    private static InvalidOperationException Malformed(string format) =>
        new($"The stored {format} file is malformed and could not be cleaned for export.");

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
