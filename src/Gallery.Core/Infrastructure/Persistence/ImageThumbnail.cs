using System.Security.Cryptography;

namespace Gallery.Core.Infrastructure.Persistence;

/// <summary>Owns one decoded thumbnail stream; disposal clears its buffer before releasing ownership.</summary>
internal sealed class ImageThumbnail(byte[] ownedPngBytes) : MemoryStream(ownedPngBytes, writable: false)
{
    private byte[]? ownedPngBytes = ownedPngBytes;
    public ReadOnlyMemory<byte> Content => ownedPngBytes ?? throw new ObjectDisposedException(nameof(ImageThumbnail));

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref ownedPngBytes, null) is { } bytes)
            CryptographicOperations.ZeroMemory(bytes);
        base.Dispose(disposing);
    }
}
