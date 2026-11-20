using System.Security.Cryptography;

namespace Gallery.Core.Infrastructure.Persistence;

/// <summary>Owns one decoded thumbnail; disposal clears its buffer before releasing ownership.</summary>
internal sealed class ImageThumbnail(byte[] ownedPngBytes) : IDisposable
{
    private byte[]? ownedPngBytes = ownedPngBytes;
    public ReadOnlyMemory<byte> Content => ownedPngBytes ?? throw new ObjectDisposedException(nameof(ImageThumbnail));

    internal Stream OpenStream()
    {
        var bytes = Interlocked.Exchange(ref ownedPngBytes, null) ?? throw new ObjectDisposedException(nameof(ImageThumbnail));
        return new ThumbnailStream(bytes);
    }

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref ownedPngBytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }

    private sealed class ThumbnailStream : MemoryStream
    {
        private byte[]? bytes;

        internal ThumbnailStream(byte[] bytes) : base(bytes, writable: false) => this.bytes = bytes;

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref bytes, null) is { } owned)
                CryptographicOperations.ZeroMemory(owned);
            base.Dispose(disposing);
        }
    }
}
