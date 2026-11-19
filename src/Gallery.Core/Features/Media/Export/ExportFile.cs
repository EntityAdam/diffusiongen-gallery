using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Gallery.Core.Models;

public sealed record ExportFile
{
    private readonly byte[] contentBytes;

    public ExportFile(string fileName, string contentType, ReadOnlySpan<byte> contentBytes)
    {
        FileName = fileName;
        ContentType = contentType;
        this.contentBytes = contentBytes.ToArray();
    }

    public string FileName { get; init; }
    public string ContentType { get; init; }
    [JsonPropertyName("Content")]
    public ImmutableArray<byte> ContentBytes => ImmutableArray.CreateRange(contentBytes);
    public byte[] CopyContentBytes() => contentBytes.ToArray();
    public void ClearContent() => CryptographicOperations.ZeroMemory(contentBytes);
}
