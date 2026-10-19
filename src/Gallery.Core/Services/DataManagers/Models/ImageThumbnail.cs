using System.Security.Cryptography;

namespace Gallery.Core.Services.DataManagers.Models;

/// <summary>Owns one decoded thumbnail; disposal clears its buffer before releasing ownership.</summary>
internal sealed class ImageThumbnail(byte[] ownedPngBytes) : IDisposable
{
    private byte[]? ownedPngBytes = ownedPngBytes;
    public ReadOnlyMemory<byte> Content => ownedPngBytes ?? throw new ObjectDisposedException(nameof(ImageThumbnail));

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref ownedPngBytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }
}
